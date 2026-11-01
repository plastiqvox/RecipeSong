using System.Text.Json;
using RecipeSong.Core;
using RecipeSong.Core.Soulseek;

static class InstalledNsImport
{
    public static Task RunAsync(bool apply)
    {
        var imported = SoulseekNsImport.ReadInstalled();
        if (!apply)
        {
            // 실제 설정값은 콘솔에 출력하지 않는다.
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                HasId = imported.Username.Length > 0,
                HasPassword = imported.Password.Length > 0,
                HasDownloadFolder = Path.IsPathFullyQualified(imported.DownloadsDirectory),
                SharedFolderCount = imported.SharedDirectories.Count,
                imported.RequiresShareReview,
            }));
            return Task.CompletedTask;
        }
        if (AppSettings.TestDataDir != null) throw new InvalidOperationException("Cannot import real credentials into a test data directory");
        if (imported.RequiresShareReview) throw new InvalidOperationException("Use the app preview to select shared folders");
        if (File.Exists(AppSettings.DefaultPath))
        {
            var app = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppSettings.DefaultPath));
            if (app?.CacheDirectory is { Length: > 0 } cache) CacheLocation.Directory = cache;
        }
        var settings = SoulseekSettings.Load();
        var target = Path.Combine(CacheLocation.Directory, "soulseek.json");
        if (File.Exists(target)) File.Copy(target, target + ".before-ns-import-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        settings.Username = imported.Username;
        settings.RememberPassword = true;
        settings.AutoConnect = false;
        settings.SetPassword(imported.Password);
        settings.DownloadsDirectory = imported.DownloadsDirectory;
        settings.SharedDirectories = settings.SharedDirectories.Concat(imported.SharedDirectories).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        settings.Save();
        var verify = SoulseekSettings.Load();
        if (verify.Username != imported.Username || verify.GetPassword() != imported.Password
            || verify.DownloadsDirectory != imported.DownloadsDirectory || imported.SharedDirectories.Any(p => !verify.SharedDirectories.Contains(p)))
            throw new InvalidDataException("Imported settings did not roundtrip");
        Console.WriteLine($"ID·비밀번호·다운로드 폴더·공유 폴더 {imported.SharedDirectories.Count}개 저장·복원 확인. 비밀번호는 DPAPI 암호화. 로그인·공유 스캔은 실행하지 않음.");
        return Task.CompletedTask;
    }
}
