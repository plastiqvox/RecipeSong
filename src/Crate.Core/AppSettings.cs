using System.Text.Json;

namespace Crate.Core;

public sealed class AppSettings
{
    public string? ITunesLibraryPath { get; set; }
    public List<string> PlaylistFolders { get; set; } = [];
    /// <summary>iTunes Search API 국가 코드 (앨범 아트 · 미리듣기 지역)</summary>
    public string Country { get; set; } = "KR";
    /// <summary>소리 분석 서비스(ml/service.py) 폴더. 비우면 실행 파일 위쪽 폴더에서 찾는다.</summary>
    public string? MlDirectory { get; set; }
    /// <summary>캐시·기록 폴더. 비우면 %LOCALAPPDATA%\Crate</summary>
    public string? CacheDirectory { get; set; }
    /// <summary>uv.exe 경로. 비우면 %USERPROFILE%\.local\bin 과 PATH에서 찾는다.</summary>
    public string? UvPath { get; set; }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Crate", "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        var s = File.Exists(path)
            ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings()
            : new AppSettings();

        // 환경 변수가 있으면 우선 (MCP 서버 설정에서 쓰기 쉽게)
        if (Environment.GetEnvironmentVariable("Crate_ITUNES_XML") is { Length: > 0 } x) s.ITunesLibraryPath = x;
        if (Environment.GetEnvironmentVariable("Crate_PLAYLIST_DIRS") is { Length: > 0 } d)
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
