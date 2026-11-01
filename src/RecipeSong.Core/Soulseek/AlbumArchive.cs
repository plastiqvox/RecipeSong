using System.Diagnostics;
using System.IO.Compression;

namespace RecipeSong.Core.Soulseek;

public static class AlbumArchive
{
    public static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return clean.Length == 0 ? "_" : clean;
    }

    public static string ArchiveName(string artist, string album, string type, DateOnly date) =>
        SafeName($"[{date:yyyy.MM.dd}] ({type.ToLowerInvariant() switch { "album" => "Album", "ep" => "EP", "single" => "Single", _ => type }}) {artist} - {album}") + ".zip";

    /// <summary>자기 작업 폴더에서만 변환한다. 완료 ZIP은 같은 볼륨의 임시 파일을 이동해 공개한다.</summary>
    public static async Task<string> CreateAsync(AlbumDownload job, IReadOnlyList<string> sources, CancellationToken ct)
    {
        if (sources.Count == 0) throw new InvalidOperationException(L.T("압축할 음원이 없어요."));
        Directory.CreateDirectory(job.ArchiveDirectory);
        var work = Path.Combine(job.ArchiveDirectory, ".recipesong-" + job.Id.ToString("N"));
        Directory.CreateDirectory(work);
        var cover = Path.Combine(work, "cover.jpg");
        if (job.CoverUrl != null && Path.IsPathFullyQualified(job.CoverUrl) && File.Exists(job.CoverUrl))
        {
            if (new FileInfo(job.CoverUrl).Length > 20 * 1024 * 1024) throw new IOException(L.T("앨범아트가 너무 커요 (최대 20MB)."));
            File.Copy(job.CoverUrl, Path.Combine(work, "artwork"), overwrite: true);
        }
        else
        {
            if (!Uri.TryCreate(job.CoverUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
                throw new InvalidOperationException(L.T("앨범아트가 없어요. 앨범아트를 지정한 뒤 다시 처리하세요."));
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = File.Create(Path.Combine(work, "artwork"));
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                total += read;
                if (total > 20 * 1024 * 1024) throw new IOException(L.T("앨범아트가 너무 커요 (최대 20MB)."));
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
        await RunFfmpegAsync(job.FfmpegPath, ["-i", Path.Combine(work, "artwork"), "-frames:v", "1", "-update", "1", cover], ct);
        var tracks = AlbumTrackNames.Metadata(job, sources);
        var entries = new List<string> { cover };
        var archiveNames = new Dictionary<string, string> { [cover] = "cover.jpg" };
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cover.jpg" };
        for (var index = 0; index < sources.Count; index++)
        {
            var source = sources[index];
            var track = tracks[index];
            ct.ThrowIfCancellationRequested();
            var stem = SafeName(Path.GetFileNameWithoutExtension(source));
            var name = stem + ".mp3";
            for (int suffix = 2; !names.Add(name); suffix++) name = $"{stem} ({suffix}).mp3";
            var target = Path.Combine(work, name);
            // MP3는 오디오를 재인코딩하지 않는다. 모든 파일에 같은 앨범아트를 내장한다.
            var codec = Path.GetExtension(source).Equals(".mp3", StringComparison.OrdinalIgnoreCase) ? "copy" : "libmp3lame";
            var args = new List<string> { "-i", source, "-i", cover, "-map", "0:a:0", "-map", "1:v:0", "-map_metadata", "0", "-c:a", codec };
            if (codec != "copy") args.AddRange(["-b:a", "320k"]);
            args.AddRange(["-c:v", "mjpeg", "-disposition:v:0", "attached_pic", "-id3v2_version", "3",
                "-metadata", "artist=" + job.Artist, "-metadata", "album_artist=" + job.Artist, "-metadata", "album=" + job.Album,
                "-metadata", "title=" + track.Title,
                "-metadata", "track=" + track.Number + (track.TrackCount > 0 ? "/" + track.TrackCount : ""),
                "-metadata", "disc=" + track.Disc + (track.DiscCount > 0 ? "/" + track.DiscCount : ""),
                "-metadata", "date=" + job.ReleaseDate.ToString("yyyy-MM-dd"), "-metadata:s:v", "title=Album cover", target]);
            await RunFfmpegAsync(job.FfmpegPath, args, ct);
            entries.Add(target);
            archiveNames[target] = track.FileName;
        }
        var temporary = Path.Combine(work, "album.zip.partial");
        await using (var file = File.Create(temporary))
        {
            using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                // 음원은 이미 압축되어 있으므로 ZIP에서는 재압축하지 않는다.
                var item = zip.CreateEntry(archiveNames[entry], CompressionLevel.NoCompression);
                await using var from = File.OpenRead(entry);
                await using var to = item.Open();
                await from.CopyToAsync(to, ct);
            }
        }
        ct.ThrowIfCancellationRequested();
        var final = Path.Combine(job.ArchiveDirectory, ArchiveName(job.Artist, job.Album, job.RecordType, job.ReleaseDate));
        // 이미 있는 앨범 ZIP은 덮어쓰지 않는다.
        var basePath = Path.Combine(job.ArchiveDirectory, Path.GetFileNameWithoutExtension(final));
        for (int suffix = 2; File.Exists(final); suffix++) final = $"{basePath} ({suffix}).zip";
        File.Move(temporary, final);
        // 변환 임시 파일을 정리한다. 원본은 AlbumDownloads가 완료 기록과 함께 정리한다.
        try
        {
            foreach (var entry in entries.Append(Path.Combine(work, "artwork"))) File.Delete(entry);
            if (!Directory.EnumerateFileSystemEntries(work).Any()) Directory.Delete(work);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return final;
    }

    public static async Task RunFfmpegAsync(string executable, IEnumerable<string> arguments, CancellationToken ct)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-y" }.Concat(arguments)) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException(L.T("ffmpeg를 실행하지 못했어요."));
        using var cancellation = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        var stderr = process.StandardError.ReadToEndAsync(ct);
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        var error = await stderr;
        await stdout;
        if (process.ExitCode != 0) throw new IOException(L.F("ffmpeg 처리 실패: {0}", error.Length > 1000 ? error[..1000] : error));
    }
}
