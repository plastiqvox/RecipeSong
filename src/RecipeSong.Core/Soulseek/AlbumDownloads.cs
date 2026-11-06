using System.Text.Json;

namespace RecipeSong.Core.Soulseek;

public sealed class AlbumDownload
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public string RecordType { get; set; } = "";
    public DateOnly ReleaseDate { get; set; }
    public string? CoverUrl { get; set; }
    public string Username { get; set; } = "";
    public string Owner { get; set; } = "";
    public List<SoulseekFile> Files { get; set; } = [];
    public string DownloadsDirectory { get; set; } = "";
    public string ArchiveDirectory { get; set; } = "";
    public string FfmpegPath { get; set; } = "";
    public string State { get; set; } = "waiting";
    public string Detail { get; set; } = "";
    public string? ArchivePath { get; set; }
    public ReleaseRecommendation? Recommendation { get; set; }
    public string CleanupError { get; set; } = "";
    public bool FilesRemoved { get; set; }
    public bool CleanupPending { get; set; }
    public DateTimeOffset? CleanupAttemptedAt { get; set; }
    public DateTimeOffset? CleanupCompletedAt { get; set; }
    public string CleanupDisplay => FilesRemoved ? L.T("파일 정리 완료") : CleanupPending ? L.T("파일 정리 대기") : "";
    public string Display => $"{Artist} — {Album}\n{L.T(State switch { "done" => "ZIP 저장 완료", "failed" => "처리 실패", "cancelled" => "취소됨", "packing" => "MP3 변환·앨범아트·ZIP 처리 중…", "downloading" => "다운로드 중…", _ => "다운로드 대기·진행 중…" })}  {Detail}"
        + (CleanupDisplay.Length == 0 ? "" : "\n" + CleanupDisplay)
        + (CleanupError.Length == 0 ? "" : "\n" + L.F("파일 정리 실패: {0}", CleanupError));
}

/// <summary>직접 받은 음원의 완료 확인·변환·압축과 앱 재실행 후 재개를 담당한다.</summary>
public sealed class AlbumDownloads : IAsyncDisposable
{
    private readonly List<AlbumDownload> _jobs;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _life = new();
    private readonly Dictionary<Guid, CancellationTokenSource> _cancels = [];
    private readonly Dictionary<Guid, TaskCompletionSource> _completions = [];
    private readonly HashSet<Guid> _explicitStops = [];
    private readonly SemaphoreSlim _packing = new(1, 1);
    private readonly Func<bool> _isLoggedIn;
    private readonly Func<string> _username;
    private readonly Func<AlbumDownload, CancellationToken, Task> _process;
    private readonly Func<string, SoulseekFile, string, CancellationToken, Guid?, Task> _download;
    private readonly Task _worker;
    private readonly string _path = Path.Combine(JsonCache<object>.Dir, "soulseek-native-downloads.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    // 나를 차단한 상대 (검색 결과에 표시). 차단은 풀릴 수 있어 180일 뒤 잊고, 그 상대에게서 앨범을 다 받으면 지운다.
    private readonly JsonCache<bool> _bannedBy = new("soulseek-banned-by", TimeSpan.FromDays(180));
    public SoulseekSession Session { get; } = new();

    /// <summary>이 상대가 나를 차단했다고 응답한 시각(로컬). 기록이 없으면 null</summary>
    public DateTime? BannedMeAt(string user) => _bannedBy.TryGetAt(user, out _, out var at) ? at : null;
    public string? StartupError { get; private set; }

    public AlbumDownloads() : this(null, null, null) { }

    // 실제 서버 없이 작업 배치·종료 대기를 검사할 때 실행부를 바꾼다.
    internal AlbumDownloads(Func<bool>? isLoggedIn, Func<string>? username, Func<AlbumDownload, CancellationToken, Task>? process,
        Func<string, SoulseekFile, string, CancellationToken, Guid?, Task>? download = null)
    {
        _isLoggedIn = isLoggedIn ?? (() => Session.IsLoggedIn);
        _username = username ?? (() => Session.Username);
        _process = process ?? ProcessAsync;
        _download = download ?? Session.DownloadAsync;
        _jobs = File.Exists(_path) ? JsonSerializer.Deserialize<List<AlbumDownload>>(File.ReadAllText(_path)) ?? [] : [];
        foreach (var job in _jobs.Where(j => j.State is "packing" or "downloading")) job.State = "waiting";
        foreach (var job in _jobs.Where(j => !j.FilesRemoved && (j.State == "done" || j.CleanupError.Length > 0))) job.CleanupPending = true;
        _worker = RunAsync();
    }

    public async Task<List<AlbumDownload>> SnapshotAsync()
    {
        await _gate.WaitAsync();
        try { return JsonSerializer.Deserialize<List<AlbumDownload>>(JsonSerializer.Serialize(_jobs))!; }
        finally { _gate.Release(); }
    }

    private void Save(IReadOnlyList<AlbumDownload>? jobs = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(jobs ?? _jobs, Json));
        File.Move(_path + ".tmp", _path, overwrite: true);
    }

