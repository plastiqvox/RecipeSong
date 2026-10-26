using System.Collections.Concurrent;
using System.Text.Json;

namespace Crate.Core;

/// <summary>%LOCALAPPDATA%\Crate 아래에 저장되는 단순 키-값 캐시 (항목별 TTL).</summary>
public sealed class JsonCache<T>
{
    private sealed record Entry(DateTime At, T Value);

    private readonly ConcurrentDictionary<string, Entry> _items;
    private readonly string _file;
    private readonly TimeSpan _ttl;

    // 디스코그래피 캐시가 수십 MB라 빈 필드는 쓰지 않는다
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>캐시 폴더 (CacheLocation.Directory — 설정에서 바꿀 수 있다)</summary>
    public static string Dir => CacheLocation.Directory;

    public JsonCache(string name, TimeSpan ttl)
    {
        Directory.CreateDirectory(Dir);
        _file = Path.Combine(Dir, name + ".json");
        _ttl = ttl;
        try
        {
            _items = File.Exists(_file)
                ? new(JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_file)) ?? [])
                : new();
        }
        catch (JsonException) { _items = new(); }
    }

    public bool TryGet(string key, out T value)
    {
        if (_items.TryGetValue(key, out var e) && DateTime.UtcNow - e.At < _ttl) { value = e.Value; return true; }
        value = default!;
        return false;
    }

    /// <summary>만료된 값도 돌려준다 (fresh=false). 화면은 옛 값으로 먼저 그리고 뒤에서 새로 받을 때.</summary>
    public bool TryGetStale(string key, out T value, out bool fresh)
    {
        if (_items.TryGetValue(key, out var e)) { value = e.Value; fresh = DateTime.UtcNow - e.At < _ttl; return true; }
        value = default!;
        fresh = false;
        return false;
    }

    /// <summary>저장한 시각(로컬)도 함께</summary>
    public bool TryGetAt(string key, out T value, out DateTime savedAt)
    {
        if (_items.TryGetValue(key, out var e) && DateTime.UtcNow - e.At < _ttl)
        {
            value = e.Value;
            savedAt = e.At.ToLocalTime();
            return true;
        }
        value = default!;
        savedAt = default;
        return false;
    }

    /// <summary>만료되지 않은 값 전부 (달력 표시처럼 전체를 훑을 때)</summary>
    public IEnumerable<T> FreshValues => _items.Values.Where(e => DateTime.UtcNow - e.At < _ttl).Select(e => e.Value);

    public void Set(string key, T value) => _items[key] = new Entry(DateTime.UtcNow, value);

    public async Task<T> GetOrAddAsync(string key, Func<Task<T>> factory)
    {
        if (TryGet(key, out var v)) return v;
        v = await factory();
        Set(key, v);
        return v;
    }

    public void Save()
    {
        lock (_file)
            File.WriteAllText(_file, JsonSerializer.Serialize(_items.ToDictionary(k => k.Key, k => k.Value), WriteOptions));
    }
}

/// <summary>
/// 캐시·기록 폴더. 기본 %LOCALAPPDATA%\Crate, 설정(AppSettings.CacheDirectory)으로 바꿀 수 있다.
/// 앱 시작 때 캐시를 만들기 전에 한 번 정한다 (바꾼 위치는 다시 시작해야 적용).
/// </summary>
public static class CacheLocation
{
    public static string Default { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Crate");

    public static string Directory { get; set; } = Default;

    /// <summary>폴더 용량을 종류별로 (바이트)</summary>
    public static IReadOnlyList<(string Name, long Bytes)> Breakdown(string dir)
    {
        if (!System.IO.Directory.Exists(dir)) return [];
        static string Group(string name) => name switch
        {
            _ when name.StartsWith("deezer-") => "Deezer 디스코그래피·앨범 정보",
            _ when name.StartsWith("rec-") => "저장된 추천 결과",
            _ when name.StartsWith("ai-") => "AI 결과·사용 기록",
            "saved.json" => "저장함",
            _ when name.StartsWith("art") => "앨범아트",
            _ when name.EndsWith(".log") => "로그",
            _ => "기타",
        };
        return [.. new DirectoryInfo(dir).EnumerateFileSystemInfos()
            .Select(f => (Name: Group(f.Name), Bytes: Size(f)))
            .GroupBy(x => x.Name)
            .Select(g => (g.Key, g.Sum(x => x.Bytes)))
            .OrderByDescending(x => x.Item2)];
    }

    public static long Size(FileSystemInfo f) => f switch
    {
        FileInfo fi => fi.Length,
        DirectoryInfo di => di.EnumerateFiles("*", SearchOption.AllDirectories).Sum(x => x.Length),
        _ => 0,
    };

    public static string Format(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B",
    };
}
