using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace RecipeSong.Core.Soulseek;

/// <summary>NS 157의 레지스트리 설정을 읽는다. 원본은 수정하지 않으며 자격 증명을 문자열로 덤프하지 않는다.</summary>
public static class SoulseekNsImport
{
    public const string RegistryPath = @"Software\Soulseek2\config";
    private const int MaxSectionBytes = 16 * 1024 * 1024;

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();

    public static SoulseekImportedSettings ReadInstalled()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(checked((int)GetACP()), EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
            using var key = user.OpenSubKey(RegistryPath, writable: false);
            if (key == null) continue;
            var sections = new Dictionary<string, byte[]>();
            foreach (var name in new[] { "login", "save", "shared" })
            {
                if (key.GetValue(name) is not byte[] bytes || bytes.Length > MaxSectionBytes)
                    throw new InvalidDataException(L.T("Soulseek NS 설정 항목을 읽지 못했어요."));
                sections[name] = bytes;
            }
            try { return Parse(sections, encoding); }
            finally { foreach (var bytes in sections.Values) Array.Clear(bytes); }
        }
        throw new FileNotFoundException(L.T("이 Windows 계정에서 Soulseek NS 설정을 찾지 못했어요."));
    }

    /// <summary>login.cfg와 같은 LE 길이 지정 문자열. 알려진 NS 구조만 읽고 길이를 검증한다.</summary>
    public static SoulseekImportedSettings Parse(IReadOnlyDictionary<string, byte[]> sections, Encoding encoding)
    {
        BinaryReader Open(string name)
        {
            if (!sections.TryGetValue(name, out var bytes) || bytes.Length > MaxSectionBytes) throw Invalid();
            return new BinaryReader(new MemoryStream(bytes, writable: false), encoding);
        }

        try
        {
            using var login = Open("login");
            login.ReadInt32(); login.ReadInt32();
            var username = ReadString(login, encoding);
            var password = ReadString(login, encoding);
            if (username.Length == 0 || username.Any(char.IsControl) || password.Any(char.IsControl)) throw Invalid();

            using var save = Open("save");
            save.ReadInt32();
            var downloadDirectory = ReadPath(save, encoding);

            using var shared = Open("shared");
            shared.ReadInt32(); shared.ReadInt32(); shared.ReadByte(); shared.ReadByte();
            var first = new List<string>();
            bool restricted = false;
            var count = ReadCount(shared);
            for (int i = 0; i < count; i++)
            {
                first.Add(ReadPath(shared, encoding));
                restricted |= shared.ReadInt32() != 0;
            }
            var second = new List<string>();
            count = ReadCount(shared);
            for (int i = 0; i < count; i++)
            {
                second.Add(ReadPath(shared, encoding));
                restricted |= shared.ReadByte() != 0;
            }
            if (shared.BaseStream.Position != shared.BaseStream.Length) throw Invalid();
            // 두 목록이 다르면 후보 전체를 보여 주되 자동 적용 대상으로 취급하지 않는다.
            restricted |= !first.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(second);
            return new(username, password, downloadDirectory,
                first.Concat(second).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), restricted);
        }
        catch (Exception ex) when (ex is EndOfStreamException or DecoderFallbackException or ArgumentException or OverflowException)
        { throw Invalid(); }
    }

    private static int ReadCount(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        if (count is < 0 or > 10000 || count > (reader.BaseStream.Length - reader.BaseStream.Position) / 5) throw Invalid();
        return count;
    }

    private static string ReadString(BinaryReader reader, Encoding encoding)
    {
        var length = reader.ReadInt32();
        if (length is < 0 or > 32768 || length > reader.BaseStream.Length - reader.BaseStream.Position) throw Invalid();
        var bytes = reader.ReadBytes(length);
        try { return encoding.GetString(bytes); }
        finally { Array.Clear(bytes); }
    }

    private static string ReadPath(BinaryReader reader, Encoding encoding)
    {
        var path = ReadString(reader, encoding);
        if (path.Length == 0 || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path)) throw Invalid();
        return path.Replace('/', Path.DirectorySeparatorChar);
    }

    private static InvalidDataException Invalid() => new(L.T("지원하지 않거나 손상된 Soulseek NS 설정 형식이에요."));
}

/// <summary>비밀번호는 메모리에서만 전달한다. JSON·로그·예외 메시지에는 포함하지 않는다.</summary>
public sealed class SoulseekImportedSettings(string username, string password, string downloadsDirectory, IReadOnlyList<string> sharedDirectories, bool requiresShareReview)
{
    public string Username { get; } = username;
    [JsonIgnore] public string Password { get; } = password;
    public string DownloadsDirectory { get; } = downloadsDirectory;
    public IReadOnlyList<string> SharedDirectories { get; } = sharedDirectories;
    public bool RequiresShareReview { get; } = requiresShareReview;
    public override string ToString() => "Soulseek NS settings (credentials masked)";
}
