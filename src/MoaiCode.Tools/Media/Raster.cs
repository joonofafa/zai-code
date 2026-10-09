using System.IO.Compression;

namespace MoaiCode.Tools.Media;

/// <summary>
/// 이미지 분석 보조용 최소 래스터 연산: Lanczos3 리샘플링과 PNG 인코딩.
/// 예전엔 SixLabors.ImageSharp 를 썼다 — 3.x 부터 Six Labors Split License 라 기업 내부 상업적 사용이
/// 상용 라이선스 대상이고(moai-code 계열은 기업 내부용), 보안 권고도 이어져 걷어냈다. 디코딩은 StbImageSharp
/// (Unlicense OR MIT, 순수 관리 코드)에 맡기고, 여기서는 확대와 무손실 저장만 한다(외부 의존 없음).
/// 픽셀 버퍼는 행 우선 8비트 채널 배열(RGB 또는 RGBA)이다.
/// </summary>
public static class Raster
{
    private const int Lobes = 3;   // Lanczos3

    /// <summary>
    /// <paramref name="pixels"/>(w×h, <paramref name="channels"/> 채널)를 nw×nh 로 리샘플링한다(분리형 Lanczos3).
    /// 채널은 독립적으로 보간한다(알파 미리곱셈 없음 — 분석용 확대라 경계 후광은 무시해도 된다).
    /// </summary>
    public static byte[] Resize(byte[] pixels, int w, int h, int channels, int nw, int nh)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(w, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(h, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(nw, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(nh, 1);
        if (pixels.Length != w * h * channels)
        {
            throw new ArgumentException("pixel buffer size does not match dimensions", nameof(pixels));
        }

        // 가로 → 임시(float) → 세로. 각 축의 가중치는 출력 좌표마다 한 번만 계산한다.
        var (xs, xw) = Kernel(w, nw);
        var tmp = new float[h * nw * channels];
        for (var y = 0; y < h; y++)
        {
            var row = y * w * channels;
            for (var x = 0; x < nw; x++)
            {
                var o = (y * nw + x) * channels;
                for (var t = 0; t < xs[x].Length; t++)
                {
                    var src = row + xs[x][t] * channels;
                    var k = xw[x][t];
                    for (var c = 0; c < channels; c++)
                    {
                        tmp[o + c] += pixels[src + c] * k;
                    }
                }
            }
        }

        var (ys, yw) = Kernel(h, nh);
        var dst = new byte[nw * nh * channels];
        for (var y = 0; y < nh; y++)
        {
            for (var x = 0; x < nw; x++)
            {
                var o = (y * nw + x) * channels;
                for (var c = 0; c < channels; c++)
                {
                    float acc = 0;
                    for (var t = 0; t < ys[y].Length; t++)
                    {
                        acc += tmp[(ys[y][t] * nw + x) * channels + c] * yw[y][t];
                    }

                    dst[o + c] = (byte)Math.Clamp((int)MathF.Round(acc), 0, 255);
                }
            }
        }

        return dst;
    }

    // 출력 좌표마다 참조할 입력 인덱스(가장자리 클램프)와 정규화된 Lanczos3 가중치.
    // 축소 시에는 커널 폭을 배율만큼 넓혀 앨리어싱을 막는다(확대 시엔 폭 1).
    private static (int[][] Index, float[][] Weight) Kernel(int src, int dst)
    {
        var scale = dst / (double)src;
        var support = Lobes * Math.Max(1.0, 1.0 / scale);
        var stretch = Math.Max(1.0, 1.0 / scale);
        var index = new int[dst][];
        var weight = new float[dst][];
        for (var i = 0; i < dst; i++)
        {
            var center = (i + 0.5) / scale - 0.5;
            var lo = (int)Math.Floor(center - support) + 1;
            var hi = (int)Math.Floor(center + support);
            var n = hi - lo + 1;
            index[i] = new int[n];
            weight[i] = new float[n];
            double sum = 0;
            for (var t = 0; t < n; t++)
            {
                var j = lo + t;
                var v = Lanczos((j - center) / stretch);
                index[i][t] = Math.Clamp(j, 0, src - 1);
                weight[i][t] = (float)v;
                sum += v;
            }

            for (var t = 0; t < n; t++)
            {
                weight[i][t] = (float)(weight[i][t] / sum);
            }
        }

        return (index, weight);
    }

    private static double Lanczos(double x)
    {
        x = Math.Abs(x);
        if (x < 1e-8)
        {
            return 1;
        }

        if (x >= Lobes)
        {
            return 0;
        }

        var px = Math.PI * x;
        return Lobes * Math.Sin(px) * Math.Sin(px / Lobes) / (px * px);
    }

    /// <summary>8비트 RGB(3) 또는 RGBA(4) 픽셀을 PNG 로 인코딩한다(행마다 Up 필터, zlib 압축).</summary>
    public static byte[] EncodePng(byte[] pixels, int w, int h, int channels)
    {
        if (channels is not (3 or 4))
        {
            throw new ArgumentOutOfRangeException(nameof(channels), "only RGB (3) or RGBA (4)");
        }

        if (pixels.Length != w * h * channels)
        {
            throw new ArgumentException("pixel buffer size does not match dimensions", nameof(pixels));
        }

        var stride = w * channels;
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
        {
            var line = new byte[stride + 1];
            for (var y = 0; y < h; y++)
            {
                line[0] = 2;   // Up: 위 행과의 차이(스크린샷·확대본에서 압축이 잘 된다)
                var cur = y * stride;
                for (var i = 0; i < stride; i++)
                {
                    var up = y > 0 ? pixels[cur - stride + i] : (byte)0;
                    line[i + 1] = (byte)(pixels[cur + i] - up);
                }

                z.Write(line, 0, line.Length);
            }
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        WriteBE(ihdr, 0, w);
        WriteBE(ihdr, 4, h);
        ihdr[8] = 8;                               // 채널당 8비트
        ihdr[9] = (byte)(channels == 4 ? 6 : 2);   // 6=RGBA, 2=RGB
        // 10~12: 압축 0, 필터 방식 0, 인터레이스 없음
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", raw.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var head = new byte[8];
        WriteBE(head, 0, data.Length);
        for (var i = 0; i < 4; i++)
        {
            head[4 + i] = (byte)type[i];
        }

        s.Write(head);
        s.Write(data);
        var crc = Crc32(head.AsSpan(4, 4), data);
        var tail = new byte[4];
        WriteBE(tail, 0, (int)crc);
        s.Write(tail);
    }

    private static void WriteBE(byte[] b, int o, int v)
    {
        b[o] = (byte)(v >> 24);
        b[o + 1] = (byte)(v >> 16);
        b[o + 2] = (byte)(v >> 8);
        b[o + 3] = (byte)v;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            t[n] = c;
        }

        return t;
    }

    private static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in type)
        {
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        }

        foreach (var b in data)
        {
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        }

        return c ^ 0xFFFFFFFFu;
    }
}