    public async Task QueueAsync(AlbumDownload job, CancellationToken ct)
    {
        if (!Session.IsLoggedIn) throw new InvalidOperationException(L.T("먼저 Soulseek에 로그인하세요."));
        if (job.Files.Count == 0 || job.Files.Any(f => !f.IsAudio)) throw new ArgumentException(L.T("음원 파일을 선택하세요."));
        if (job.Files.DistinctBy(f => f.Filename).Count() != job.Files.Count) throw new ArgumentException(L.T("같은 파일을 두 번 선택했어요."));
        await _gate.WaitAsync(ct);
        try
        {
            if (_jobs.Any(j => j.State is "waiting" or "packing" or "downloading" && j.Artist == job.Artist && j.Album == job.Album))
                throw new InvalidOperationException(L.T("이 앨범은 이미 처리 중이에요."));
            job.Owner = Session.Username;
            _jobs.Add(job);
            Save();
        }
        finally { _gate.Release(); }
    }

    public async Task RetryAsync(Guid id, SoulseekSettings settings, string? coverUrl = null)
    {
        await _gate.WaitAsync();
        try
        {
            var job = _jobs.Single(j => j.Id == id);
            if (job.State is "done" or "packing" or "downloading" || _cancels.ContainsKey(id)) return;
            job.FfmpegPath = settings.ResolveFfmpeg();
            job.ArchiveDirectory = settings.ArchiveDirectory;
            if (!string.IsNullOrWhiteSpace(coverUrl)) job.CoverUrl = coverUrl;
            job.State = "waiting";
            job.Detail = "";
            job.CleanupError = "";
            job.FilesRemoved = false;
            job.CleanupPending = false;
            job.CleanupAttemptedAt = job.CleanupCompletedAt = null;
            Session.DeleteDownloadHistory(new HashSet<Guid> { id }, includeUnassociated: false);
            Save();
        }
        finally { _gate.Release(); }
    }

    public async Task CancelAsync(Guid id)
    {
        Task? completion = null;
        await _gate.WaitAsync();
        try
        {
            var job = _jobs.SingleOrDefault(j => j.Id == id);
            if (job == null || job.State == "done") return;
            if (_cancels.TryGetValue(id, out var cancel))
            {
                completion = _completions[id].Task;
                _explicitStops.Add(id);
                cancel.Cancel();
            }
            else
            {
                job.State = "cancelled";
                job.Detail = "";
                CleanupFiles(job);
                Save();
            }
        }
        finally { _gate.Release(); }
        if (completion != null) await completion;
    }

    public async Task SetRecommendationAsync(Guid id, ReleaseRecommendation recommendation)
    {
        await _gate.WaitAsync();
        try { _jobs.Single(j => j.Id == id).Recommendation = recommendation; Save(); }
        finally { _gate.Release(); }
    }

