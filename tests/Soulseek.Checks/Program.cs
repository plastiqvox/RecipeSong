using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using RecipeSong.Core;
using RecipeSong.Core.Soulseek;

static void Check(bool value, string message)
{
    if (!value) throw new Exception(message);
}

if (args.FirstOrDefault() is "--inspect-installed-ns" or "--import-installed-ns")
{
    await InstalledNsImport.RunAsync(args[0] == "--import-installed-ns");
    return;
}
L.Init("en");
var root = Path.Combine(Path.GetTempPath(), "RecipeSong-soulseek-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
CacheLocation.Directory = Path.Combine(root, "cache");
if (args.FirstOrDefault() == "--period-range")
{
    await PeriodRangeChecks.RunAsync();
    return;
}
if (args.FirstOrDefault() == "--download-cache")
{
    DownloadCacheChecks.Run(root);
    return;
}
if (args.FirstOrDefault() == "--taste-gate")
{
    TasteGateChecks.Run();
    return;
}
if (args.FirstOrDefault() == "--latest-action")
{
    await LatestAsyncActionChecks.RunAsync();
    return;
}
if (args.FirstOrDefault() == "--metadata")
{
    await MetadataAffinityChecks.RunAsync();
    return;
}
if (args.FirstOrDefault() == "--period-cache")
{
    await PeriodCacheChecks.RunAsync();
    return;
}
if (args.FirstOrDefault() == "--zip-archive")
{
    await ZipArchiveChecks.RunAsync(root);
    return;
}
if (args.FirstOrDefault() == "--followed-duplicates")
{
    await FollowedDuplicateChecks.RunAsync();
    return;
}
if (args.FirstOrDefault() == "--artist-discovery")
{
    await ArtistDiscoveryChecks.RunAsync();
    return;
}
if (args.FirstOrDefault() == "--track-names")
{
    await TrackNameChecks.RunAsync(root, args[1]);
    return;
}
if (args.FirstOrDefault() == "--per-track")
{
    await PerTrackChecks.RunAsync(root);
    return;
}
if (args.FirstOrDefault() == "--transfer-states")
{
    TransferStateChecks.Run();
    return;
}
if (args.FirstOrDefault() == "--album-actions")
{
    await AlbumActionChecks.RunAsync(root, args[1]);
    return;
}
if (args.FirstOrDefault() == "--album-scheduling")
{
    await AlbumSchedulingChecks.RunAsync(root);
    return;
}
await NativeChecks.RunAsync(root);
await AlbumSchedulingChecks.RunAsync(root);
Check(AlbumArchive.ArchiveName("Artist", "Album", "ep", new(2026, 11, 1)) == "[2026.11.01] (EP) Artist - Album.zip", "Archive name");
Check(AlbumArchive.ArchiveName("Cory Wong", "Lost In The Wonder", "album", new(2026, 2, 3)) == "[2026.02.03] (Album) Cory Wong - Lost In The Wonder.zip", "Requested archive format");
Check(AlbumArchive.ArchiveName("A/B", "C:D?", "album", new(2026, 11, 1)) == "[2026.11.01] (Album) A_B - C_D_.zip", "Invalid path characters");
Check(new SoulseekFile(@"Music\Album\01.flac", 1).IsAudio, "FLAC search");
Check(!new SoulseekFile(@"Music\Album\file.exe", 1).IsAudio, "Non-audio filtering");

var original = Path.Combine(root, "original.flac");
await File.WriteAllTextAsync(original, "original remains untouched");
try
{
    await AlbumArchive.CreateAsync(new() { ArchiveDirectory = Path.Combine(root, "missing-art") }, [original], default);
    throw new Exception("앨범아트 없이 ZIP 생성");
}
catch (InvalidOperationException) { }
Check(!Directory.GetFiles(Path.Combine(root, "missing-art"), "*.zip", SearchOption.AllDirectories).Any(), "실패 시 완성 ZIP 없음");
Check(await File.ReadAllTextAsync(original) == "original remains untouched", "실패 시 원본 보존");
Console.WriteLine("파일명·앨범아트 필수·원본 보존 검사 통과");

if (args.Length == 0)
{
    Console.WriteLine("실제 변환 검사 생략: ffmpeg.exe 경로를 인수로 지정하세요.");
    return;
}

var ffmpeg = args[0];
var audio = new List<string>();
foreach (var extension in new[] { "flac", "ogg", "mp3" })
{
    var path = Path.Combine(root, "tone." + extension);
    await AlbumArchive.RunFfmpegAsync(ffmpeg, ["-f", "lavfi", "-i", "sine=frequency=440:duration=1", path], default);
    audio.Add(path);
}
var cover = Path.Combine(root, "art.jpg");
await AlbumArchive.RunFfmpegAsync(ffmpeg, ["-f", "lavfi", "-i", "color=c=blue:s=128x128", "-frames:v", "1", "-update", "1", cover], default);
using var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
var port = ((IPEndPoint)listener.LocalEndpoint).Port;
var job = new AlbumDownload { Artist = "Test artist", Album = "Test album", RecordType = "album", ReleaseDate = new(2026, 11, 1),
    CoverUrl = $"http://127.0.0.1:{port}/cover", ArchiveDirectory = Path.Combine(root, "archives"), FfmpegPath = ffmpeg };
async Task ServeCoverAsync()
{
    using var connection = await listener.AcceptTcpClientAsync();
    await using var stream = connection.GetStream();
    using var reader = new StreamReader(stream, leaveOpen: true);
    while (await reader.ReadLineAsync() is { Length: > 0 }) { }
    var bytes = await File.ReadAllBytesAsync(cover);
    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: image/jpeg\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"));
    await stream.WriteAsync(bytes);
}
var serving = ServeCoverAsync();
var zipPath = await AlbumArchive.CreateAsync(job, audio, default);
await serving;
using (var zip = ZipFile.OpenRead(zipPath))
{
    Check(zip.Entries.Count == 4 && zip.GetEntry("cover.jpg") != null, "ZIP에 MP3 3곡·cover.jpg 포함");
    Directory.CreateDirectory(Path.Combine(root, "extracted"));
    foreach (var entry in zip.Entries.Where(e => e.Name.EndsWith(".mp3")))
    {
        var extracted = Path.Combine(root, "extracted", entry.Name);
        entry.ExtractToFile(extracted);
        using var tag = TagLib.File.Create(extracted);
        Check(tag.Tag.Pictures.Length == 1 && tag.Tag.Album == job.Album && tag.Tag.Performers.Contains(job.Artist), "MP3 앨범아트·태그");
        Check(tag.Properties.Duration.TotalSeconds > 0.8, "MP3 재생 길이");
    }
}
serving = ServeCoverAsync();
var secondPath = await AlbumArchive.CreateAsync(job, audio, default);
await serving;
Check(zipPath != secondPath && File.Exists(zipPath), "기존 ZIP 덮어쓰기 방지");
Check(audio.All(File.Exists), "압축 함수 입력은 작업 관리자 정리 전까지 보존");
Console.WriteLine("FLAC·OGG·MP3 변환·내장 앨범아트·ZIP 검사 통과: " + zipPath);
