using System.Diagnostics;

namespace RecipeSong.Core;

/// <summary>앱이 만든 다운로드 캐시만 관리한다. 공용 uv/Hugging Face 캐시와 ml/data는 정리하지 않는다.</summary>
public sealed class DownloadCache(string directory)
{
    private const string Marker = ".recipesong-download-cache";
    private const string Owner = "RecipeSong downloadable cache v1";
    private static readonly string[] Parts = ["uv", "models", "hf", "temp"];
    public string DirectoryPath { get; } = Normalize(directory);

    private static string Normalize(string directory)
    {
        var full = Path.GetFullPath(directory);
        if (string.Equals(full, Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase))
            throw new IOException(L.T("드라이브 전체를 캐시로 사용할 수 없어요."));
        return full.TrimEnd(Path.DirectorySeparatorChar);
    }

    public static string Location(AppSettings settings, string? mlDirectory)
    {
        if (AppSettings.TestDataDir is { } test) return Path.Combine(test, "download-cache");
        if (settings.DownloadCacheDirectory is { Length: > 0 } configured) return Path.GetFullPath(configured);
        if (mlDirectory != null && (Directory.Exists(Path.Combine(mlDirectory, "..", ".git")) || File.Exists(Path.Combine(mlDirectory, "..", ".git"))))
            return Path.GetFullPath(Path.Combine(mlDirectory, ".download-cache"));
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RecipeSong-download-cache");
    }

    public void EnsureOwned()
    {
        RejectLinks(DirectoryPath);
        var marker = Path.Combine(DirectoryPath, Marker);
        if (File.Exists(marker))
        {
            if ((File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0)
                throw new IOException(L.T("RecipeSong 전용 캐시가 아니에요."));
            if (File.ReadAllText(marker) != Owner) throw new IOException(L.T("RecipeSong 전용 캐시가 아니에요."));
            return;
        }
        if (Directory.Exists(DirectoryPath) && Directory.EnumerateFileSystemEntries(DirectoryPath).Any())
            throw new IOException(L.T("캐시는 비어 있는 새 폴더를 선택해 주세요."));
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(marker, Owner);
    }

    public FileStream Lease(bool exclusive = false)
    {
        EnsureOwned();
        var path = Path.Combine(DirectoryPath, ".in-use");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException(L.T("연결된 파일은 캐시 잠금으로 사용할 수 없어요."));
        if (!File.Exists(path)) using (File.Create(path)) { }
        return new FileStream(path, FileMode.Open, exclusive ? FileAccess.ReadWrite : FileAccess.Read,
            exclusive ? FileShare.Delete : FileShare.Read);
    }

    public void Apply(ProcessStartInfo process)
    {
        process.Environment["UV_CACHE_DIR"] = Path.Combine(DirectoryPath, "uv");
        process.Environment["HF_HUB_CACHE"] = Path.Combine(DirectoryPath, "models");
        process.Environment["HF_HOME"] = Path.Combine(DirectoryPath, "hf");
        process.Environment.Remove("TRANSFORMERS_CACHE");
        // 예전 공용 모델이 이미 있으면 읽기 전용으로 재사용한다. 새 다운로드만 전용 경로에 둔다.
        process.Environment["RECIPESONG_LEGACY_MODEL_CACHE"] = AppSettings.TestDataDir == null
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "huggingface", "hub") : "";
    }

    public IReadOnlyList<(string Name, long Bytes)> Sizes() => Parts.Select(name =>
        (name, Bytes(Path.Combine(DirectoryPath, name)))).ToArray();

    public long Clear()
    {
        using var lease = Lease(exclusive: true);
        long bytes = 0;
        foreach (var name in Parts)
        {
            var path = Path.Combine(DirectoryPath, name);
            bytes += Bytes(path);
            DeleteTree(path);
        }
        return bytes;
    }

