using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RecipeSong.Core.Soulseek;

public sealed class SoulseekSettings
{
    public string Username { get; set; } = "";
    public string? EncryptedPassword { get; set; }
    public bool RememberPassword { get; set; }
    public bool AutoConnect { get; set; }
    public int ListenPort { get; set; } = 50001;
    public List<string> SharedDirectories { get; set; } = [];
    public int DownloadSlots { get; set; } = 3;
    public int UploadSlots { get; set; } = 2;
    public int DownloadsPerUser { get; set; } = 1;
    public int UploadsPerUser { get; set; } = 1;
    public int DownloadKiB { get; set; }
    public int UploadKiB { get; set; }
    public string DownloadsDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "RecipeSong", "originals");
    public string ArchiveDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "RecipeSong", "albums");
    public string FfmpegPath { get; set; } = "";
    public string ResolveFfmpeg() => !string.IsNullOrWhiteSpace(FfmpegPath) ? FfmpegPath
        : File.Exists(Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "ffmpeg.exe"))
            ? Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "ffmpeg.exe") : "ffmpeg";
    private static string FilePath => Path.Combine(JsonCache<object>.Dir, "soulseek.json");

    public string GetPassword()
    {
        if (EncryptedPassword == null) return "";
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(EncryptedPassword), null, DataProtectionScope.CurrentUser)); }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { return ""; }
    }

    public void SetPassword(string password) => EncryptedPassword = !RememberPassword || password.Length == 0 ? null
        : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser));

    public void Validate()
    {
        if (ListenPort is < 1024 or > 65535 || DownloadSlots is < 1 or > 100 || UploadSlots is < 1 or > 100
            || DownloadsPerUser is < 1 or > 100 || UploadsPerUser is < 1 or > 100 || DownloadKiB < 0 || UploadKiB < 0)
            throw new ArgumentException(L.T("슬롯은 1~100, 속도는 0 이상, 포트는 1024~65535로 입력하세요."));
        if (!Path.IsPathFullyQualified(DownloadsDirectory) || !Path.IsPathFullyQualified(ArchiveDirectory))
            throw new ArgumentException(L.T("저장 폴더는 전체 경로로 지정하세요."));
        if (AutoConnect && (!RememberPassword || string.IsNullOrEmpty(EncryptedPassword)))
            throw new ArgumentException(L.T("자동 로그인에는 비밀번호 저장이 필요해요."));
    }

    public static SoulseekSettings Load() => File.Exists(FilePath)
        ? JsonSerializer.Deserialize<SoulseekSettings>(File.ReadAllText(FilePath)) ?? new() : new();

    public void Save()
    {
        Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(FilePath + ".tmp", FilePath, overwrite: true);
    }
}
