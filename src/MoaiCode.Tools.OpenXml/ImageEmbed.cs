using System;
using System.IO;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using P = DocumentFormat.OpenXml.Presentation;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace MoaiCode.Tools.OpenXml;

/// <summary>docx/pptx 에 이미지(PNG/JPG 등)를 삽입하는 공용 헬퍼. Open XML Drawing 조립.</summary>
internal static class ImageEmbed
{
    public const long EmuPerInch = 914400;

    public static PartTypeInfo PartType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => ImagePartType.Jpeg,
        ".gif" => ImagePartType.Gif,
        ".bmp" => ImagePartType.Bmp,
        _ => ImagePartType.Png,
    };

    /// <summary>PNG IHDR 에서 픽셀 크기를 읽는다(PNG 외/실패 시 null).</summary>
    public static (int W, int H)? PixelSize(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> b = stackalloc byte[24];
            if (fs.Read(b) < 24)
            {
                return null;
            }

            if (b[0] != 0x89 || b[1] != 0x50 || b[2] != 0x4E || b[3] != 0x47)
            {
                return null; // PNG 시그니처만 지원
            }

            var w = (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19];
            var h = (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23];
            return (w > 0 && h > 0) ? (w, h) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>폭(인치) 기준 종횡비 유지 EMU 크기. 크기 못 읽으면 4:3.</summary>
    public static (long Cx, long Cy) EmuSize(string path, double widthInches)
    {
        var cx = (long)(widthInches * EmuPerInch);
        var px = PixelSize(path);
        var ratio = px is { } p ? (double)p.H / p.W : 0.75;
        return (cx, (long)(cx * ratio));
    }

    /// <summary>docx 본문용 인라인 이미지 문단.</summary>
    public static W.Paragraph DocxImageParagraph(MainDocumentPart main, string imagePath, double widthInches, uint id)
    {
        var part = main.AddImagePart(PartType(imagePath));
        using (var fs = File.OpenRead(imagePath))
        {
            part.FeedData(fs);
        }

        var relId = main.GetIdOfPart(part);
        var (cx, cy) = EmuSize(imagePath, widthInches);

        var drawing = new W.Drawing(
            new DW.Inline(
                new DW.Extent { Cx = cx, Cy = cy },
                new DW.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
                new DW.DocProperties { Id = id, Name = "Image" + id },
                new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
                new A.Graphic(new A.GraphicData(
                    new PIC.Picture(
                        new PIC.NonVisualPictureProperties(
                            new PIC.NonVisualDrawingProperties { Id = 0U, Name = "Image" + id },
                            new PIC.NonVisualPictureDrawingProperties()),
                        new PIC.BlipFill(
                            new A.Blip { Embed = relId },
                            new A.Stretch(new A.FillRectangle())),
                        new PIC.ShapeProperties(
                            new A.Transform2D(
                                new A.Offset { X = 0L, Y = 0L },
                                new A.Extents { Cx = cx, Cy = cy }),
                            new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }))
                    )
                    { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" })
            )
            {
                DistanceFromTop = 0U,
                DistanceFromBottom = 0U,
                DistanceFromLeft = 0U,
                DistanceFromRight = 0U,
            });

        return new W.Paragraph(new W.Run(drawing));
    }

    /// <summary>pptx 슬라이드용 위치형 그림(Picture). 좌표·크기는 EMU.</summary>
    public static P.Picture PptxPicture(SlidePart slidePart, string imagePath, long x, long y, long cx, long cy, uint id)
    {
        var part = slidePart.AddImagePart(PartType(imagePath));
        using (var fs = File.OpenRead(imagePath))
        {
            part.FeedData(fs);
        }

        var relId = slidePart.GetIdOfPart(part);

        return new P.Picture(
            new P.NonVisualPictureProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = "Image" + id },
                new P.NonVisualPictureDrawingProperties(new A.PictureLocks { NoChangeAspect = true }),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.BlipFill(
                new A.Blip { Embed = relId },
                new A.Stretch(new A.FillRectangle())),
            new P.ShapeProperties(
                new A.Transform2D(
                    new A.Offset { X = x, Y = y },
                    new A.Extents { Cx = cx, Cy = cy }),
                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }));
    }
}
