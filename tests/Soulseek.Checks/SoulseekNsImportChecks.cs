using System.Text;
using System.Text.Json;
using RecipeSong.Core.Soulseek;

static class SoulseekNsImportChecks
{
    public static void Run()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(949, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        var sections = Fixture(encoding, @"D:\음악\받은 곡", [@"E:\음악\공유", @"F:\Albums"], "시험계정", "test-password-123");
        var imported = SoulseekNsImport.Parse(sections, encoding);
        Check(imported.Username == "시험계정" && imported.Password == "test-password-123", "NS credentials and ANSI encoding");
        Check(imported.DownloadsDirectory == @"D:\음악\받은 곡" && imported.SharedDirectories.Count == 2, "NS download and shared folders");
        Check(!imported.RequiresShareReview, "Matching public folder lists");
        Check(!imported.ToString().Contains(imported.Password) && !JsonSerializer.Serialize(imported).Contains(imported.Password), "Password not logged or serialized");
        var malformed = new Dictionary<string, byte[]>(sections) { ["login"] = sections["login"][..11] };
        Throws(() => SoulseekNsImport.Parse(malformed, encoding));
        malformed["login"] = (byte[])sections["login"].Clone();
        BitConverter.GetBytes(int.MaxValue).CopyTo(malformed["login"], 8);
        Throws(() => SoulseekNsImport.Parse(malformed, encoding));
        malformed = new(sections) { ["shared"] = sections["shared"][..^1] };
        Throws(() => SoulseekNsImport.Parse(malformed, encoding));
        malformed = Fixture(encoding, @"D:\Music", [@"E:\Private"], "user", "password", 1);
        Check(SoulseekNsImport.Parse(malformed, encoding).RequiresShareReview, "Restricted shares require explicit selection");
        Console.WriteLine("NS credentials/folders, encoding, malformed input and secret redaction checks passed");
    }

    public static Dictionary<string, byte[]> Fixture(Encoding encoding, string download, string[] shares, string username, string password, int flags = 0)
    {
        byte[] Build(Action<BinaryWriter> write)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, encoding, leaveOpen: true);
            write(writer);
            return stream.ToArray();
        }
        void Text(BinaryWriter writer, string value) { var data = encoding.GetBytes(value); writer.Write(data.Length); writer.Write(data); }
        return new()
        {
            ["login"] = Build(w => { w.Write(0); w.Write(0); Text(w, username); Text(w, password); w.Write(new byte[9]); }),
            ["save"] = Build(w => { w.Write(7472); Text(w, download); w.Write(new byte[16]); }),
            ["shared"] = Build(w =>
            {
                w.Write(0); w.Write(0); w.Write((byte)0); w.Write((byte)0); w.Write(shares.Length);
                foreach (var path in shares) { Text(w, path); w.Write(flags); }
                w.Write(shares.Length);
                foreach (var path in shares) { Text(w, path); w.Write((byte)flags); }
            }),
        };
    }
    static void Check(bool value, string name) { if (!value) throw new Exception(name); }
    static void Throws(Action action)
    {
        try { action(); throw new Exception("Malformed NS input accepted"); }
        catch (InvalidDataException ex) { Check(!ex.Message.Contains("test-password"), "Secret in exception"); }
    }
}