    public void ValidateDestination(string destination)
    {
        var target = new DownloadCache(destination);
        if (string.Equals(DirectoryPath, target.DirectoryPath, StringComparison.OrdinalIgnoreCase)) return;
        if (target.DirectoryPath.StartsWith(DirectoryPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || DirectoryPath.StartsWith(target.DirectoryPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException(L.T("캐시의 상위·하위 폴더로는 옮길 수 없어요."));
        RejectLinks(target.DirectoryPath);
        if (Directory.Exists(target.DirectoryPath) && Directory.EnumerateFileSystemEntries(target.DirectoryPath).Any())
            throw new IOException(L.T("캐시는 비어 있는 새 폴더를 선택해 주세요."));
    }

    public void CopyTo(string destination)
    {
        ValidateDestination(destination);
        var target = new DownloadCache(destination);
        if (string.Equals(DirectoryPath, target.DirectoryPath, StringComparison.OrdinalIgnoreCase)) return;
        using var lease = Lease(exclusive: true);
        var staging = target.DirectoryPath + ".moving-" + Guid.NewGuid().ToString("N");
        try
        {
            new DownloadCache(staging).EnsureOwned();
            File.WriteAllText(Path.Combine(staging, ".copied-from"), DirectoryPath);
            foreach (var name in Parts)
                if (Directory.Exists(Path.Combine(DirectoryPath, name)))
                {
                    Directory.CreateDirectory(Path.Combine(staging, name));
                    CopyTree(Path.Combine(DirectoryPath, name), Path.Combine(staging, name));
                }
            if (Directory.Exists(target.DirectoryPath)) Directory.Delete(target.DirectoryPath);
            Directory.Move(staging, target.DirectoryPath);
        }
        finally { DeleteTree(staging); }
    }

    public static void CompletePending(AppSettings settings, string? mlDirectory)
    {
        if (AppSettings.TestDataDir != null) return;
        if (!settings.ClearDownloadCacheOnNextStart && string.IsNullOrWhiteSpace(settings.PendingDownloadCacheDirectory)) return;
        var cache = new DownloadCache(Location(settings, mlDirectory));
        // 재시작은 이전 프로세스가 닫히기 직전에 새 프로세스를 띄우므로 잠금 해제를 잠깐 기다린다.
        for (int attempt = 0; ; attempt++)
        {
            try { using var idle = cache.Lease(exclusive: true); break; }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 && attempt < 20) { Thread.Sleep(250); }
        }
        if (settings.ClearDownloadCacheOnNextStart)
        {
            cache.Clear();
            settings.ClearDownloadCacheOnNextStart = false;
            settings.Save();
        }
        if (settings.PendingDownloadCacheDirectory is not { Length: > 0 } next) return;
        if (string.Equals(cache.DirectoryPath, Path.GetFullPath(next).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        { settings.PendingDownloadCacheDirectory = null; settings.Save(); return; }
        var previousCopy = Path.Combine(next, ".copied-from");
        if (!File.Exists(previousCopy) || File.ReadAllText(previousCopy) != cache.DirectoryPath) cache.CopyTo(next);
        else new DownloadCache(next).EnsureOwned();
        settings.DownloadCacheDirectory = Path.GetFullPath(next);
        settings.PendingDownloadCacheDirectory = null;
        settings.Save();
        cache.Clear(); // 설정 저장이 실패하면 원본도 그대로 남긴다.
        File.Delete(previousCopy);
    }

    public static void CheckInstallSpace(string mlDirectory, string cacheDirectory)
    {
        if (File.Exists(Path.Combine(mlDirectory, ".venv", "pyvenv.cfg"))
            && File.Exists(Path.Combine(mlDirectory, ".venv", "Lib", "site-packages", "torch", "__init__.py"))
            && File.Exists(Path.Combine(mlDirectory, ".venv", "Lib", "site-packages", "transformers", "__init__.py"))) return;
        var needed = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, bytes) in new[] { (mlDirectory, 6L << 30), (cacheDirectory, 8L << 30) })
        {
            var drive = Path.GetPathRoot(Path.GetFullPath(path))!;
            needed[drive] = needed.GetValueOrDefault(drive) + bytes;
        }
        foreach (var (drive, bytes) in needed)
            if (new DriveInfo(drive).AvailableFreeSpace < bytes)
                throw new IOException(L.F("소리 분석 환경을 설치하려면 {0}에 약 {1}의 여유 공간이 필요해요.", drive, CacheLocation.Format(bytes)));
    }

    private static void RejectLinks(string path)
    {
        for (var dir = new DirectoryInfo(path); dir != null; dir = dir.Parent)
            if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException(L.T("연결된 폴더는 캐시 경로로 사용할 수 없어요."));
    }

    private static long Bytes(string path)
    {
        if (!Directory.Exists(path)) return 0;
        var dir = new DirectoryInfo(path);
        if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) return 0;
        return dir.EnumerateFiles().Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0).Sum(f => f.Length)
            + dir.EnumerateDirectories().Sum(d => Bytes(d.FullName));
    }

    private static void DeleteTree(string path)
    {
        if (!Directory.Exists(path)) return;
        var dir = new DirectoryInfo(path);
        if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) { dir.Delete(); return; }
        foreach (var file in dir.EnumerateFiles())
        {
            if ((file.Attributes & FileAttributes.ReparsePoint) == 0) file.Attributes &= ~FileAttributes.ReadOnly;
            file.Delete();
        }
        foreach (var child in dir.EnumerateDirectories()) DeleteTree(child.FullName);
        dir.Delete();
    }

    private static void CopyTree(string from, string to)
    {
        foreach (var entry in new DirectoryInfo(from).EnumerateFileSystemInfos())
        {
            if (entry.Name == ".in-use") continue;
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException(L.T("연결된 파일이 있는 캐시는 옮길 수 없어요."));
            var dest = Path.Combine(to, entry.Name);
            if (entry is DirectoryInfo child) { Directory.CreateDirectory(dest); CopyTree(child.FullName, dest); }
            else File.Copy(entry.FullName, dest, overwrite: false);
        }
    }
}
