using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace RecipeSong.Core.Soulseek;

/// <summary>형식이 공개되지 않은 Qt 데이터에서 길이가 명시된 경로 문자열만 추출한다. 설정값으로 추측해 적용하지 않는다.</summary>
public static class SoulseekQtImport
{
    public static string? FindConfiguration()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoulseekQt");
        return Directory.Exists(root) ? Directory.EnumerateFiles(root, "soulseek-client.dat*", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() : null;
    }

    public static IReadOnlyList<string> ReadFolderCandidates(string path)
    {
        if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new IOException(L.T("설정 파일이 너무 커요 (최대 64MB)."));
        var data = File.ReadAllBytes(path);
        if (data.Length >= 6 && data[4] == 0x78)
        {
            var expected = BinaryPrimitives.ReadUInt32BigEndian(data);
            if (expected > 64 * 1024 * 1024) throw new IOException(L.T("설정 파일이 너무 커요 (최대 64MB)."));
            using var input = new MemoryStream(data, 4, data.Length - 4);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = zlib.Read(buffer)) != 0)
            {
                if (output.Length + read > 64 * 1024 * 1024) throw new InvalidDataException("Qt configuration limit");
                output.Write(buffer, 0, read);
            }
            if (output.Length != expected) throw new InvalidDataException("Qt configuration length");
            data = output.ToArray();
        }
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i + 10 < data.Length; i++)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(i, 4));
            if (length is < 6 or > 8192 || length % 2 != 0 || i + 4L + length > data.Length) continue;
            var text = Encoding.BigEndianUnicode.GetString(data, i + 4, (int)length).Replace('/', '\\');
            // UNC 주소는 자동 조회하지 않는다. 설정 파일을 열었다는 이유로 네트워크 인증을 보내지 않게 한다.
            if (text.Length < 3 || !char.IsAsciiLetter(text[0]) || text[1] != ':' || text[2] != '\\' || text.Any(char.IsControl)) continue;
            try { if (Directory.Exists(text)) paths.Add(Path.GetFullPath(text)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { }
        }
        return paths.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
