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

    /// <summary>픽셀 크기(PNG/JPEG/GIF/BMP). 실패 시 null. → ImageInfo.PixelSize 공유.</summary>
    public static (int W, int H)? PixelSize(string path) => ImageInfo.PixelSize(path);

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

/// <summary>이미지 파일 헤더에서 픽셀 크기를 읽는다(디코더 없이). PNG IHDR · JPEG SOFn · GIF · BMP.
/// 생성/다운로드 툴이 결과에 크기·비율을 알려주고, insert_picture 가 비율을 지켜 넣는 데 쓴다
/// (2026-08-27: 모델이 생성 이미지 크기를 몰라 852x78 으로 늘려 왜곡시킨 사고).</summary>
public static class ImageInfo
{
    public static (int W, int H)? PixelSize(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return PixelSize(fs);
        }
        catch
        {
            return null;
        }
    }

    public static (int W, int H)? PixelSize(Stream fs)
    {
        Span<byte> b = stackalloc byte[26];
        var n = fs.Read(b);
        if (n < 10)
        {
            return null;
        }

        // PNG
        if (n >= 24 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)
        {
            var w = (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19];
            var h = (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23];
            return w > 0 && h > 0 ? (w, h) : null;
        }

        // GIF (little-endian 폭/높이 at 6..9)
        if (b[0] == (byte)'G' && b[1] == (byte)'I' && b[2] == (byte)'F')
        {
            int w = b[6] | (b[7] << 8), h = b[8] | (b[9] << 8);
            return w > 0 && h > 0 ? (w, h) : null;
        }

        // BMP (int32 LE at 18/22; 높이는 음수 가능)
        if (n >= 26 && b[0] == (byte)'B' && b[1] == (byte)'M')
        {
            int w = BitConverter.ToInt32(b.Slice(18, 4)), h = Math.Abs(BitConverter.ToInt32(b.Slice(22, 4)));
            return w > 0 && h > 0 ? (w, h) : null;
        }

        // JPEG: 세그먼트를 훑어 SOF0~SOF15(C4/C8/CC 제외) 에서 높이·폭(big-endian).
        if (b[0] == 0xFF && b[1] == 0xD8)
        {
            fs.Position = 2;
            Span<byte> hdr = stackalloc byte[9];
            while (true)
            {
                int m1 = fs.ReadByte();
                if (m1 < 0) return null;
                if (m1 != 0xFF) continue;
                int marker = fs.ReadByte();
                if (marker < 0) return null;
                if (marker == 0xFF || marker == 0xD8 || (marker >= 0xD0 && marker <= 0xD7) || marker == 0x01) continue;
                if (fs.Read(hdr.Slice(0, 2)) < 2) return null;
                int len = (hdr[0] << 8) | hdr[1];
                bool sof = marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
                if (sof)
                {
                    if (fs.Read(hdr.Slice(0, 5)) < 5) return null;
                    int h = (hdr[1] << 8) | hdr[2], w = (hdr[3] << 8) | hdr[4];
                    return w > 0 && h > 0 ? (w, h) : null;
                }

                if (marker == 0xDA) return null; // SOS 이후엔 SOF 없음
                fs.Position += len - 2;
            }
        }

        return null;
    }

    /// <summary>모델용 크기 안내 문구(툴 출력에 덧붙임). 크기를 못 읽으면 빈 문자열.</summary>
    public static string Describe(string path)
    {
        var px = PixelSize(path);
        return px is { } p
            ? " " + MoaiCode.Localization.L10n.Get("tools.image.sizeNote", p.W, p.H, ((double)p.W / p.H).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))
            : string.Empty;
    }
}

