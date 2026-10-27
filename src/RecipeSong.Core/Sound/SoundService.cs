using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace RecipeSong.Core.Sound;

/// <summary>
/// ml/service.py (Python, GPU)를 자식 프로세스로 띄우고 표준 입출력 JSON 한 줄씩으로 통신한다.
/// 요청 하나가 끝나야 다음 요청을 보낸다 (서비스가 순차 처리).
/// </summary>
public sealed class SoundService : IAsyncDisposable
{
    private readonly string _mlDir;
    private readonly string _uv;
    private readonly string _xml;
    private readonly ConcurrentDictionary<int, Pending> _pending = new();
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Process? _proc;
    private int _nextId;

    private sealed record Pending(TaskCompletionSource<JsonElement> Done, IProgress<string>? Progress);

    public static string LogPath => Path.Combine(CacheLocation.Directory, "sound-service.log");

    private SoundService(string mlDir, string uv, string xml) => (_mlDir, _uv, _xml) = (mlDir, uv, xml);

    public string MlDirectory => _mlDir;

    /// <summary>ml 폴더와 uv를 찾을 수 있으면 서비스를 만든다 (아직 시작하지 않음).</summary>
    public static SoundService? TryCreate(AppSettings settings)
    {
        var ml = settings.MlDirectory ?? FindMlDirectory();
        var uv = settings.UvPath ?? FindUv();
        if (ml == null || uv == null || settings.ITunesLibraryPath == null) return null;
        if (!File.Exists(Path.Combine(ml, "data", "crate_model.json"))) return null;
        return new SoundService(ml, uv, settings.ITunesLibraryPath);
    }

