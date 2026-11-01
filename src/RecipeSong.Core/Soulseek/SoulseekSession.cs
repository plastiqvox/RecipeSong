using System.Collections.Concurrent;
using Slsk = global::Soulseek;

namespace RecipeSong.Core.Soulseek;

/// <summary>RecipeSong 프로세스 안에서 로그인·검색·파일 전송을 처리한다.</summary>
public sealed class SoulseekSession : IAsyncDisposable
{
    private Slsk.SoulseekClient? _client;
    private readonly SemaphoreSlim _connection = new(1, 1);
    private CancellationTokenSource _transfers = new();
    private readonly ConcurrentDictionary<Guid, (SoulseekTransfer Row, CancellationTokenSource Cancel)> _rows = new();
    private readonly ConcurrentDictionary<Guid, Task> _running = new();
    private readonly ConcurrentDictionary<(string User, string Remote), Guid> _uploads = new();
    private readonly List<Guid> _uploadQueue = [];
    private readonly SemaphoreSlim _queueQueries = new(2, 2);
    private string[] _excluded = [];
    private SoulseekSettings _settings = SoulseekSettings.Load();
    public ShareIndex Shares { get; } = new();
    public TransferSlots Downloads { get; } = new();
    public TransferSlots Uploads { get; } = new();
    public bool IsLoggedIn => _client?.State.HasFlag(Slsk.SoulseekClientStates.LoggedIn) == true;
    public string Username { get; private set; } = "";
    public string Status { get; private set; } = L.T("로그인 안 됨");
    public IReadOnlyList<SoulseekTransfer> Transfers => _rows.Values.Select(v => v.Row.Upload && !v.Row.IsFinished && !v.Row.IsActive
        ? v.Row with { QueuePosition = QueuePosition(v.Row.Username, v.Row.Filename) } : v.Row).OrderBy(v => v.CreatedAt).ToArray();

