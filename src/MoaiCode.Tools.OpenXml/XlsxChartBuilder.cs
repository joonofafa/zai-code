using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using C = DocumentFormat.OpenXml.Drawing.Charts;
using A = DocumentFormat.OpenXml.Drawing;
using Xdr = DocumentFormat.OpenXml.Drawing.Spreadsheet;

namespace MoaiCode.Tools.OpenXml;

/// <summary>
/// 워크시트에 Open XML 차트(막대/선/원)를 붙인다. 값 캐시를 채워 Excel 없이도 즉시 렌더된다.
/// 카테고리/시리즈는 같은 시트의 세로 단일 열 범위(예: "A2:A11")로 참조한다.
/// </summary>
internal static class XlsxChartBuilder
{
    internal sealed record ChartSeries(string ValuesRange, string? Name, string? NameRef);

    internal sealed record ChartSpec(
        string Type, string? Title, string CategoriesRange, List<ChartSeries> Series, string? Anchor);

    // 시트에 차트들을 추가. rows 는 캐시 값 조회용(0-based row/col).
    public static void AddCharts(
        WorksheetPart wsPart, string sheetName, IReadOnlyList<IReadOnlyList<string>> rows, List<ChartSpec> charts)
    {
        var drawingsPart = wsPart.AddNewPart<DrawingsPart>();
        var worksheetDrawing = new Xdr.WorksheetDrawing();

        var idx = 0;
        foreach (var spec in charts)
        {
            var chartPart = drawingsPart.AddNewPart<ChartPart>();
            chartPart.ChartSpace = BuildChartSpace(spec, sheetName, rows);
            var relId = drawingsPart.GetIdOfPart(chartPart);
            worksheetDrawing.AppendChild(BuildAnchor(spec.Anchor, idx, relId));
            idx++;
        }

        drawingsPart.WorksheetDrawing = worksheetDrawing;
        // 워크시트가 그림 파트를 참조(SheetData 뒤에 와야 함).
        wsPart.Worksheet.AppendChild(new DocumentFormat.OpenXml.Spreadsheet.Drawing
        {
            Id = wsPart.GetIdOfPart(drawingsPart),
        });
    }

