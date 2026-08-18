using System.Text.Json;

namespace MoaiCode.Persistence;

/// <summary>모델 하나의 누적 사용량(로컬 기준).</summary>
public sealed record ModelUsage(string Model, long InputTokens, long OutputTokens, long Turns);

/// <summary>
/// 모델별 토큰 사용량을 로컬에 누적해 ~/.moai/usage.json 에 영속화. /usage 표시용.
/// 서버 과금과 무관한 "이 클라이언트가 집계한" 기준. 턴마다 Record 로 델타를 더한다.
/// </summary>
public sealed class UsageStore
{
    private sealed class Entry
    {
        public long In { get; set; }
        public long Out { get; set; }
        public long Turns { get; set; }
    }

    private sealed class Doc
    {
        public string? Since { get; set; }
        public Dictionary<string, Entry> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _models = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>집계 시작 시각(첫 사용 기록 이후 유지).</summary>
    public DateTimeOffset Since { get; private set; } = DateTimeOffset.Now;

    public UsageStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".zaicode", "usage.json");
        Load();
    }

    /// <summary>한 턴의 입/출력 토큰을 해당 모델에 누적. 음수/공백 모델은 방어적으로 처리.</summary>
    public void Record(string? model, int input, int output)
    {
        var key = string.IsNullOrWhiteSpace(model) ? "(unknown)" : model!.Trim();
        lock (_lock)
        {
            if (!_models.TryGetValue(key, out var e))
            {
                e = new Entry();
                _models[key] = e;
            }

            e.In += Math.Max(0, input);
            e.Out += Math.Max(0, output);
            e.Turns += 1;
            Save();
        }
    }

    /// <summary>사용량 많은 순 모델별 누적.</summary>
    public IReadOnlyList<ModelUsage> All()
    {
        lock (_lock)
        {
            return _models
                .Select(kv => new ModelUsage(kv.Key, kv.Value.In, kv.Value.Out, kv.Value.Turns))
                .OrderByDescending(m => m.InputTokens + m.OutputTokens)
                .ToList();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var doc = JsonSerializer.Deserialize<Doc>(File.ReadAllText(_path));
            if (doc is null)
            {
                return;
            }

            if (DateTimeOffset.TryParse(doc.Since, out var since))
            {
                Since = since;
            }

            foreach (var (k, v) in doc.Models)
            {
                _models[k] = v;
            }
        }
        catch
        {
            // 손상/구버전 파일은 무시하고 새로 시작.
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var doc = new Doc { Since = Since.ToString("o"), Models = _models };
            File.WriteAllText(_path, JsonSerializer.Serialize(doc, Json));
        }
        catch
        {
            // 저장 실패가 REPL 을 막지 않게 한다.
        }
    }
}