    public async Task LoginAsync(SoulseekSettings settings, string password, CancellationToken ct)
    {
        settings.Validate();
        if (string.IsNullOrWhiteSpace(settings.Username) || password.Length == 0) throw new ArgumentException(L.T("Soulseek ID와 비밀번호를 입력하세요."));
        await _connection.WaitAsync(ct);
        try
        {
            if (IsLoggedIn && Username == settings.Username) { await ApplyAsync(settings, ct); return; }
            Logout();
            _settings = settings;
            ConfigureSlots();
            Status = L.T("Soulseek 연결 중…");
            _client = new Slsk.SoulseekClient(8100001, new Slsk.SoulseekClientOptions(
                listenPort: settings.ListenPort, maximumConcurrentDownloads: 100, maximumConcurrentUploads: 100,
                maximumDownloadSpeed: Limit(settings.DownloadKiB), maximumUploadSpeed: Limit(settings.UploadKiB),
                acceptDistributedChildren: false,
                browseResponseResolver: (_, _) => Task.FromResult(Shares.Browse()),
                directoryContentsResolver: (_, _, _, name) => Task.FromResult(Shares.Browse().Directories.Where(d => d.Name == name)),
                searchResponseResolver: (_, token, query) => Task.FromResult(new Slsk.SearchResponse(settings.Username, token, Uploads.HasFree,
                    0, Uploads.Waiting, Shares.Search(query, Volatile.Read(ref _excluded)))),
                userInfoResolver: (_, _) => Task.FromResult(new Slsk.UserInfo("RecipeSong", settings.UploadSlots, Uploads.Waiting, Uploads.HasFree)),
                enqueueDownload: (user, _, remote) => EnqueueUploadAsync(user, remote),
                placeInQueueResolver: (user, _, remote) => Task.FromResult<int?>(QueuePosition(user, remote))));
            _client.Disconnected += (_, _) => Status = L.T("Soulseek 연결 끊김");
            _client.ExcludedSearchPhrasesReceived += (_, e) => Volatile.Write(ref _excluded, e.ToArray());
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await _client.ConnectAsync(settings.Username, password, timeout.Token);
            Username = settings.Username;
            Status = L.F("로그인됨 · {0}", Username);
            try { await RescanAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        }
        catch { Status = L.T("Soulseek 로그인 실패"); _client?.Dispose(); _client = null; throw; }
        finally { _connection.Release(); }
    }

    private static int Limit(int kib) => kib == 0 ? int.MaxValue : kib;
    private void ConfigureSlots() { Downloads.Configure(_settings.DownloadSlots, _settings.DownloadsPerUser); Uploads.Configure(_settings.UploadSlots, _settings.UploadsPerUser); }

    public async Task ApplyAsync(SoulseekSettings settings, CancellationToken ct)
    {
        settings.Validate();
        _settings = settings;
        ConfigureSlots();
        Shares.RestrictTo(settings.SharedDirectories);
        if (_client != null) await _client.ReconfigureOptionsAsync(new Slsk.SoulseekClientOptionsPatch(
            maximumDownloadSpeed: Limit(settings.DownloadKiB), maximumUploadSpeed: Limit(settings.UploadKiB)), ct);
        await RescanAsync(ct);
    }

    public async Task RescanAsync(CancellationToken ct)
    {
        await Shares.ScanAsync(_settings.SharedDirectories, ct);
        if (IsLoggedIn) await _client!.SetSharedCountsAsync(Shares.DirectoryCount, Shares.Files.Count, ct);
    }

    public async Task SearchAsync(string query, Action<SoulseekPeer> response, CancellationToken ct)
    {
        var client = RequireLogin();
        await client.SearchAsync(Slsk.SearchQuery.FromText(query), r => response(new(r.Username, r.HasFreeUploadSlot, r.QueueLength, r.UploadSpeed,
            r.Files.Select(f => new SoulseekFile(f.Filename, f.Size, f.BitRate, f.SampleRate, f.BitDepth)).ToArray())),
            options: new Slsk.SearchOptions(searchTimeout: 15000, responseLimit: 100, fileLimit: 3000), cancellationToken: ct);
    }

    private Slsk.SoulseekClient RequireLogin() => IsLoggedIn ? _client! : throw new InvalidOperationException(L.T("먼저 Soulseek에 로그인하세요."));

    public async Task DownloadAsync(string user, SoulseekFile file, string target, CancellationToken ct, Guid? albumJobId = null)
    {
        var client = RequireLogin();
        var id = Guid.NewGuid();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct, _transfers.Token);
        _rows[id] = (new SoulseekTransfer(id, false, user, file.Filename, L.T("대기 중"), 0, file.Size, 0, null)
            { AlbumJobId = albumJobId, LocalPath = target }, cancel);
        using var queueLife = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
        var queueUpdates = UpdateDownloadQueueAsync(client, id, queueLife.Token);
        try
        {
            var partial = target + ".partial";
            var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            if (offset > file.Size) throw new IOException(L.T("미완료 파일이 원본보다 커요. 원본 폴더를 확인하세요."));
            if (offset > 0) ChangeRow(id, row => row with { Bytes = offset, HasTransferred = true, WaitingSince = DateTimeOffset.UtcNow });
            var result = await client.DownloadAsync(user, file.Filename, partial, size: file.Size, startOffset: offset,
                options: Options(id, Downloads), cancellationToken: cancel.Token);
            if (!result.State.HasFlag(Slsk.TransferStates.Succeeded) || new FileInfo(partial).Length != file.Size) throw new IOException(L.T("다운로드 파일 크기가 맞지 않아요."));
            File.Move(partial, target, overwrite: false);
        }
        catch (Exception ex) { Failed(id, ex); throw; }
        finally { queueLife.Cancel(); await queueUpdates; }
    }

