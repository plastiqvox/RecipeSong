namespace RecipeSong.Core.Soulseek;

/// <summary>앨범 작업이 만든 원본·미완료·변환 파일의 경로와 정리를 담당한다.</summary>
public static class AlbumJobFiles
{
    public static bool AllDownloadsPresent(AlbumDownload job)
    {
        try { return job.Files.Count > 0 && job.Files.Select((f, i) => (File: f, Path: DownloadPath(job, i)))
            .All(p => File.Exists(p.Path) && new FileInfo(p.Path).Length == p.File.Size); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    public static bool HasFiles(AlbumDownload job)
    {
        if (job.Files.Count == 0) return false;
        foreach (var directory in new[] { job.DownloadsDirectory, job.ArchiveDirectory })
        {
            if (!Path.IsPathFullyQualified(directory)) throw new IOException(L.T("작업 폴더를 확인할 수 없어 파일을 정리하지 않았어요."));
            if (!Directory.Exists(Path.GetPathRoot(directory))) throw new IOException(L.T("저장 드라이브에 접근할 수 없어 파일 정리를 보류했어요."));
        }
        var work = Path.Combine(job.ArchiveDirectory, ".recipesong-" + job.Id.ToString("N"));
        for (var i = 0; i < job.Files.Count; i++)
        {
            var source = DownloadPath(job, i);
            if (File.Exists(source) || File.Exists(source + ".partial")
                || File.Exists(Path.Combine(work, AlbumArchive.SafeName(Path.GetFileNameWithoutExtension(source)) + ".mp3"))) return true;
        }
        return new[] { "cover.jpg", "artwork", "album.zip.partial" }.Any(name => File.Exists(Path.Combine(work, name)));
    }

    public static string DownloadPath(AlbumDownload job, int index) => Path.Combine(job.DownloadsDirectory,
        job.Id.ToString("N"), $"{index + 1:00}-" + AlbumArchive.SafeName(job.Files[index].Name));

    public static void Cleanup(AlbumDownload job)
    {
        if (job.Id == Guid.Empty) throw new IOException(L.T("작업 폴더를 확인할 수 없어 파일을 정리하지 않았어요."));
        var originals = OwnedDirectory(job.DownloadsDirectory, job.Id.ToString("N"));
        var work = OwnedDirectory(job.ArchiveDirectory, ".recipesong-" + job.Id.ToString("N"));
        // 재귀 삭제하지 않는다. 선언된 곡과 이 작업이 생성한 파일만 지운다.
        var paths = new List<string>();
        for (var i = 0; i < job.Files.Count; i++)
        {
            var source = Path.Combine(originals, Path.GetFileName(DownloadPath(job, i)));
            paths.Add(source); paths.Add(source + ".partial");
            paths.Add(Path.Combine(work, AlbumArchive.SafeName(Path.GetFileNameWithoutExtension(source)) + ".mp3"));
        }
        paths.AddRange(new[] { "cover.jpg", "artwork", "album.zip.partial" }.Select(name => Path.Combine(work, name)));
        foreach (var path in paths) CheckNoLinks(path);
        foreach (var path in paths) if (File.Exists(path)) File.Delete(path);
        foreach (var folder in new[] { originals, work })
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
    }

    private static string OwnedDirectory(string parent, string name)
    {
        if (!Path.IsPathFullyQualified(parent)) throw new IOException(L.T("작업 폴더를 확인할 수 없어 파일을 정리하지 않았어요."));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        var owned = Path.GetFullPath(Path.Combine(root, name));
        if (!string.Equals(Path.GetDirectoryName(owned), root, StringComparison.OrdinalIgnoreCase))
            throw new IOException(L.T("작업 폴더를 확인할 수 없어 파일을 정리하지 않았어요."));
        CheckNoLinks(owned);
        return owned;
    }

    private static void CheckNoLinks(string path)
    {
        for (string? current = path; current != null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException(L.T("연결된 경로는 자동으로 삭제하지 않아요. 작업 폴더를 확인하세요."));
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
