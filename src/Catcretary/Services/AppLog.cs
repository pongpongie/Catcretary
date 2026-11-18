using System.IO;
using System.Text;

namespace Catcretary.Services;

/// <summary>
/// %APPDATA%\Catcretary\logs\yyyy-MM-dd.log 에 남기는 날짜별 파일 로그. 보관 기간이 지난 파일은 시작할 때 지운다.
/// 로그 쓰기 실패가 앱을 멈추면 안 되므로 여기서 나는 예외는 모두 삼킨다.
/// </summary>
public static class AppLog
{
    private const int RetentionDays = 7;
    private static readonly object Gate = new();

    public static string Directory => Path.Combine(AppConfig.Directory, "logs");

    /// <summary>보관 기간이 지난 로그를 지우고 시작 줄을 남긴다.</summary>
    public static void Init()
    {
        try
        {
            if (System.IO.Directory.Exists(Directory))
            {
                var cutoff = DateTime.Today.AddDays(-RetentionDays);
                foreach (var f in System.IO.Directory.GetFiles(Directory, "*.log"))
                    if (File.GetLastWriteTime(f) < cutoff) File.Delete(f);
            }
        }
        catch { }

        Info($"시작 v{typeof(AppLog).Assembly.GetName().Version}");
    }

    public static void Info(string message) => Write("INFO ", message);

    public static void Error(string context, Exception ex)
    {
        var sb = new StringBuilder(context).AppendLine().Append(ex);
        if (ex is PlaneApiException api)
        {
            sb.AppendLine().Append("  요청: ").Append(api.Method).Append(' ').Append(api.Path);
            if (api.Body.Length > 0) sb.AppendLine().Append("  응답 본문: ").Append(api.Body);
        }
        Write("ERROR", sb.ToString());
    }

    private static void Write(string level, string message)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}";
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.AppendAllText(Path.Combine(Directory, $"{DateTime.Today:yyyy-MM-dd}.log"), line, Encoding.UTF8);
            }
        }
        catch { }
    }
}