    private async Task UpdateDownloadQueueAsync(Slsk.SoulseekClient client, Guid id, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                if (!_rows.TryGetValue(id, out var current) || current.Row.IsFinished) return;
                if (!current.Row.IsRemoteQueued || current.Row.IsActive) continue;
                await _queueQueries.WaitAsync(ct);
                try
                {
                    if (!_rows.TryGetValue(id, out current) || !current.Row.IsRemoteQueued || current.Row.IsActive || current.Row.IsFinished) continue;
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    int? position = null;
                    try { position = await client.GetDownloadPlaceInQueueAsync(current.Row.Username, current.Row.Filename, timeout.Token); }
                    catch (Exception) when (!ct.IsCancellationRequested) { }
                    ChangeRow(id, row => row.IsRemoteQueued && !row.IsActive && !row.IsFinished
                        ? row with { QueuePosition = position is > 0 ? position : null, QueueUnavailable = position is not > 0, QueueCheckedAt = DateTimeOffset.UtcNow } : row);
                }
                finally { _queueQueries.Release(); }
                await Task.Delay(TimeSpan.FromSeconds(13), ct);
            }
        }
        catch (Exception) when (ct.IsCancellationRequested) { }
    }

    private void ChangeRow(Guid id, Func<SoulseekTransfer, SoulseekTransfer> update)
    {
        // 대기 순번 조회와 전송 콜백이 겹쳐도 완료 상태를 잃거나 삭제 기록을 되살리지 않는다.
        while (_rows.TryGetValue(id, out var previous))
        {
            var row = update(previous.Row);
            if (ReferenceEquals(row, previous.Row) || _rows.TryUpdate(id, (row, previous.Cancel), previous)) return;
        }
    }

    private Slsk.TransferOptions Options(Guid id, TransferSlots slots) => new(
        slotAwaiter: async (t, ct) =>
        {
            if (ReferenceEquals(slots, Downloads)) ChangeRow(id, row => row with
            {
                IsLocalQueued = true,
                WaitingSince = row.HasTransferred || row.StartedAt.HasValue || row.Bytes > 0 ? row.WaitingSince ?? DateTimeOffset.UtcNow : null,
            });
            try { await slots.AcquireAsync(t.Username, ct); }
            finally { if (ReferenceEquals(slots, Downloads)) ChangeRow(id, row => row with { IsLocalQueued = false }); }
            if (ReferenceEquals(slots, Uploads)) lock (_uploadQueue) _uploadQueue.Remove(id);
        }, slotReleased: t => slots.Release(t.Username),
        stateChanged: p => Update(id, p.Transfer), progressUpdated: p => Update(id, p.Transfer));

    private void Update(Guid id, Slsk.Transfer transfer) => ChangeRow(id, previous => ApplyTransferUpdate(previous, transfer));

    internal static SoulseekTransfer ApplyTransferUpdate(SoulseekTransfer previous, Slsk.Transfer transfer)
    {
        if (previous.IsFinished) return previous;
        var active = transfer.State.HasFlag(Slsk.TransferStates.InProgress);
        var finished = transfer.State.HasFlag(Slsk.TransferStates.Completed);
        var queued = transfer.State.HasFlag(Slsk.TransferStates.Queued) && !transfer.State.HasFlag(Slsk.TransferStates.Locally)
            && !transfer.State.HasFlag(Slsk.TransferStates.InProgress) && !transfer.State.HasFlag(Slsk.TransferStates.Completed);
        return previous with
        {
            State = transfer.State.HasFlag(Slsk.TransferStates.Succeeded) ? L.T("완료")
                : transfer.State.HasFlag(Slsk.TransferStates.InProgress) ? L.T("전송 중")
                : transfer.State.HasFlag(Slsk.TransferStates.Completed) ? L.T("실패") : L.T("대기 중"),
            Bytes = !previous.Upload && !active && !finished ? Math.Max(previous.Bytes, transfer.BytesTransferred) : transfer.BytesTransferred,
            Size = transfer.Size, Speed = active ? transfer.AverageSpeed : 0,
            HasTransferred = previous.HasTransferred || active || transfer.BytesTransferred > 0,
            WaitingSince = active || finished ? null : previous.HasTransferred || previous.StartedAt.HasValue || previous.Bytes > 0 || transfer.BytesTransferred > 0
                ? previous.WaitingSince ?? DateTimeOffset.UtcNow : null,
            StartedAt = transfer.StartTime is { } start ? start.ToUniversalTime() : previous.StartedAt,
            FinishedAt = transfer.State.HasFlag(Slsk.TransferStates.Completed) ? DateTimeOffset.UtcNow : previous.FinishedAt,
            Endpoint = transfer.IPEndPoint?.ToString() ?? previous.Endpoint,
            IsRemoteQueued = !previous.Upload && queued,
            QueuePosition = queued && previous.IsRemoteQueued ? previous.QueuePosition : null,
            QueueCheckedAt = queued && previous.IsRemoteQueued ? previous.QueueCheckedAt : null,
            QueueUnavailable = queued && previous.IsRemoteQueued && previous.QueueUnavailable,
            IsActive = transfer.State.HasFlag(Slsk.TransferStates.InProgress),
            Succeeded = transfer.State.HasFlag(Slsk.TransferStates.Succeeded),
        };
    }

    private void Failed(Guid id, Exception ex)
    {
        ChangeRow(id, previous => previous with
        { State = ex is OperationCanceledException ? L.T("취소됨") : L.T("실패"), Error = ex is OperationCanceledException ? null : ex.Message,
            Speed = 0, IsActive = false, Succeeded = false, FinishedAt = DateTimeOffset.UtcNow });
    }

    private Task EnqueueUploadAsync(string user, string remote)
    {
        var file = Shares.Files.SingleOrDefault(f => f.Remote == remote) ?? throw new FileNotFoundException("File not shared");
        var client = RequireLogin();
        var id = Guid.NewGuid();
        if (!_uploads.TryAdd((user, remote), id)) return Task.CompletedTask;
        lock (_uploadQueue) _uploadQueue.Add(id);
        var cancel = CancellationTokenSource.CreateLinkedTokenSource(_transfers.Token);
        _rows[id] = (new SoulseekTransfer(id, true, user, remote, L.T("대기 중"), 0, file.Size, 0, null) { LocalPath = file.Local }, cancel);
        var task = SendAsync();
        _running[id] = task;
        _ = task.ContinueWith(_ =>
        {
            _running.TryRemove(id, out var ignored);
            _uploads.TryRemove((user, remote), out var old);
            lock (_uploadQueue) _uploadQueue.Remove(id);
            cancel.Dispose();
        }, TaskScheduler.Default);
        return Task.CompletedTask;

        async Task SendAsync()
        {
            try { await client.UploadAsync(user, remote, file.Size, offset => Task.FromResult(Shares.OpenRead(remote, offset)), options: Options(id, Uploads), cancellationToken: cancel.Token); }
            catch (Exception ex) { Failed(id, ex); }
        }
    }

    public void Cancel(Guid id)
    {
        if (_rows.TryGetValue(id, out var row)) { try { row.Cancel.Cancel(); } catch (ObjectDisposedException) { } }
    }

    public void DeleteDownloadHistory(IReadOnlySet<Guid> albumIds, bool includeUnassociated)
    {
        foreach (var pair in _rows)
        {
            var row = pair.Value.Row;
            if (!row.Upload && row.IsFinished && (row.AlbumJobId is { } album ? albumIds.Contains(album) : includeUnassociated))
                _rows.TryRemove(pair.Key, out var ignored);
        }
    }

    private int QueuePosition(string user, string remote)
    {
        if (!_uploads.TryGetValue((user, remote), out var id)) return 0;
        lock (_uploadQueue) return _uploadQueue.IndexOf(id) + 1;
    }

    public void Logout()
    {
        _transfers.Cancel();
        _transfers.Dispose();
        _transfers = new();
        _client?.Disconnect();
        _client?.Dispose();
        _client = null;
        Username = "";
        Status = L.T("로그인 안 됨");
    }

    public async ValueTask DisposeAsync()
    {
        Logout();
        await Task.WhenAll(_running.Values);
        _transfers.Dispose();
    }
}
