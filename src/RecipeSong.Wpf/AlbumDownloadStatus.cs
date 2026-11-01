using System.IO;
using RecipeSong.Core;
using RecipeSong.Core.Soulseek;

namespace RecipeSong.Wpf;

public sealed class AlbumDownloadStatus
{
    public AlbumDownload Job { get; }
    public Guid Id => Job.Id;
    public string Display => Job.State == "downloading" && Tracks.Any(t => t.IsRewaiting) && !Tracks.Any(t => t.IsActive)
        ? $"{Job.Artist} — {Job.Album}\n{L.T("다운로드 재대기 중…")} {Job.Detail}" : Job.Display;
    public double Percent { get; }
    public string ProgressText { get; }
    public IReadOnlyList<SoulseekTransfer> Tracks { get; }
    public bool IsFavorite { get; init; }
    public string FavoriteDisplay => IsFavorite ? L.T("★ 즐겨찾기") : "";

    public AlbumDownloadStatus(AlbumDownload job, IReadOnlyList<SoulseekTransfer> transfers)
    {
        Job = job;
        var latest = transfers.Where(t => !t.Upload && t.AlbumJobId == job.Id)
            .GroupBy(t => t.Filename).ToDictionary(g => g.Key, g => g.MaxBy(t => t.CreatedAt)!);
        double total = job.Files.Sum(f => (double)f.Size), received = 0;
        int completed = 0;
        var tracks = new List<SoulseekTransfer>();
        var speed = latest.Values.Where(t => t.IsActive && !t.IsFinished).Sum(t => t.Speed);
        for (var i = 0; i < job.Files.Count; i++)
        {
            var file = job.Files[i];
            latest.TryGetValue(file.Filename, out var transfer);
            var bytes = transfer?.Bytes ?? 0;
            if (job.State is "packing" or "done") bytes = file.Size;
            else
            {
                // AlbumDownloads.ProcessAsync와 같은 경로. 이전 실행에서 받은 파일도 합산한다.
                try
                {
                    var path = AlbumJobFiles.DownloadPath(job, i);
                    if (File.Exists(path)) bytes = new FileInfo(path).Length;
                    else if (File.Exists(path + ".partial")) bytes = new FileInfo(path + ".partial").Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
            }
            if (job.State == "cancelled" && job.FilesRemoved) bytes = 0;
            received += Math.Clamp(bytes, 0, Math.Max(0, file.Size));
            if (bytes >= file.Size && file.Size > 0) completed++;
            var archived = job.State == "done";
            var removed = job.State == "cancelled" && job.FilesRemoved;
            var complete = bytes >= file.Size && file.Size > 0;
            if (transfer != null && !archived && !removed) tracks.Add(transfer);
            else
            {
                var key = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(job.Id.ToString("N") + file.Filename));
                tracks.Add(new(new Guid(key.AsSpan(0, 16)), false, job.Username, file.Filename,
                    archived ? L.T("ZIP에 저장됨") : removed ? L.T("중단·파일 삭제됨") : job.State == "cancelled" ? L.T("취소됨") : complete ? L.T("완료")
                    : job.State == "failed" ? L.T("전송 안 됨") : L.T("대기 중"), removed ? 0 : Math.Clamp(bytes, 0, Math.Max(0, file.Size)), file.Size, 0, null)
                {
                    AlbumJobId = job.Id, LocalPath = AlbumJobFiles.DownloadPath(job, i), HasTiming = false,
                    Terminal = archived || removed || complete || job.State is "failed" or "cancelled", Succeeded = archived || complete && !removed,
                });
            }
        }
        Tracks = tracks;
        Percent = total > 0 ? Math.Clamp(received * 100 / total, 0, 100) : 0;
        ProgressText = L.F("음원 다운로드 {0:0}% · {1}/{2}곡 · {3:0.0}/{4:0.0} MiB · {5:0} KiB/s",
            Percent, completed, job.Files.Count, received / 1048576d, total / 1048576d, speed / 1024d);
        if (speed > 0 && job.State == "downloading") ProgressText += " · " + L.F("예상 {0:N0}초 남음", Math.Ceiling(Math.Max(0, total - received) / speed));
        var rewaiting = Tracks.Count(t => t.IsRewaiting);
        if (job.State == "downloading" && rewaiting > 0) ProgressText += " · " + L.F("재대기 {0}곡", rewaiting);
    }
}
