using System.Diagnostics;
using RecipeSong.Core;
using RecipeSong.Core.Sound;

static class DownloadCacheChecks
{
    public static void Run(string root)
    {
        static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
        static bool Refuses(Action action) { try { action(); return false; } catch (IOException) { return true; } }
        var home = Path.Combine(root, "downloads-test");
        var source = new DownloadCache(Path.Combine(home, "source"));
        source.EnsureOwned();
        Directory.CreateDirectory(Path.Combine(source.DirectoryPath, "uv"));
        File.WriteAllText(Path.Combine(source.DirectoryPath, "uv", "package.bin"), "package");
        Directory.CreateDirectory(Path.Combine(source.DirectoryPath, "models"));
        File.WriteAllText(Path.Combine(source.DirectoryPath, "models", "weights.bin"), "weights");
        File.WriteAllText(Path.Combine(source.DirectoryPath, "keep.txt"), "unrelated");
        Assert(source.Sizes().Sum(p => p.Bytes) == 14, "Cache size accounting changed.");
        using (source.Lease())
        {
            Assert(Refuses(() => source.Clear()), "Active cache was cleared.");
            Assert(Refuses(() => source.CopyTo(Path.Combine(home, "busy-copy"))), "Active cache was moved.");
        }
        var target = Path.Combine(home, "target");
        source.CopyTo(target);
        Assert(File.ReadAllText(Path.Combine(target, "models", "weights.bin")) == "weights", "Model cache was not copied.");
        Assert(File.Exists(Path.Combine(source.DirectoryPath, "models", "weights.bin")), "Source was deleted before settings commit.");
        Assert(Refuses(() => source.CopyTo(target)), "Nonempty destination was overwritten.");
        Assert(Refuses(() => source.CopyTo(Path.Combine(source.DirectoryPath, "nested"))), "Nested cache move was accepted.");
        Assert(source.Clear() == 14 && File.Exists(Path.Combine(source.DirectoryPath, "keep.txt")), "Cleanup affected unrelated files.");
        var foreign = Path.Combine(home, "foreign");
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "crate_model.json"), "protected");
        Assert(Refuses(() => new DownloadCache(foreign).Clear()), "Unowned data directory was cleared.");
        var process = new ProcessStartInfo();
        new DownloadCache(target).Apply(process);
        Assert(process.Environment["UV_CACHE_DIR"] == Path.Combine(target, "uv")
            && process.Environment["HF_HUB_CACHE"] == Path.Combine(target, "models"), "Child cache environment was not isolated.");
        Assert(Refuses(() => new DownloadCache(Path.GetPathRoot(home)!).Clear()), "Drive root was accepted.");
        var repo = Path.Combine(home, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        Assert(DownloadCache.Location(new AppSettings(), Path.Combine(repo, "ml")) == Path.Combine(repo, "ml", ".download-cache"),
            "Development cache was not stable and separate.");
        var fakeMl = Path.Combine(home, "fake-ml");
        foreach (var relative in new[] { ".venv/pyvenv.cfg", ".venv/Lib/site-packages/torch/__init__.py", ".venv/Lib/site-packages/transformers/__init__.py" })
        {
            var file = Path.Combine(fakeMl, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "fixture");
        }
        var service = SoundService.TryCreate(new AppSettings { MlDirectory = fakeMl, UvPath = "unused.exe", ITunesLibraryPath = "unused.xml", DownloadCacheDirectory = foreign })!;
        try
        {
            Assert(Refuses(() => service.StartAsync().GetAwaiter().GetResult()), "Unowned service cache was accepted.");
            Assert(Refuses(() => service.ScoreAlbumsAsync([1]).WaitAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult()),
                "Failed startup left sound requests waiting indefinitely.");
        }
        finally { service.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        Console.WriteLine("Download cache: ownership, locks, transactional copy, cleanup, child environment and development location passed.");
    }
}