    public static string? FindMlDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "ml");
            if (File.Exists(Path.Combine(candidate, "service.py"))) return candidate;
        }
        return null;
    }

    public static string? FindUv()
    {
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "uv.exe");
        if (File.Exists(local)) return local;
        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => Path.Combine(p.Trim(), "uv.exe"))
            .FirstOrDefault(File.Exists);
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_proc != null) { await _ready.Task.WaitAsync(ct); return; }
        var psi = new ProcessStartInfo(_uv)
        {
            WorkingDirectory = _mlDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "run", "--quiet", "python", "service.py", "--xml", _xml }) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUNBUFFERED"] = "1";

        _proc = Process.Start(psi) ?? throw new InvalidOperationException(L.T("소리 분석 서비스를 시작하지 못했습니다."));
        _proc.StandardInput.AutoFlush = true;
        _ = Task.Run(ReadStdoutAsync);
        _ = Task.Run(ReadStderrAsync);
        // 첫 실행은 Python 패키지 확인 때문에 오래 걸릴 수 있다
        await _ready.Task.WaitAsync(TimeSpan.FromMinutes(3), ct);
    }

    public bool IsRunning => _proc is { HasExited: false } && _ready.Task.IsCompletedSuccessfully;

    public async Task<SoundStatus> StatusAsync(CancellationToken ct = default)
    {
        var r = await CallAsync("status", null, null, ct);
        return new SoundStatus(
            r.GetProperty("device").GetString()!,
            r.GetProperty("gpu").GetString(),
            DateTime.Parse(r.GetProperty("trained_at").GetString()!),
            r.GetProperty("tracks").GetInt32(),
            r.GetProperty("embedded").GetInt32(),
            r.GetProperty("new_tracks").GetInt32(),
            [.. r.GetProperty("styles").EnumerateArray().Select(e => e.GetString()!)],
            r.GetProperty("style_accuracy").GetDouble(),
            [.. r.GetProperty("regions").EnumerateArray().Select(e => e.GetString()!)],
            r.GetProperty("region_accuracy").GetDouble());
    }

    /// <summary>Deezer 앨범 ID들의 미리듣기를 분석한다. 이미 분석한 앨범은 서비스 쪽 캐시에서 바로 나온다.</summary>
    public async Task<IReadOnlyDictionary<long, AlbumSound>> ScoreAlbumsAsync(
        IEnumerable<long> albumIds, IProgress<string>? progress = null, CancellationToken ct = default,
        IReadOnlyDictionary<long, string?>? covers = null, bool hitsOnly = false)
    {
        // 커버 주소: 소리 + 커버로 지역을 판정한다 (없으면 소리만). tracks: 앨범에서 들을 곡 (전곡 / 인기곡)
        var args = new { albums = albumIds.Select(id => new { id, cover = covers?.GetValueOrDefault(id) }).ToList(), tracks = hitsOnly ? "top" : "all" };
        var r = await CallAsync("score_albums", args, progress, ct);
        var map = new Dictionary<long, AlbumSound>();
        foreach (var a in r.GetProperty("albums").EnumerateArray())
        {
            bool ok = a.GetProperty("ok").GetBoolean();
            map[a.GetProperty("id").GetInt64()] = new AlbumSound(
                ok ? ToDict(a.GetProperty("style")) : null,
                ok ? ToDict(a.GetProperty("region")) : null,
                ok ? a.GetProperty("taste").GetDouble() : null,
                ok ? [.. a.GetProperty("near").EnumerateArray().Select(e => e.GetString()!)] : [],
                Str(a, "label"), Str(a, "genre"),
                a.TryGetProperty("fav", out var fav) ? fav.GetDouble() : null,
                Str(a, "fav_near"),
                a.TryGetProperty("dis", out var dis) ? dis.GetDouble() : null,
                a.TryGetProperty("with", out var with) && with.ValueKind == JsonValueKind.Array
                    ? [.. with.EnumerateArray().Select(e => e.GetString() ?? "").Where(n => n.Length > 0)] : null,
                Pairs(a),
                a.TryGetProperty("genres", out var gs) && gs.ValueKind == JsonValueKind.Array
                    ? [.. gs.EnumerateArray().Select(e => e.GetString() ?? "").Where(n => n.Length > 0)] : null);
        }
        return map;
    }

    /// <summary>
    /// 저장함 앨범을 알려 준다. 이후 신보 점수에 "저장한 앨범과 닮은 정도"(fav)가 붙는다.
    /// 소리를 모르는 저장 앨범은 서비스가 미리듣기로 분석한다.
    /// </summary>
    /// <param name="likes">앨범 하트는 앨범 ID, 곡 하트는 "t곡ID"</param>
    public async Task<int> SetFavoritesAsync(IEnumerable<(string Key, string Name)> likes, CancellationToken ct = default,
        IEnumerable<long>? dislikes = null)
    {
        var args = new
        {
            albums = likes.Select(a => new { id = a.Key, name = a.Name }).ToList(),
            dislikes = (dislikes ?? []).Select(id => new { id }).ToList(),
        };
        var r = await CallAsync("favorites", args, null, ct);
        return r.GetProperty("count").GetInt32();
    }

    /// <summary>수록곡마다 미리듣기를 들어 가장 닮은 내 곡 (곡 id → "아티스트 – 곡명", 유사도)</summary>
    public async Task<Dictionary<long, (string Near, double Sim)>> TrackNearAsync(
        IEnumerable<(long Id, string? Preview)> tracks, CancellationToken ct = default)
    {
        var args = new { tracks = tracks.Select(t => new { id = t.Id, preview = t.Preview }).ToList() };
        var r = await CallAsync("track_near", args, null, ct);
        return r.GetProperty("tracks").EnumerateArray().ToDictionary(
            t => t.GetProperty("id").GetInt64(), t => (t.GetProperty("near").GetString()!, t.GetProperty("sim").GetDouble()));
    }

    /// <summary>iTunes에 새로 들어온 곡만 분석하고 재학습한다. 성능이 떨어지면 이전 모델을 유지한다.</summary>
    public async Task<SoundUpdateResult> UpdateAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var r = await CallAsync("update", null, progress, ct);
        return new SoundUpdateResult(
            r.GetProperty("added").GetInt32(), r.GetProperty("accepted").GetBoolean(),
            r.GetProperty("old_style_accuracy").GetDouble(), r.GetProperty("new_style_accuracy").GetDouble());
    }

    private static IReadOnlyList<TrackPair>? Pairs(JsonElement a) =>
        a.TryGetProperty("pairs", out var p) && p.ValueKind == JsonValueKind.Array
            ? [.. p.EnumerateArray().Select(x => new TrackPair(Str(x, "track") ?? "", Str(x, "near") ?? "", x.GetProperty("sim").GetDouble()))]
            : null;

    /// <summary>곡 짝 기능 전에 분석한 앨범의 짝 (없으면 미리듣기를 곡별로 다시 듣는다, 한 번만)</summary>
    public async Task<Dictionary<long, IReadOnlyList<TrackPair>>> AlbumPairsAsync(IEnumerable<long> albumIds, CancellationToken ct = default)
    {
        var r = await CallAsync("album_pairs", new { ids = albumIds.ToList() }, null, ct);
        return r.GetProperty("albums").EnumerateArray().ToDictionary(a => a.GetProperty("id").GetInt64(), a => Pairs(a) ?? []);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static Dictionary<string, double> ToDict(JsonElement e) =>
        e.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble());

    /// <summary>target 앨범과 소리가 min 이상 닮은 앨범 (높은 순)</summary>
    public async Task<List<(long Id, double Sim)>> SimilarAlbumsAsync(long target, IEnumerable<long> ids, double min = 0.85, CancellationToken ct = default)
    {
        var r = await CallAsync("similar_albums", new { target, ids = ids.ToList(), min }, null, ct);
        return [.. r.GetProperty("albums").EnumerateArray().Select(a => (a.GetProperty("id").GetInt64(), a.GetProperty("sim").GetDouble()))];
    }

    /// <summary>다른 요청을 처리하는 중 (요청은 한 번에 하나씩 — 새 요청은 줄을 선다)</summary>
    public bool IsBusy => _serial.CurrentCount == 0;

    private async Task<JsonElement> CallAsync(string cmd, object? args, IProgress<string>? progress, CancellationToken ct)
    {
        await StartAsync(ct);
        await _serial.WaitAsync(ct);
        try
        {
            int id = Interlocked.Increment(ref _nextId);
            var pending = new Pending(new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously), progress);
            _pending[id] = pending;
            var line = JsonSerializer.Serialize(new { id, cmd, args });
            await _proc!.StandardInput.WriteLineAsync(line.AsMemory(), ct);
            using var reg = ct.Register(() => pending.Done.TrySetCanceled(ct));
            return await pending.Done.Task;
        }
        finally { _serial.Release(); }
    }

    private async Task ReadStdoutAsync()
    {
        var reader = _proc!.StandardOutput;
        while (await reader.ReadLineAsync() is { } line)
        {
            JsonElement msg;
            try { msg = JsonDocument.Parse(line).RootElement.Clone(); }
            catch (JsonException) { continue; }

            if (msg.TryGetProperty("event", out var ev))
            {
                if (ev.GetString() == "ready") _ready.TrySetResult();
                else if (msg.TryGetProperty("id", out var pid) && pid.ValueKind == JsonValueKind.Number
                         && _pending.TryGetValue(pid.GetInt32(), out var p))
                    p.Progress?.Report(msg.GetProperty("message").GetString() ?? "");
                continue;
            }
            if (!msg.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number) continue;
            if (!_pending.TryRemove(idEl.GetInt32(), out var done)) continue;
            if (msg.GetProperty("ok").GetBoolean()) done.Done.TrySetResult(msg.GetProperty("result"));
            else done.Done.TrySetException(new InvalidOperationException(msg.GetProperty("error").GetString()));
        }
        // 프로세스 종료: 기다리던 요청은 모두 실패 처리
        var err = new InvalidOperationException(L.F("소리 분석 서비스가 종료되었습니다. 로그: {0}", LogPath));
        _ready.TrySetException(err);
        foreach (var p in _pending.Values) p.Done.TrySetException(err);
        _pending.Clear();
    }

    private async Task ReadStderrAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        await using var log = new StreamWriter(LogPath, append: false, Encoding.UTF8) { AutoFlush = true };
        while (await _proc!.StandardError.ReadLineAsync() is { } line)
            await log.WriteLineAsync(line);
    }

    public async ValueTask DisposeAsync()
    {
        if (_proc is not { HasExited: false }) return;
        try
        {
            await _proc.StandardInput.WriteLineAsync("{\"cmd\":\"quit\"}");
            if (!_proc.WaitForExit(3000)) _proc.Kill(entireProcessTree: true);
        }
        catch (Exception) { /* 이미 종료 */ }
    }
}
