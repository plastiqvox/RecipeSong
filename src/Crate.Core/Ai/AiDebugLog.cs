namespace Crate.Core.Ai;

/// <summary>
/// AI 커넥터 진단 기록: %LOCALAPPDATA%\Crate\ai-debug.log.
/// AI가 무엇을 답했고 어떤 제안이 Deezer 확인에서 왜 빠졌는지 남긴다 (1MB 넘으면 새로 시작).
/// </summary>
public static class AiDebugLog
{
    private static readonly string File_ = Path.Combine(JsonCache<object>.Dir, "ai-debug.log");
    private static readonly Lock Gate = new();

    public static void Write(string who, string message)
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(File_) && new FileInfo(File_).Length > 1_000_000) File.Delete(File_);
                File.AppendAllText(File_, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{who}] {message}{Environment.NewLine}");
            }
            catch (IOException) { }  // 진단 기록 실패는 무시
        }
    }
}
