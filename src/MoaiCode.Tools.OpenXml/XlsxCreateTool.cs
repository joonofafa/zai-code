using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using MoaiCode.Core.Tools;
using MoaiCode.Localization;

namespace MoaiCode.Tools.OpenXml;

/// <summary>새 Excel 통합문서(.xlsx)를 만든다. Open XML SDK — Excel 설치 불필요, 전 플랫폼.</summary>
public sealed class XlsxCreateTool : ITool
{
    public string Name => "XlsxCreate";

    public string Description => """
        Creates a new Excel workbook (.xlsx) with one or more sheets of rows, using the built-in
        Open XML writer — no dependencies, no Excel install. Cell strings are auto-typed: a plain
        number becomes a number; "12.5%" becomes a real percentage (0.125 with % format);
        "1,234"/"1,234.5" becomes a number with thousands formatting; a string starting with "="
        (e.g. "=SUM(B2:B4)", "=B2/C2") becomes a live formula; anything else stays text. Prefer
        writing percentages, thousands-separated numbers, and formulas this way instead of pre-
        computing them as plain text. Modern functions (TEXTJOIN, XLOOKUP, XMATCH, IFS, SWITCH,
        UNIQUE, FILTER, SORT, LET, …) may be written naturally — they are auto-prefixed (_xlfn.)
        so Excel/LibreOffice recognize them instead of showing #NAME?. Supports native charts
        (bar/line/pie) and a bold header row —
        so charts do NOT need Python/openpyxl. ALWAYS use this to produce an .xlsx file. Do NOT
        install packages (openpyxl, exceljs, etc.) or write scripts to build spreadsheets.
        Charts reference vertical single-column ranges of the same sheet (e.g. categories "A2:A11",
        series values "B2:B11").
        BUSINESS FORMS: set "template" to scaffold a standard Korean form (expense, invoice,
        inventory) — you get the title, header row, table columns (with currency/amount and
        integer/quantity formats) and a total SUM formula. Then either omit "sheets"
        to emit the blank form, or provide "sheets" yourself to fill the same layout with real rows.
        """;

    public bool IsReadOnly => false;
    public bool IsConcurrencySafe => true;