    private static C.ChartSpace BuildChartSpace(
        ChartSpec spec, string sheetName, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var plotArea = new C.PlotArea(new C.Layout());
        var type = spec.Type?.Trim().ToLowerInvariant();

        uint catAxisId = 111111111U;
        uint valAxisId = 222222222U;

        if (type == "pie")
        {
            var pie = new C.PieChart(new C.VaryColors { Val = true });
            // 원형은 시리즈 1개만 사용.
            pie.AppendChild(BuildSeries(spec, spec.Series[0], 0, sheetName, rows, "pie"));
            plotArea.AppendChild(pie);
        }
        else if (type == "line")
        {
            var line = new C.LineChart(new C.Grouping { Val = C.GroupingValues.Standard }, new C.VaryColors { Val = false });
            for (var i = 0; i < spec.Series.Count; i++)
            {
                line.AppendChild(BuildSeries(spec, spec.Series[i], i, sheetName, rows, "line"));
            }

            line.AppendChild(new C.AxisId { Val = catAxisId });
            line.AppendChild(new C.AxisId { Val = valAxisId });
            plotArea.AppendChild(line);
            AppendCatValAxes(plotArea, catAxisId, valAxisId);
        }
        else
        {
            var bar = new C.BarChart(
                new C.BarDirection { Val = C.BarDirectionValues.Column },
                new C.BarGrouping { Val = C.BarGroupingValues.Clustered },
                new C.VaryColors { Val = false });
            for (var i = 0; i < spec.Series.Count; i++)
            {
                bar.AppendChild(BuildSeries(spec, spec.Series[i], i, sheetName, rows, "bar"));
            }

            bar.AppendChild(new C.AxisId { Val = catAxisId });
            bar.AppendChild(new C.AxisId { Val = valAxisId });
            plotArea.AppendChild(bar);
            AppendCatValAxes(plotArea, catAxisId, valAxisId);
        }

        var chart = new C.Chart(plotArea) { PlotVisibleOnly = new C.PlotVisibleOnly { Val = true } };
        if (!string.IsNullOrWhiteSpace(spec.Title))
        {
            chart.Title = BuildTitle(spec.Title!);
            chart.AutoTitleDeleted = new C.AutoTitleDeleted { Val = false };
        }

        var space = new C.ChartSpace(chart);
        space.AddNamespaceDeclaration("c", "http://schemas.openxmlformats.org/drawingml/2006/chart");
        space.AddNamespaceDeclaration("a", "http://schemas.openxmlformats.org/drawingml/2006/main");
        space.AddNamespaceDeclaration("r", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
        return space;
    }

    private static void AppendCatValAxes(C.PlotArea plotArea, uint catAxisId, uint valAxisId)
    {
        plotArea.AppendChild(new C.CategoryAxis(
            new C.AxisId { Val = catAxisId },
            new C.Scaling(new C.Orientation { Val = C.OrientationValues.MinMax }),
            new C.Delete { Val = false },
            new C.AxisPosition { Val = C.AxisPositionValues.Bottom },
            new C.CrossingAxis { Val = valAxisId }));
        plotArea.AppendChild(new C.ValueAxis(
            new C.AxisId { Val = valAxisId },
            new C.Scaling(new C.Orientation { Val = C.OrientationValues.MinMax }),
            new C.Delete { Val = false },
            new C.AxisPosition { Val = C.AxisPositionValues.Left },
            new C.CrossingAxis { Val = catAxisId }));
    }

    private static C.Title BuildTitle(string text)
    {
        return new C.Title(
            new C.ChartText(new C.RichText(
                new A.BodyProperties(),
                new A.ListStyle(),
                new A.Paragraph(new A.Run(new A.Text(text))))),
            new C.Overlay { Val = false });
    }

    // 시리즈/데이터포인트 색상 팔레트(Office 유사). spPr 이 없으면 style 파트 없는 소비자(LibreOffice)가
    // 막대를 무색으로 그려 안 보인다 → 명시적 채우기를 넣는다.
    private static readonly string[] Palette =
    {
        "4472C4", "ED7D31", "A5A5A5", "FFC000", "5B9BD5", "70AD47", "264478", "9E480E",
    };

    private static OpenXmlElement BuildSeries(
        ChartSpec spec, ChartSeries s, int index, string sheetName,
        IReadOnlyList<IReadOnlyList<string>> rows, string kind)
    {
        var catFormula = RangeFormula(sheetName, spec.CategoriesRange);
        var valFormula = RangeFormula(sheetName, s.ValuesRange);
        var catValues = ReadRange(rows, spec.CategoriesRange);
        var numValues = ReadRange(rows, s.ValuesRange);

        var seriesText = BuildSeriesText(s, sheetName);
        var catAxisData = new C.CategoryAxisData(BuildStringReference(catFormula, catValues));
        var values = new C.Values(BuildNumberReference(valFormula, numValues));
        var color = Palette[index % Palette.Length];

        if (kind == "pie")
        {
            // 원형은 조각(포인트)마다 색을 줘야 구분된다.
            var ser = new C.PieChartSeries(
                new C.Index { Val = (uint)index },
                new C.Order { Val = (uint)index },
                seriesText);
            for (var j = 0; j < numValues.Count; j++)
            {
                ser.AppendChild(new C.DataPoint(
                    new C.Index { Val = (uint)j },
                    new C.Bubble3D { Val = false },
                    ShapeFill(Palette[j % Palette.Length])));
            }

            ser.AppendChild(catAxisData);
            ser.AppendChild(values);
            return ser;
        }

        if (kind == "line")
        {
            // 선은 LineChartSeries + 선 색(a:ln). 채우기 아님.
            return new C.LineChartSeries(
                new C.Index { Val = (uint)index },
                new C.Order { Val = (uint)index },
                seriesText,
                ShapeLine(color),
                new C.Marker(new C.Symbol { Val = C.MarkerStyleValues.None }),
                catAxisData,
                values);
        }

        // 막대: BarChartSeries + 채우기(tx 뒤, cat 앞).
        return new C.BarChartSeries(
            new C.Index { Val = (uint)index },
            new C.Order { Val = (uint)index },
            seriesText,
            ShapeFill(color),
            catAxisData,
            values);
    }

    private static C.ChartShapeProperties ShapeFill(string hex)
        => new(new A.SolidFill(new A.RgbColorModelHex { Val = hex }));

    private static C.ChartShapeProperties ShapeLine(string hex)
        => new(new A.Outline(new A.SolidFill(new A.RgbColorModelHex { Val = hex })) { Width = 28575 });

    private static C.SeriesText BuildSeriesText(ChartSeries s, string sheetName)
    {
        if (!string.IsNullOrWhiteSpace(s.NameRef))
        {
            return new C.SeriesText(new C.StringReference(
                new C.Formula(RangeFormula(sheetName, s.NameRef!))));
        }

        return new C.SeriesText(new C.NumericValue(s.Name ?? "Series"));
    }

    private static C.StringReference BuildStringReference(string formula, List<string> values)
    {
        var cache = new C.StringCache(new C.PointCount { Val = (uint)values.Count });
        for (var i = 0; i < values.Count; i++)
        {
            cache.AppendChild(new C.StringPoint { Index = (uint)i, NumericValue = new C.NumericValue(values[i]) });
        }

        return new C.StringReference(new C.Formula(formula), cache);
    }

    private static C.NumberReference BuildNumberReference(string formula, List<string> values)
    {
        var cache = new C.NumberingCache(new C.PointCount { Val = (uint)values.Count });
        for (var i = 0; i < values.Count; i++)
        {
            var v = double.TryParse(values[i], NumberStyles.Any, CultureInfo.InvariantCulture, out var n)
                ? n.ToString(CultureInfo.InvariantCulture)
                : "0";
            cache.AppendChild(new C.NumericPoint { Index = (uint)i, NumericValue = new C.NumericValue(v) });
        }

        return new C.NumberReference(new C.Formula(formula), cache);
    }

    // "A2:A11" → "'Sheet'!$A$2:$A$11"
    private static string RangeFormula(string sheetName, string a1Range)
    {
        var parts = a1Range.Split(':');
        var abs = string.Join(":", parts.Select(Absolute));
        return $"'{sheetName.Replace("'", "''")}'!{abs}";
    }

    private static string Absolute(string cell)
    {
        var (col, row) = ParseCell(cell);
        return $"${ColLetters(col)}${row}";
    }

    // 세로 단일 열 범위의 값을 rows(0-based)에서 읽는다.
    private static List<string> ReadRange(IReadOnlyList<IReadOnlyList<string>> rows, string a1Range)
    {
        var parts = a1Range.Split(':');
        var (col, r1) = ParseCell(parts[0]);
        var r2 = parts.Length > 1 ? ParseCell(parts[1]).Row : r1;
        var result = new List<string>();
        for (var r = r1; r <= r2; r++)
        {
            var ri = r - 1;
            var cell = ri >= 0 && ri < rows.Count && col < rows[ri].Count ? rows[ri][col] : "";
            result.Add(cell ?? "");
        }

        return result;
    }

    private static (int Col, int Row) ParseCell(string cell)
    {
        var i = 0;
        var col = 0;
        while (i < cell.Length && char.IsLetter(cell[i]))
        {
            col = col * 26 + (char.ToUpperInvariant(cell[i]) - 'A' + 1);
            i++;
        }

        var row = int.Parse(cell[i..], CultureInfo.InvariantCulture);
        return (col - 1, row);
    }

    private static string ColLetters(int col)
    {
        var name = string.Empty;
        var c = col;
        do
        {
            name = (char)('A' + (c % 26)) + name;
            c = c / 26 - 1;
        }
        while (c >= 0);

        return name;
    }

    // 앵커 셀(예: "H2")에서 시작하는 대략 8열×15행 크기의 TwoCellAnchor.
    private static Xdr.TwoCellAnchor BuildAnchor(string? anchorCell, int index, string chartRelId)
    {
        var (col, row) = ParseCell(string.IsNullOrWhiteSpace(anchorCell) ? "H2" : anchorCell!);
        var fromRow = row - 1 + index * 16; // 여러 차트는 세로로 쌓음
        var from = new Xdr.FromMarker(
            new Xdr.ColumnId(col.ToString(CultureInfo.InvariantCulture)),
            new Xdr.ColumnOffset("0"),
            new Xdr.RowId(fromRow.ToString(CultureInfo.InvariantCulture)),
            new Xdr.RowOffset("0"));
        var to = new Xdr.ToMarker(
            new Xdr.ColumnId((col + 8).ToString(CultureInfo.InvariantCulture)),
            new Xdr.ColumnOffset("0"),
            new Xdr.RowId((fromRow + 15).ToString(CultureInfo.InvariantCulture)),
            new Xdr.RowOffset("0"));

        var graphicFrame = new Xdr.GraphicFrame(
            new Xdr.NonVisualGraphicFrameProperties(
                new Xdr.NonVisualDrawingProperties { Id = (uint)(index + 2), Name = $"Chart {index + 1}" },
                new Xdr.NonVisualGraphicFrameDrawingProperties()),
            new Xdr.Transform(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = 0, Cy = 0 }),
            new A.Graphic(new A.GraphicData(
                new C.ChartReference { Id = chartRelId })
            { Uri = "http://schemas.openxmlformats.org/drawingml/2006/chart" }));

        return new Xdr.TwoCellAnchor(from, to, graphicFrame, new Xdr.ClientData());
    }
}
