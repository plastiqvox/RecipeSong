namespace RecipeSong.Core.Soulseek;

public sealed record SoulseekPeer(string Username, bool HasFreeUploadSlot, long QueueLength, int UploadSpeed, IReadOnlyList<SoulseekFile> Files);
public sealed record SoulseekFile(string Filename, long Size, int? BitRate = null, int? SampleRate = null, int? BitDepth = null)
{
    public string Name => Filename.Replace('\\', '/').Split('/').Last();
    public string Folder => Filename.Replace('\\', '/') is var path && path.LastIndexOf('/') is var i && i >= 0 ? path[..i] : "";
    public string Format => Path.GetExtension(Name).TrimStart('.').ToUpperInvariant();
    public string Quality => string.Join(" · ", new[] { Format, BitRate is > 0 ? $"{BitRate} kbps" : null,
        SampleRate is > 0 ? $"{SampleRate / 1000d:0.#} kHz" : null, BitDepth is > 0 ? $"{BitDepth} bit" : null }.Where(s => s != null));
    public bool IsAudio => new[] { ".mp3", ".flac", ".ogg", ".opus", ".m4a", ".aac", ".wav", ".aiff", ".aif", ".wma", ".alac" }
        .Contains(Path.GetExtension(Name), StringComparer.OrdinalIgnoreCase);
}

public sealed record SoulseekTransfer(Guid Id, bool Upload, string Username, string Filename, string State, long Bytes, long Size, double Speed, string? Error)
{
    public Guid? AlbumJobId { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public string? LocalPath { get; init; }
    public string? Endpoint { get; init; }
    public int? QueuePosition { get; init; }
    public bool IsRemoteQueued { get; init; }
    public bool IsLocalQueued { get; init; }
    public bool QueueUnavailable { get; init; }
    public DateTimeOffset? QueueCheckedAt { get; init; }
    public bool HasTransferred { get; init; }
    public DateTimeOffset? WaitingSince { get; init; }
    public bool IsRewaiting => !Upload && !IsActive && !IsFinished && (HasTransferred || StartedAt.HasValue || Bytes > 0);
    public string DisplayState => IsRewaiting ? L.T("다운로드 후 다시 대기 중") : State;
    public string RewaitText => IsRewaiting && WaitingSince is { } since
        ? L.F("재대기 시작 {0:HH:mm:ss} · {1:N0}초 경과", since.ToLocalTime(), Math.Max(0, (int)(DateTimeOffset.UtcNow - since).TotalSeconds)) : "";
    public string QueueStatus => Upload || IsActive || IsFinished ? ""
        : IsRemoteQueued ? QueuePosition is > 0
            ? L.F("상대 대기열 {0:N0}번째 · 앞에 {1:N0}개", QueuePosition.Value, QueuePosition.Value - 1)
                + (QueueCheckedAt is { } time ? " · " + L.F("확인 {0:HH:mm:ss}", time.ToLocalTime()) : "")
            : QueueUnavailable ? L.T("상대방이 대기 순번을 알려주지 않았어요.") : L.T("상대 대기열 순번 확인 중…")
        : IsLocalQueued ? L.T("내 다운로드 슬롯 대기") : "";
    public bool IsActive { get; init; }
    public bool Succeeded { get; init; }
    public bool Terminal { get; init; }
    public bool HasTiming { get; init; } = true;
    public bool IsFinished => Terminal || FinishedAt.HasValue;
    public double Percent => Size > 0 ? Math.Clamp(Bytes * 100d / Size, 0, 100) : 0;
    public string Display => $"{Username} · {Path.GetFileName(Filename.Replace('\\', '/'))}\n{DisplayState} · {Bytes / 1048576d:0.0}/{Size / 1048576d:0.0} MiB · {Speed / 1024d:0} KiB/s" + (Error == null ? "" : "\n" + Error);
    public string Information
    {
        get
        {
            var elapsed = (FinishedAt ?? DateTimeOffset.UtcNow) - (StartedAt ?? CreatedAt);
            var duration = $"{Math.Max(0, (int)elapsed.TotalHours):00}:{Math.Max(0, elapsed.Minutes):00}:{Math.Max(0, elapsed.Seconds):00}";
            var lines = new List<string>
            {
                L.F("상대 사용자: {0}", Username),
                L.F("공유 경로: {0}", Filename),
                L.F("내 파일: {0}", LocalPath ?? L.T("확인 중")),
                L.F("접속 주소: {0}", Endpoint ?? L.T("연결 대기")),
            };
            if (HasTiming) lines.Add(L.F("요청: {0} · 경과 {1}", CreatedAt.ToLocalTime().ToString("yyyy.MM.dd HH:mm:ss"), duration));
            if (RewaitText.Length > 0) lines.Add(RewaitText);
            if (StartedAt is { } start) lines.Add(L.F("시작: {0}", start.ToLocalTime().ToString("yyyy.MM.dd HH:mm:ss")));
            if (FinishedAt is { } end) lines.Add(L.F("종료: {0}", end.ToLocalTime().ToString("yyyy.MM.dd HH:mm:ss")));
            if (QueuePosition is > 0 && !IsFinished) lines.Add(L.F("대기 순서: {0}", QueuePosition));
            if (Speed > 0 && !IsFinished) lines.Add(L.F("예상 {0:N0}초 남음", Math.Ceiling(Math.Max(0, Size - Bytes) / Speed)));
            return string.Join(Environment.NewLine, lines);
        }
    }
}