    public JsonElement InputSchema { get; } = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Output .xlsx path (relative to workspace)" },
            "template": { "type": "string", "enum": ["expense", "invoice", "inventory"], "description": "Optional business form to scaffold: expense, invoice, inventory. If set and 'sheets' is omitted, emits the standard blank form." },
            "body_font": { "type": "string", "description": "Brand font name (from a template style skill). Applied to all cells." },
            "title_font": { "type": "string", "description": "Brand font name alias (used if body_font absent)." },
            "sheets": {
              "type": "array",
              "description": "Sheets; each a name + rows (array of string cells)",
              "items": {
                "type": "object",
                "properties": {
                  "name": { "type": "string" },
                  "rows": { "type": "array", "items": { "type": "array", "items": { "type": "string" } } },
                  "boldHeader": { "type": "boolean", "description": "Bold the first row (header). Default false." },
                  "table": { "type": "boolean", "description": "Wrap the data as an Excel Table (ListObject) with filterable/sortable header dropdowns and banded rows. First row is the header. Best for row-oriented data (records, matrices, checklists). Default false." },
                  "formats": {
                    "type": "array",
                    "items": { "type": "string" },
                    "description": "Optional per-column number format, index-aligned to columns. Applies to data rows (not the header) and formats formula results too. Aliases: won, usd, percent, thousands, int, date. Or a raw Excel code like \"#,##0\" or \"0.0%\". Empty string = auto-detect that column."
                  },
                  "charts": {
                    "type": "array",
                    "description": "Charts on this sheet, referencing its cell ranges",
                    "items": {
                      "type": "object",
                      "properties": {
                        "type": { "type": "string", "enum": ["bar", "line", "pie"], "description": "Chart type" },
                        "title": { "type": "string" },
                        "categories": { "type": "string", "description": "Vertical range for labels, e.g. A2:A11" },
                        "series": {
                          "type": "array",
                          "items": {
                            "type": "object",
                            "properties": {
                              "values": { "type": "string", "description": "Vertical value range, e.g. B2:B11" },
                              "name": { "type": "string", "description": "Series name (literal)" },
                              "nameRef": { "type": "string", "description": "Cell holding the series name, e.g. B1" }
                            },
                            "required": ["values"]
                          }
                        },
                        "anchor": { "type": "string", "description": "Top-left cell for the chart, e.g. H2" }
                      },
                      "required": ["categories", "series"]
                    }
                  }
                }
              }
            }
          },
          "required": ["path"]
        }
        """).RootElement.Clone();

    private sealed record ChartSeriesIn(
        [property: JsonPropertyName("values")] string? Values,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("nameRef")] string? NameRef);

    private sealed record ChartIn(
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("categories")] string? Categories,
        [property: JsonPropertyName("series")] List<ChartSeriesIn>? Series,
        [property: JsonPropertyName("anchor")] string? Anchor);

    private sealed record SheetIn(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("rows")] List<List<string>>? Rows,
        [property: JsonPropertyName("boldHeader")] bool? BoldHeader,
        [property: JsonPropertyName("formats")] List<string>? Formats,
        [property: JsonPropertyName("charts")] List<ChartIn>? Charts,
        [property: JsonPropertyName("table")] bool? AsTable = null,
        [property: JsonPropertyName("bordered")] bool? Bordered = null);

    private sealed record Input(
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("template")] string? Template,
        // 브랜드 폰트(템플릿 스타일 스킬에서 전달). 전 셀에 적용해 브랜드 톤을 맞춘다.
        [property: JsonPropertyName("body_font")] string? BodyFont,
        [property: JsonPropertyName("title_font")] string? TitleFont,
        [property: JsonPropertyName("sheets")] List<SheetIn>? Sheets);

    // 요청당 브랜드 폰트(BuildStylesheet 가 시그니처 변경 없이 읽도록 ThreadStatic).
    [ThreadStatic]
    private static string? _brandFont;

    // 한국 업무용 표준 엑셀 양식. template 지정 + sheets 미제공 시 규격 시트를 스캐폴딩한다.
    // 병합 셀은 미지원이므로 표 중심의 깔끔한 서식으로 구성한다(모델이 rows 로 값을 채워 넣음).
    private static List<SheetIn>? FormSheets(string? template)
    {
        List<List<string>> Rows(params string[][] rs) => rs.Select(x => x.ToList()).ToList();
        string[] Empty(int n) => new string[n];

        switch ((template ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "expense" or "지출결의서":
                // 제목·기안정보·항목표(일자/적요/금액/비고)·합계.
                return new() { new SheetIn("지출결의서",
                    Rows(
                        new[] { "지출결의서" },
                        Empty(4),
                        new[] { "기안일", "", "부서", "" },
                        new[] { "작성자", "", "결재", "" },
                        Empty(4),
                        new[] { "일자", "적요", "금액", "비고" },
                        Empty(4), Empty(4), Empty(4), Empty(4), Empty(4),
                        new[] { "", "합계", "=SUM(C7:C11)", "" }),
                    BoldHeader: true, Formats: new() { "date", "", "won", "" }, Charts: null, Bordered: true) };

            case "invoice" or "거래명세서":
                // 제목·거래처·품목표(품목/규격/수량/단가/금액)·합계.
                return new() { new SheetIn("거래명세서",
                    Rows(
                        new[] { "거래명세서" },
                        Empty(5),
                        new[] { "거래처", "", "거래일자", "", "" },
                        Empty(5),
                        new[] { "품목", "규격", "수량", "단가", "금액" },
                        Empty(5), Empty(5), Empty(5), Empty(5), Empty(5),
                        new[] { "합계", "", "", "", "=SUM(E6:E10)" }),
                    BoldHeader: true, Formats: new() { "", "", "int", "won", "won" }, Charts: null, Bordered: true) };

            case "inventory" or "재고관리표":
                // 제목·재고표(품목/규격/입고/출고/재고/비고).
                return new() { new SheetIn("재고관리표",
                    Rows(
                        new[] { "재고관리표" },
                        Empty(6),
                        new[] { "품목", "규격", "입고", "출고", "재고", "비고" },
                        Empty(6), Empty(6), Empty(6), Empty(6), Empty(6)),
                    BoldHeader: true, Formats: new() { "", "", "int", "int", "int", "" }, Charts: null, Bordered: true) };

            default:
                return null;
        }
    }

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        var inp = input.Deserialize<Input>();
        // sheets 미제공 시 template 이 표준 양식을 스캐폴딩(둘 다 없으면 오류).
        var sheets = (inp?.Sheets is { Count: > 0 }) ? inp!.Sheets : FormSheets(inp?.Template);
        if (inp is null || string.IsNullOrWhiteSpace(inp.Path) || sheets is null || sheets.Count == 0)
        {
            yield return new ToolOutput(L10n.Get("tools.xlsxCreate.inputRequired"), IsError: true);
            yield break;
        }

        string full;
        string? error = null;
        try
        {
            full = OpenXmlPaths.ResolveForWrite(context.WorkingDirectory, inp.Path, ".xlsx");
            var bf = inp.BodyFont ?? inp.TitleFont;
            _brandFont = string.IsNullOrWhiteSpace(bf) ? null : bf.Trim();
            try
            {
                Write(full, sheets);
            }
            finally
            {
                _brandFont = null;
            }

            // 테마 스탬프(폰트만 — xlsx 생성은 색 테마가 없음). ExcelEdit 가 기본 폰트로 읽을 수 있게.
            DocThemeStamp.Stamp(full, new DocTheme(null, null, null, null,
                string.IsNullOrWhiteSpace(bf) ? FontResolver.AppDefaultEastAsianFont() : bf!.Trim()));
        }
        catch (Exception ex)
        {
            error = ex.Message;
            full = string.Empty;
        }

        yield return error is not null
            ? new ToolOutput(L10n.Get("tools.xlsxCreate.failed", error), IsError: true)
            : new ToolOutput(L10n.Get("tools.xlsxCreate.ok", full, sheets.Count));
    }

    private static void Write(string path, List<SheetIn> sheets)
    {
        using var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var wbPart = doc.AddWorkbookPart();
        wbPart.Workbook = new Workbook();

        // MoAI 고유 식별자를 심어 나중에 같은 대화로 되찾을 수 있게 한다(생성 문서 한정).
        doc.AddCustomFilePropertiesPart().Properties = OfficeDocId.Build(OfficeDocId.NewId());

        // 모든 시트의 열 서식(formats)을 먼저 수집해 커스텀 numFmt/스타일로 등록한다.
        // (스타일시트는 시트 기록 전에 한 번만 만들어지므로 사전 수집이 필요.)
        var customCodes = new List<string>();
        var codeToStyle = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var sheet in sheets)
        {
            foreach (var fmt in sheet.Formats ?? new List<string>())
            {
                var code = ResolveFormat(fmt);
                if (code is not null && !codeToStyle.ContainsKey(code))
                {
                    codeToStyle[code] = (uint)(FirstCustomStyle + customCodes.Count);
                    customCodes.Add(code);
                }
            }
        }

        var stylesPart = wbPart.AddNewPart<WorkbookStylesPart>();
        stylesPart.Stylesheet = BuildStylesheet(customCodes);

        // 테두리 뱅크 오프셋: 뱅크 A(테두리 없음) 크기 = 고정 5 + 커스텀 numFmt 수.
        // bordered 시트는 각 셀 스타일 인덱스에 이 값을 더해 동일 서식 + 테두리를 참조한다.
        uint borderOffset = FirstCustomStyle + (uint)customCodes.Count;

        var sheetsEl = wbPart.Workbook.AppendChild(new Sheets());

        uint sheetId = 1;
        uint tableId = 1;
        foreach (var sheet in sheets)
        {
            var wsPart = wbPart.AddNewPart<WorksheetPart>();
            var data = new SheetData();

            // 열별 서식 스타일 인덱스(지정 없으면 null → 자동 감지).
            var colStyles = (sheet.Formats ?? new List<string>())
                .Select(f => ResolveFormat(f) is { } code ? codeToStyle[code] : (uint?)null)
                .ToList();

            var bold = sheet.BoldHeader == true;
            var borderBase = sheet.Bordered == true ? borderOffset : 0u;
            var colWidth = new Dictionary<int, double>();
            uint r = 1;
            foreach (var rowCells in sheet.Rows ?? new List<List<string>>())
            {
                var row = new Row { RowIndex = r };
                var col = 0;
                foreach (var value in rowCells)
                {
                    var colStyle = col < colStyles.Count ? colStyles[col] : null;
                    row.AppendChild(MakeCell(Reference(col, r), value, bold && r == 1, colStyle, borderBase));

                    // 열 너비 추정: 수식은 결과를 몰라 내용폭에서 제외하고 서식 최소폭으로 커버.
                    var content = (value ?? string.Empty).StartsWith('=') ? 0 : DisplayWidth(value);
                    var want = Math.Max(content, MinWidthForFormat(col < colStyles.Count ? sheet.Formats?[col] : null));
                    colWidth[col] = Math.Max(colWidth.GetValueOrDefault(col), want);
                    col++;
                }

                data.AppendChild(row);
                r++;
            }

            // Worksheet 조립(스키마 순서: cols → sheetData). 열 너비로 '###' 잘림 방지.
            var ws = new Worksheet();
            if (colWidth.Count > 0)
            {
                var cols = new Columns();
                foreach (var kv in colWidth.OrderBy(k => k.Key))
                {
                    cols.AppendChild(new Column
                    {
                        Min = (uint)(kv.Key + 1),
                        Max = (uint)(kv.Key + 1),
                        Width = Math.Clamp(kv.Value + 2, 8, 80),
                        CustomWidth = true,
                    });
                }

                ws.AppendChild(cols);
            }

            ws.AppendChild(data);
            wsPart.Worksheet = ws;

            // 차트(SheetData 뒤에 drawing 추가).
            var specs = MapCharts(sheet.Charts);
            if (specs.Count > 0)
            {
                var rows = (IReadOnlyList<IReadOnlyList<string>>)(sheet.Rows ?? new List<List<string>>())
                    .Select(x => (IReadOnlyList<string>)x).ToList();
                var sheetName = string.IsNullOrWhiteSpace(sheet.Name) ? $"Sheet{sheetId}" : sheet.Name!;
                XlsxChartBuilder.AddCharts(wsPart, sheetName, rows, specs);
            }

            // Excel Table(ListObject) + AutoFilter — tableParts 는 drawing 뒤(스키마 마지막 쪽)에 온다.
            if (sheet.AsTable == true)
            {
                AddTable(wsPart, ws, sheet.Rows ?? new List<List<string>>(), tableId++);
            }

            sheetsEl.AppendChild(new Sheet
            {
                Id = wbPart.GetIdOfPart(wsPart),
                SheetId = sheetId,
                Name = string.IsNullOrWhiteSpace(sheet.Name) ? $"Sheet{sheetId}" : sheet.Name!,
            });
            sheetId++;
        }
    }

    // 데이터 범위를 Excel Table(ListObject)로 감싼다: 필터/정렬 드롭다운(AutoFilter) + 줄무늬 스타일.
    // 첫 행 = 헤더. 열 이름은 헤더 텍스트에서 취하되 비었거나 중복이면 정규화(Excel 요구: 고유·비어있지 않음).
    private static void AddTable(WorksheetPart wsPart, Worksheet ws, List<List<string>> rows, uint tableId)
    {
        var nrows = rows.Count;
        var ncols = nrows > 0 ? rows.Max(r => r.Count) : 0;
        if (nrows < 2 || ncols < 1)
        {
            return; // 헤더 + 데이터 1행 이상일 때만 테이블로 만든다.
        }

        var range = $"{Reference(0, 1)}:{Reference(ncols - 1, (uint)nrows)}";
        var headers = rows[0];
        var cols = new TableColumns { Count = (uint)ncols };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < ncols; i++)
        {
            var name = (i < headers.Count ? headers[i] ?? string.Empty : string.Empty).Trim();
            if (name.Length == 0)
            {
                name = $"Column{i + 1}";
            }

            var baseName = name;
            var suffix = 2;
            while (!seen.Add(name))
            {
                name = $"{baseName}{suffix++}";
            }

            cols.AppendChild(new TableColumn { Id = (uint)(i + 1), Name = name });
        }

        var table = new Table
        {
            Id = tableId,
            Name = $"Table{tableId}",
            DisplayName = $"Table{tableId}",
            Reference = range,
        };
        table.AppendChild(new AutoFilter { Reference = range });
        table.AppendChild(cols);
        table.AppendChild(new TableStyleInfo
        {
            Name = "TableStyleMedium2",
            ShowFirstColumn = false,
            ShowLastColumn = false,
            ShowRowStripes = true,
            ShowColumnStripes = false,
        });

        var tPart = wsPart.AddNewPart<TableDefinitionPart>();
        tPart.Table = table;
        ws.AppendChild(new TableParts(new TablePart { Id = wsPart.GetIdOfPart(tPart) }) { Count = 1U });
    }

    private static List<XlsxChartBuilder.ChartSpec> MapCharts(List<ChartIn>? charts)
    {
        var result = new List<XlsxChartBuilder.ChartSpec>();
        foreach (var c in charts ?? new List<ChartIn>())
        {
            if (string.IsNullOrWhiteSpace(c.Categories) || c.Series is null || c.Series.Count == 0)
            {
                continue; // 불완전한 차트 스펙은 건너뜀
            }

            var series = c.Series
                .Where(s => !string.IsNullOrWhiteSpace(s.Values))
                .Select(s => new XlsxChartBuilder.ChartSeries(s.Values!, s.Name, s.NameRef))
                .ToList();
            if (series.Count == 0)
            {
                continue;
            }

            result.Add(new XlsxChartBuilder.ChartSpec(
                string.IsNullOrWhiteSpace(c.Type) ? "bar" : c.Type!, c.Title, c.Categories!, series, c.Anchor));
        }

        return result;
    }

    // CellFormat 인덱스: 0 기본 · 1 굵게 · 2 퍼센트(0.00%) · 3 천단위 정수(#,##0) · 4 천단위 소수(#,##0.00).
    // 5+ = 열 서식(formats)으로 요청된 커스텀 코드. 서식 ID 10/3/4 는 builtin.
    private const uint StyleBold = 1;
    private const uint StylePercent = 2;
    private const uint StyleThousandsInt = 3;
    private const uint StyleThousandsDec = 4;
    private const uint FirstCustomStyle = 5;
    private const uint FirstCustomNumFmtId = 164; // Open XML: 커스텀 numFmt 는 164 이상.

    // 열 서식 별칭 → Excel 서식 코드. 별칭이 아니면 원시 코드로 사용한다.
    private static string? ResolveFormat(string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            return null;
        }

        return alias.Trim().ToLowerInvariant() switch
        {
            "won" or "krw" or "currency" => "#,##0\"원\"",
            "usd" => "$#,##0.00",
            "percent" or "pct" => "0.0%",
            "thousands" => "#,##0",
            "int" => "0",
            "date" => "yyyy-mm-dd",
            _ => alias.Trim(),
        };
    }

    private static Stylesheet BuildStylesheet(IReadOnlyList<string> customCodes)
    {
        var numberingFormats = new NumberingFormats();
        var fmtId = FirstCustomNumFmtId;

        // 뱅크 A(테두리 없음) 서식 스펙: (fontId?, numFmtId?). 인덱스 0..4 는 기존과 동일.
        var specs = new List<(uint? Font, uint? NumFmt)>
        {
            (null, null),  // 0: 기본
            (1u, null),    // 1: 굵게
            (null, 10u),   // 2: 0.00%
            (null, 3u),    // 3: #,##0
            (null, 4u),    // 4: #,##0.00
        };
        foreach (var code in customCodes)
        {
            numberingFormats.AppendChild(new NumberingFormat { NumberFormatId = fmtId, FormatCode = code });
            specs.Add((null, fmtId));
            fmtId++;
        }

        // 뱅크 A(테두리 없음) 그다음 뱅크 B(테두리 borderId=1). B 인덱스 = A 인덱스 + specs.Count(=borderOffset).
        CellFormat Make((uint? Font, uint? NumFmt) s, bool bordered)
        {
            var cf = new CellFormat();
            if (s.Font is uint f) { cf.FontId = f; cf.ApplyFont = true; }
            if (s.NumFmt is uint n) { cf.NumberFormatId = n; cf.ApplyNumberFormat = true; }
            if (bordered) { cf.BorderId = 1; cf.ApplyBorder = true; }
            return cf;
        }

        var cellFormats = new CellFormats();
        foreach (var s in specs) { cellFormats.AppendChild(Make(s, false)); }
        foreach (var s in specs) { cellFormats.AppendChild(Make(s, true)); }
        cellFormats.Count = (uint)(specs.Count * 2);

        // 폰트: 브랜드 폰트 > 앱 언어 기본 EA 폰트(맑은 고딕 등) > Excel 기본(Calibri). 앞의 둘이면 전 셀 그 폰트.
        var effFont = string.IsNullOrWhiteSpace(_brandFont) ? FontResolver.AppDefaultEastAsianFont() : _brandFont;
        var fonts = string.IsNullOrWhiteSpace(effFont)
            ? new Fonts(new Font(), new Font(new Bold()))
            : new Fonts(
                new Font(new FontName { Val = effFont }),
                new Font(new Bold(), new FontName { Val = effFont }));
        var fills = new Fills(new Fill(new PatternFill { PatternType = PatternValues.None }));

        // 테두리: 0 = 없음, 1 = 얇은 사방 테두리(양식 표용).
        var thin = new Border(
            new LeftBorder { Style = BorderStyleValues.Thin },
            new RightBorder { Style = BorderStyleValues.Thin },
            new TopBorder { Style = BorderStyleValues.Thin },
            new BottomBorder { Style = BorderStyleValues.Thin },
            new DiagonalBorder());
        var borders = new Borders(new Border(), thin);

        // Stylesheet 자식 순서(스키마): numFmts → fonts → fills → borders → cellFormats.
        if (customCodes.Count > 0)
        {
            numberingFormats.Count = (uint)customCodes.Count;
            return new Stylesheet(numberingFormats, fonts, fills, borders, cellFormats);
        }

        return new Stylesheet(fonts, fills, borders, cellFormats);
    }

    private static readonly Regex PercentRe = new(@"^-?\d+(\.\d+)?%$", RegexOptions.Compiled);
    private static readonly Regex ThousandsRe = new(@"^-?\d{1,3}(,\d{3})+(\.\d+)?$", RegexOptions.Compiled);

    // 셀 문자열을 타이핑한다. columnStyle 지정 시 그 서식을 강제(수식 결과 서식·통화·날짜 등),
    // 아니면 자동 감지: 헤더=라벨(굵게 텍스트), 수식(=)·퍼센트·천단위·순수숫자·텍스트.
    // borderBase 지정 시(bordered 시트) 셀 스타일 인덱스에 오프셋을 더해 동일 서식+테두리를 참조한다.
    private static Cell MakeCell(string reference, string? value, bool isHeader, uint? columnStyle, uint borderBase)
    {
        var cell = MakeCellCore(reference, value, isHeader, columnStyle);
        if (borderBase != 0)
        {
            cell.StyleIndex = (cell.StyleIndex?.Value ?? 0u) + borderBase;
        }

        return cell;
    }

    private static Cell MakeCellCore(string reference, string? value, bool isHeader, uint? columnStyle)
    {
        var v = value ?? string.Empty;

        // 헤더는 라벨로 취급 — 값 감지/열서식 없이 굵게 텍스트.
        if (isHeader)
        {
            return Str(reference, v, StyleBold);
        }

        // 열 서식이 지정된 경우: 값에서 숫자를 뽑아 그 서식으로(수식 결과에도 서식 적용).
        if (columnStyle is uint cs)
        {
            if (v.Length > 1 && v[0] == '=')
            {
                var expr = v[1..];
                // 수식 인젝션(DDE/명령) 방어: 위험 패턴이면 라이브 수식이 아니라 텍스트로 기록.
                return IsDangerousFormula(expr) ? Str(reference, v, 0u) : Formula(reference, expr, cs);
            }

            var isPct = v.EndsWith('%');
            var numStr = (isPct ? v[..^1] : v).Replace(",", string.Empty);
            if (double.TryParse(numStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var cn))
            {
                return Num(reference, isPct ? cn / 100.0 : cn, cs);
            }

            return Str(reference, v, 0u); // 숫자가 아니면 서식 미적용 텍스트
        }

        // 수식: "=..." → 라이브 수식(값은 Excel/LibreOffice 가 열 때 계산).
        if (v.Length > 1 && v[0] == '=')
        {
            var expr = v[1..];
            // 수식 인젝션(DDE/명령) 방어: 위험 패턴이면 라이브 수식이 아니라 텍스트로 기록.
            return IsDangerousFormula(expr) ? Str(reference, v, 0u) : Formula(reference, expr, 0u);
        }

        // 퍼센트: "12.5%" → 0.125 + 퍼센트 서식.
        if (PercentRe.IsMatch(v)
            && double.TryParse(v[..^1], NumberStyles.Any, CultureInfo.InvariantCulture, out var pct))
        {
            return Num(reference, pct / 100.0, StylePercent);
        }

        // 천단위 구분: "1,234" / "1,234.5" → 숫자 + 천단위 서식.
        if (ThousandsRe.IsMatch(v))
        {
            var stripped = v.Replace(",", string.Empty);
            if (double.TryParse(stripped, NumberStyles.Any, CultureInfo.InvariantCulture, out var tn))
            {
                return Num(reference, tn, stripped.Contains('.') ? StyleThousandsDec : StyleThousandsInt);
            }
        }

        // 순수 숫자(InvariantCulture — InvariantGlobalization 대응).
        if (!string.IsNullOrEmpty(v)
            && double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var num))
        {
            return Num(reference, num, 0u);
        }

        return Str(reference, v, 0u);
    }

    // 표시폭 추정: CJK/전각 문자는 2폭으로 센다(한글 문서 열 너비 근사).
    private static double DisplayWidth(string? s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return 0;
        }

        double w = 0;
        foreach (var ch in s)
        {
            w += ch >= 0x1100 ? 2 : 1;
        }

        return w;
    }

    // 서식 열의 최소 표시폭(서식 적용 후 길어지는 통화·천단위·퍼센트·날짜 커버).
    private static double MinWidthForFormat(string? alias)
    {
        var code = ResolveFormat(alias);
        if (code is null)
        {
            return 0;
        }

        if (code.Contains('원') || code.Contains('$') || code.Contains("#,##0"))
        {
            return 14;
        }

        if (code.Contains('%'))
        {
            return 10;
        }

        return code.Contains("yyyy") ? 12 : 0;
    }

    // 수식 인젝션(CSV/DDE) 방어: 정상 수식(=SUM, =A1+B1, =IF(...))은 글자·괄호·숫자·셀참조로 시작한다.
    // 신뢰할 수 없는 데이터가 라이브 수식이 되는 대표 벡터만 차단한다(오탐 최소화):
    //  (1) '='다음 첫 글자가 + - @  (OWASP CSV 인젝션 lead-in)
    //  (2) DDE 파이프 '|' 포함        (예: cmd|'/c calc'!A0)
    //  (3) 알려진 명령 토큰으로 시작   (cmd/dde/msexcel/msquery/rundll/powershell/system)
    // 위험하면 라이브 수식 대신 텍스트로 기록해 실행을 막는다(InlineString 은 평가되지 않음).
    private static readonly string[] DangerCommands =
        { "cmd", "dde", "msexcel", "msquery", "rundll", "powershell", "system" };

    internal static bool IsDangerousFormula(string expr)
    {
        if (string.IsNullOrEmpty(expr))
        {
            return false;
        }

        var c0 = expr[0];
        if (c0 is '+' or '-' or '@')
        {
            return true;
        }

        if (expr.Contains('|'))
        {
            return true;
        }

        var low = expr.TrimStart().ToLowerInvariant();
        return DangerCommands.Any(k => low.StartsWith(k, StringComparison.Ordinal));
    }

    // 모던 함수(Excel 2016+/365)는 OOXML 에 반드시 _xlfn.(일부는 _xlfn._xlws.) 접두사로 저장해야
    // Excel/LibreOffice 가 인식한다. 접두사 없이 저장하면 #NAME? 이 뜬다. 모델이 접두사 없이 써도
    // 여기서 정규화한다(이미 접두사가 있으면 앞의 '.' 룩비하인드로 재적용되지 않음).
    private static readonly (Regex Rx, string Repl)[] ModernFuncRules = new[]
    {
        "TEXTJOIN", "CONCAT", "IFS", "SWITCH", "MAXIFS", "MINIFS", "XLOOKUP", "XMATCH",
        "UNIQUE", "SEQUENCE", "RANDARRAY", "LET", "LAMBDA", "TEXTSPLIT", "TEXTBEFORE", "TEXTAFTER",
        "FILTER", "SORT", "SORTBY",
    }.Select(f =>
    {
        // FILTER/SORT/SORTBY 는 워크시트 하위 네임스페이스(_xlfn._xlws.).
        var repl = f is "FILTER" or "SORT" or "SORTBY" ? $"_xlfn._xlws.{f}" : $"_xlfn.{f}";
        return (new Regex($@"(?<![A-Za-z0-9_.]){f}(?=\s*\()", RegexOptions.IgnoreCase | RegexOptions.Compiled), repl);
    }).ToArray();

    internal static string NormalizeFormula(string expr)
    {
        foreach (var (rx, repl) in ModernFuncRules)
        {
            expr = rx.Replace(expr, repl);
        }

        return expr;
    }

    private static Cell Formula(string reference, string expr, uint styleIndex)
    {
        var c = new Cell { CellReference = reference, CellFormula = new CellFormula(NormalizeFormula(expr)) };
        if (styleIndex != 0)
        {
            c.StyleIndex = styleIndex;
        }

        return c;
    }

    private static Cell Num(string reference, double value, uint styleIndex)
    {
        var c = new Cell
        {
            CellReference = reference,
            DataType = CellValues.Number,
            CellValue = new CellValue(value.ToString(CultureInfo.InvariantCulture)),
        };
        if (styleIndex != 0)
        {
            c.StyleIndex = styleIndex;
        }

        return c;
    }

    private static Cell Str(string reference, string value, uint styleIndex)
    {
        var c = new Cell
        {
            CellReference = reference,
            DataType = CellValues.InlineString,
            InlineString = new InlineString(new Text(value)),
        };
        if (styleIndex != 0)
        {
            c.StyleIndex = styleIndex;
        }

        return c;
    }

    // 0-based col, 1-based row → "A1" 형식.
    private static string Reference(int col, uint row)
    {
        var name = string.Empty;
        var c = col;
        do
        {
            name = (char)('A' + (c % 26)) + name;
            c = c / 26 - 1;
        }
        while (c >= 0);

        return name + row.ToString(CultureInfo.InvariantCulture);
    }
}
