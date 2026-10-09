using System.Text;

namespace MoaiCode.Tools.Files;

/// <summary>
/// 텍스트 파일 인코딩 판별·보존(Read/Edit/Write 공용). BOM 으로 UTF-8(BOM)·UTF-16 LE/BE 를 알아보고,
/// BOM 이 없으면 UTF-8 로 본다. 예전엔 항상 UTF-8 로 읽고 BOM 없는 UTF-8 로 써서 BOM 이 사라지고
/// UTF-16 파일(.ps1·.reg 등)이 재인코딩됐으며, 레거시 인코딩(CP949 등)은 파일 전체가 U+FFFD 로 깨졌다.
/// </summary>
internal static class TextFileCodec
{
    /// <summary>BOM 으로 인코딩을 판별한다. 반환 인코딩은 쓸 때 같은 BOM 을 앞에 붙인다(GetPreamble).</summary>
    public static (Encoding Encoding, int BomLength) Detect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true), 3);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            return (new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true), 2);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            return (new UnicodeEncoding(bigEndian: true, byteOrderMark: true, throwOnInvalidBytes: true), 2);
        }

        return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true), 0);
    }

    /// <summary>보여주기용 디코딩(잘못된 바이트는 U+FFFD). Read 가 쓴다.</summary>
    public static string DecodeLenient(byte[] bytes, int count)
    {
        var (enc, bom) = Detect(bytes.AsSpan(0, count));
        var lenient = (Encoding)enc.Clone();
        lenient.DecoderFallback = DecoderFallback.ReplacementFallback;
        return lenient.GetString(bytes, bom, count - bom);
    }

    /// <summary>편집용 디코딩. 판별한 인코딩으로 온전히 풀리지 않으면(레거시 인코딩 등) false — 다시 쓰면 깨진다.</summary>
    public static bool TryDecodeStrict(byte[] bytes, out string text, out Encoding encoding)
    {
        var (enc, bom) = Detect(bytes);
        encoding = enc;
        try
        {
            text = enc.GetString(bytes, bom, bytes.Length - bom);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }

    /// <summary>인코딩의 BOM(있으면) + 본문 바이트.</summary>
    public static byte[] Encode(string text, Encoding encoding)
    {
        var preamble = encoding.GetPreamble();
        var body = encoding.GetBytes(text);
        var result = new byte[preamble.Length + body.Length];
        preamble.CopyTo(result, 0);
        body.CopyTo(result, preamble.Length);
        return result;
    }
}