    private void CleanupFiles(AlbumDownload job)
    {
        job.CleanupPending = true;
        job.CleanupAttemptedAt = DateTimeOffset.UtcNow;
        Save(); // 파일을 지우기 전에 정리 의도를 기록한다. 중간에 종료돼도 다시 시도한다.
        try
        {
            if (AlbumJobFiles.HasFiles(job))
            {
                if (job.State == "done")
                {
                    if (job.ArchivePath == null || !File.Exists(job.ArchivePath)) throw new IOException(L.T("완료 ZIP을 찾을 수 없어 원본을 보관했어요."));
                    using var zip = System.IO.Compression.ZipFile.OpenRead(job.ArchivePath);
                    if (zip.GetEntry("cover.jpg") is not { Length: > 0 }
                        || zip.Entries.Count(e => e.Name.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) && e.Length > 0) != job.Files.Count)
                        throw new IOException(L.T("완료 ZIP을 확인하지 못해 원본을 보관했어요."));
                }
                AlbumJobFiles.Cleanup(job);
            }
            job.CleanupError = ""; job.FilesRemoved = true; job.CleanupPending = false; job.CleanupCompletedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { job.CleanupError = ex.Message; job.FilesRemoved = false; }
    }

    public async Task<int> RetryCleanupAsync(Guid? selectedId = null)
    {
        await _gate.WaitAsync();
        try
        {
            var count = 0;
            foreach (var job in _jobs.Where(j => (selectedId == null || j.Id == selectedId) && !_cancels.ContainsKey(j.Id)
                && (j.State == "done" || j.State == "cancelled" && (j.CleanupPending || selectedId != null)) && !j.FilesRemoved))
            {
                CleanupFiles(job);
                Save();
                if (job.FilesRemoved) count++;
            }
            return count;
        }
        finally { _gate.Release(); }
    }

