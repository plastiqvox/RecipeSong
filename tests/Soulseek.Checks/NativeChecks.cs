using System.Buffers.Binary;
using System.Text;
using RecipeSong.Core;
using RecipeSong.Core.Soulseek;
using Slsk = global::Soulseek;

static class NativeChecks
{
    static void Check(bool value, string name) { if (!value) throw new Exception(name); }

    public static async Task RunAsync(string root)
    {
        SoulseekNsImportChecks.Run();
        var settings = new SoulseekSettings { Username = "test-account", RememberPassword = true, AutoConnect = true };
        settings.SetPassword("not-a-real-password");
        settings.Save();
        var saved = SoulseekSettings.Load();
        Check(saved.GetPassword() == "not-a-real-password", "DPAPI roundtrip");
        Check(!File.ReadAllText(Path.Combine(CacheLocation.Directory, "soulseek.json")).Contains("not-a-real-password"), "Password not stored as plaintext");
        settings.RememberPassword = false; settings.AutoConnect = false; settings.SetPassword("not-a-real-password"); settings.Save();
        Check(SoulseekSettings.Load().EncryptedPassword == null, "Password persistence disabled");
        settings.DownloadSlots = 0;
        try { settings.Validate(); throw new Exception("Invalid slots accepted"); } catch (ArgumentException) { }

        var slots = new TransferSlots();
        slots.Configure(2, 1);
        await slots.AcquireAsync("a", default);
        using var cancelA = new CancellationTokenSource();
        var waitingA = slots.AcquireAsync("a", cancelA.Token);
        await slots.AcquireAsync("b", default).WaitAsync(TimeSpan.FromSeconds(2));
        Check(slots.Active == 2 && !waitingA.IsCompleted, "Per-user limit and other-user fairness");
        cancelA.Cancel();
        try { await waitingA; throw new Exception("Cancelled waiter granted slot"); } catch (OperationCanceledException) { }
        slots.Configure(1, 1);
        using var cancelC = new CancellationTokenSource();
        var waitingC = slots.AcquireAsync("c", cancelC.Token);
        slots.Release("b");
        await Task.Delay(150);
        Check(!waitingC.IsCompleted, "Reduced limit preserves active transfer");
        slots.Release("a");
        await waitingC.WaitAsync(TimeSpan.FromSeconds(2));
        slots.Release("c");
        Check(slots.Active == 0 && slots.Waiting == 0, "No slot leak");

        var shared = Path.Combine(root, "shared");
        Directory.CreateDirectory(shared);
        await File.WriteAllTextAsync(Path.Combine(shared, "song.txt"), "shared content");
        await File.WriteAllTextAsync(Path.Combine(shared, "excluded-song.txt"), "excluded content");
        await File.WriteAllTextAsync(Path.Combine(shared, "incomplete.partial"), "partial content");
        var index = new ShareIndex();
        await index.ScanAsync([shared], default);
        Check(index.Files.Count == 2, "Incomplete files excluded");
        Check(index.Browse().Directories.Single().Files.Count == 2, "Browse lists only shared files");
        Check(index.Search(Slsk.SearchQuery.FromText("song -excluded"), []).Count() == 1, "Search exclusions");
        Check(!index.Search(Slsk.SearchQuery.FromText("song"), ["song"]).Any(), "Server excluded phrases");
        var file = index.Files.First();
        using (var input = index.OpenRead(file.Remote, 0)) Check(input.Length == file.Size, "Shared stream");
        try { index.OpenRead(@"..\outside.txt", 0); throw new Exception("Traversal accepted"); } catch (FileNotFoundException) { }
        Check(!ShareIndex.IsSafe(shared, Path.Combine(root, "outside.txt")), "Share root containment");
        index.RestrictTo([]);
        try { index.OpenRead(file.Remote, 0); throw new Exception("Removed share readable"); } catch (FileNotFoundException) { }

        var fixture = Path.Combine(root, "soulseek-client.dat.fixture");
        var text = Encoding.BigEndianUnicode.GetBytes(shared);
        var data = new byte[4 + text.Length];
        BinaryPrimitives.WriteUInt32BigEndian(data, (uint)text.Length);
        text.CopyTo(data, 4);
        await File.WriteAllBytesAsync(fixture, data);
        Check(SoulseekQtImport.ReadFolderCandidates(fixture).Single() == shared, "Qt length-prefixed folder candidate");

        await using var downloads = new AlbumDownloads();
        Check((await downloads.SnapshotAsync()).Count == 0, "Isolated native job store");
        try { await downloads.QueueAsync(new(), default); throw new Exception("Offline queue accepted"); } catch (InvalidOperationException) { }
        await DownloadHistoryChecks.RunAsync(root);
        Console.WriteLine("Credentials, dynamic slots, share access, query filtering and Qt candidate tests passed");
    }
}
