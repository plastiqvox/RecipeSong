using System.Text.Json;

namespace RecipeSong.Core;

public sealed class AppSettings
{
    public string? ITunesLibraryPath { get; set; }
    public List<string> PlaylistFolders { get; set; } = [];
    /// <summary>iTunes Search API 국가 코드 (앨범 아트 · 미리듣기 지역)</summary>
    public string Country { get; set; } = "KR";
    /// <summary>소리 분석 서비스(ml/service.py) 폴더. 비우면 실행 파일 위쪽 폴더에서 찾는다.</summary>
    public string? MlDirectory { get; set; }
    /// <summary>캐시·기록 폴더. 비우면 %LOCALAPPDATA%\RecipeSong</summary>
    public string? CacheDirectory { get; set; }
    /// <summary>uv.exe 경로. 비우면 %USERPROFILE%\.local\bin 과 PATH에서 찾는다.</summary>
    public string? UvPath { get; set; }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RecipeSong", "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load(string? path = null)
    {
        MigrateFromCrate();
        path ??= DefaultPath;
        var s = File.Exists(path)
            ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings()
            : new AppSettings();

        // 환경 변수가 있으면 우선 (MCP 서버 설정에서 쓰기 쉽게). 예전 이름(Crate_)도 받는다.
        if (Env("RECIPESONG_ITUNES_XML", "Crate_ITUNES_XML") is { } x) s.ITunesLibraryPath = x;
        if (Env("RECIPESONG_PLAYLIST_DIRS", "Crate_PLAYLIST_DIRS") is { } d)
            s.PlaylistFolders = [.. d.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

        s.ITunesLibraryPath ??= FindITunesLibrary();
        return s;
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    private static string? Env(params string[] names) =>
        names.Select(Environment.GetEnvironmentVariable).FirstOrDefault(v => !string.IsNullOrEmpty(v));

    /// <summary>
    /// 앱 이름이 Crate였을 때의 설정(%APPDATA%\Crate)과 캐시(%LOCALAPPDATA%\Crate)를 새 이름 폴더로 한 번 옮긴다.
    /// 새 폴더가 이미 있으면 건드리지 않는다.
    /// </summary>
    public static void MigrateFromCrate()
    {
        foreach (var root in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData })
        {
            var baseDir = Environment.GetFolderPath(root);
            var old = Path.Combine(baseDir, "Crate");
            var now = Path.Combine(baseDir, "RecipeSong");
            if (!Directory.Exists(old) || Directory.Exists(now)) continue;
            try { Directory.Move(old, now); }
            catch (IOException)
            {
                // 다른 프로그램이 잡고 있으면 복사 (예전 폴더는 남긴다)
                foreach (var f in Directory.EnumerateFiles(old, "*", SearchOption.AllDirectories))
                {
                    var dest = Path.Combine(now, Path.GetRelativePath(old, f));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(f, dest, overwrite: false);
                }
            }
        }
    }

    private static string? FindITunesLibrary()
    {
        var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        string[] candidates =
        [
            Path.Combine(music, "iTunes", "iTunes Music Library.xml"),
            Path.Combine(music, "iTunes", "iTunes Library.xml"),
            Path.Combine(music, "iTunes", "Library.xml"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }
}