    private static bool CanForgetFiles(AlbumDownload job)
    {
        try { return job.FilesRemoved || !AlbumJobFiles.HasFiles(job); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    /// <summary>종료된 작업의 기록만 삭제한다. 음원·ZIP·변환 파일은 삭제하지 않는다.</summary>
    public async Task<int> DeleteHistoryAsync(Guid? selectedId = null)
    {
        await _gate.WaitAsync();
        try
        {
            var ids = _jobs.Where(j => (selectedId == null || j.Id == selectedId)
                && (j.State is "done" or "failed" or "cancelled") && !_cancels.ContainsKey(j.Id) && CanForgetFiles(j)).Select(j => j.Id).ToHashSet();
            if (ids.Count > 0)
            {
                var remaining = _jobs.Where(j => !ids.Contains(j.Id)).ToList();
                Save(remaining); // 저장 실패 때 화면의 기록도 그대로 보존한다.
                _jobs.RemoveAll(j => ids.Contains(j.Id));
            }
            Session.DeleteDownloadHistory(ids, includeUnassociated: selectedId == null);
            return ids.Count;
        }
        finally { _gate.Release(); }
    }

    private async Task RunAsync()
    {
        var ct = _life.Token;
        var running = new Dictionary<Guid, Task>();
        async Task Observe(Task task)
        {
            try { await task; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex) { StartupError = ex.Message; }
        }
        async Task ScheduledCleanup()
        {
            try { await RetryCleanupAsync(); }
            catch (Exception ex) when (ex is not OperationCanceledException) { StartupError = ex.Message; }
        }
        try
        {
            await Task.Yield();
            await ScheduledCleanup();
            var nextCleanup = DateTimeOffset.UtcNow.AddSeconds(30);
            var settings = SoulseekSettings.Load();
            if (settings.AutoConnect)
            {
                try { await Session.LoginAsync(settings, settings.GetPassword(), ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { StartupError = ex.Message; }
            }
            while (true)
            {
                await Task.Delay(1000, ct);
                if (DateTimeOffset.UtcNow >= nextCleanup)
                {
                    await ScheduledCleanup();
                    nextCleanup = DateTimeOffset.UtcNow.AddSeconds(30);
                }
                foreach (var pair in running.Where(p => p.Value.IsCompleted).ToArray())
                {
                    await Observe(pair.Value);
                    running.Remove(pair.Key);
                }
                List<AlbumDownload> pending;
                await _gate.WaitAsync(ct);
                try { pending = _jobs.Where(j => j.State == "waiting" && !running.ContainsKey(j.Id)).ToList(); }
                finally { _gate.Release(); }
                foreach (var job in pending)
                    if (_isLoggedIn() && job.Owner == _username() || AlbumJobFiles.AllDownloadsPresent(job)) running.Add(job.Id, _process(job, ct));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { foreach (var task in running.Values) await Observe(task); }
    }

    private async Task ProcessAsync(AlbumDownload job, CancellationToken ct)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var registered = false;
        string? refusal = null;
        try
        {
            await _gate.WaitAsync(ct);
            try
            {
                if (job.State != "waiting" || !(_isLoggedIn() && job.Owner == _username()) && !AlbumJobFiles.AllDownloadsPresent(job)) return;
                _cancels[job.Id] = cancel;
                _completions[job.Id] = new(TaskCreationOptions.RunContinuationsAsynchronously);
                registered = true;
                job.State = "downloading";
                Save();
            }
            finally { _gate.Release(); }
            var root = Path.Combine(job.DownloadsDirectory, job.Id.ToString("N"));
            Directory.CreateDirectory(root);
            var paths = job.Files.Select((_, i) => AlbumJobFiles.DownloadPath(job, i)).ToArray();
            await Task.WhenAll(job.Files.Select(async (file, index) =>
            {
                var path = paths[index];
                if (File.Exists(path))
                {
                    if (new FileInfo(path).Length != file.Size) throw new IOException(L.T("다운로드 파일 크기가 맞지 않아요."));
                    return;
                }
                // 곡 하나가 실패해도 나머지 파일 요청은 끝까지 처리한다. 직접 중단만 공통 토큰을 취소한다.
                if (!_isLoggedIn() || job.Owner != _username()) throw new InvalidOperationException(L.T("받을 곡이 남아 있어요. 해당 Soulseek 계정으로 로그인한 뒤 다시 시도하세요."));
                try { await _download(job.Username, file, path, cancel.Token, job.Id); }
                catch (Exception ex) when (PeerFailure(ex) is { StopAlbum: true } stop)
                {
                    // 이 상대로는 앨범을 끝낼 수 없으니 남은 곡을 기다리지 않는다 (받던 파일은 .partial 로 남아 다시 시도 때 이어 받는다)
                    Interlocked.CompareExchange(ref refusal, stop.Text, null);
                    if (stop.Banned) RememberBan(job.Username, true);
                    cancel.Cancel();
                    throw;
                }
            }));
            await _gate.WaitAsync(ct);
            try { job.State = "packing"; Save(); }
            finally { _gate.Release(); }
            await _packing.WaitAsync(cancel.Token);
            string zip;
            try { zip = await Task.Run(() => AlbumArchive.CreateAsync(job, paths, cancel.Token), cancel.Token); }
            finally { _packing.Release(); }
            await _gate.WaitAsync(ct);
            try { job.ArchivePath = zip; job.Detail = zip; job.State = "done"; }
            finally { _gate.Release(); }
            RememberBan(job.Username, false);
        }
        catch (OperationCanceledException)
        {
            await _gate.WaitAsync();
            try
            {
                if (registered) job.State = _explicitStops.Contains(job.Id) ? "cancelled" : refusal != null ? "failed" : ct.IsCancellationRequested ? "waiting" : "cancelled";
                if (registered && job.State == "failed") job.Detail = refusal!;
            }
            finally { _gate.Release(); }
        }
        catch (Exception ex)
        {
            await _gate.WaitAsync();
            try { if (registered) { job.State = _explicitStops.Contains(job.Id) ? "cancelled" : "failed"; job.Detail = job.State == "failed" ? refusal ?? PeerFailure(ex)?.Text ?? ex.Message : ""; } }
            finally { _gate.Release(); }
        }
        finally
        {
            if (registered)
            {
                await _gate.WaitAsync();
                Exception? finishError = null;
                try
                {
                    if (job.State == "done" || job.State == "cancelled" && _explicitStops.Contains(job.Id)) CleanupFiles(job);
                    _cancels.Remove(job.Id);
                    _explicitStops.Remove(job.Id);
                    Save();
                }
                catch (Exception ex) { finishError = ex; throw; }
                finally
                {
                    _cancels.Remove(job.Id);
                    _explicitStops.Remove(job.Id);
                    _completions.Remove(job.Id, out var completion);
                    if (finishError != null && completion != null) { completion.TrySetException(finishError); _ = completion.Task.Exception; }
                    else completion?.TrySetResult();
                    _gate.Release();
                }
            }
        }
    }

    // 차단 기록은 검색 결과 표시용이라 저장에 실패해도 받기 결과를 바꾸지 않는다
    private void RememberBan(string user, bool banned)
    {
        try
        {
            if (banned) _bannedBy.Set(user, true);
            else if (!_bannedBy.Remove(user)) return;
            _bannedBy.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// 상대 쪽 거절·오류를 앨범 안내로 바꾼다. 상대가 보낸 사유는 "Transfer rejected: Banned" 처럼 메시지 끝에 붙는다.
    /// StopAlbum 이면 이 상대로는 지금 앨범을 끝낼 수 없어(차단·공유 해제·파일 변경·접속 끊김) 남은 곡을 기다리지 않는다.
    /// 대기열 한도·연결 끊김은 다시 시도하면 풀릴 수 있어 남은 곡은 계속 받는다. 모르는 사유는 null (원문을 그대로 보여 준다).
    /// Banned 면 그 상대를 기록해 검색 결과에 표시한다.
    /// </summary>
    internal static (string Text, bool StopAlbum, bool Banned)? PeerFailure(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
            switch (e)
            {
                case global::Soulseek.TransferRejectedException:
                    bool Says(string reason) => e.Message.Contains(reason, StringComparison.OrdinalIgnoreCase);
                    return Says("Banned") || Says("Blocked") ? (L.T("상대가 내 계정을 차단했어요. ‘다른 상대 찾기’로 다른 상대에게서 받으세요."), true, true)
                        : Says("not shared") || Says("Disallowed") ? (L.T("상대가 이 곡을 더 이상 공유하지 않아요. ‘다른 상대 찾기’로 다른 상대에게서 받으세요."), true, false)
                        : Says("shutdown") ? (L.T("상대가 접속을 끊는 중이에요. 나중에 ‘다시 시도’하거나 ‘다른 상대 찾기’를 쓰세요."), true, false)
                        : Says("Too many") ? (L.T("상대가 한 사람에게 내주는 대기열이 꽉 찼어요. 받은 곡이 끝난 뒤 ‘다시 시도’하세요."), false, false)
                        : null;
                case global::Soulseek.TransferSizeMismatchException:
                    return (L.T("상대 파일이 바뀌었어요. ‘다른 상대 찾기’로 다시 고르세요."), true, false);
                case global::Soulseek.UserOfflineException:
                    return (L.T("상대가 접속해 있지 않아요. 나중에 ‘다시 시도’하거나 ‘다른 상대 찾기’를 쓰세요."), true, false);
                case global::Soulseek.ConnectionException or global::Soulseek.UserEndPointException or global::Soulseek.DownloadEnqueueException
                    or global::Soulseek.TransferReportedFailedException or TimeoutException:
                    return (L.T("상대와 연결이 끊겼어요. ‘다시 시도’하면 받은 데까지 이어 받아요."), false, false);
            }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        _life.Cancel();
        await _worker;
        await Session.DisposeAsync();
        _packing.Dispose();
        _life.Dispose();
    }
}
