using System.Text.Json;

namespace SMSO.Net;

public static class AgentDebugLog
{
    private static readonly string[] LogPaths =
    {
        Path.Combine(AppContext.BaseDirectory, "debug-44c463.log"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SMSO",
            "debug-44c463.log"),
    };

    public static void Write(string hypothesisId, string location, string message, object? data = null)
    {
        var payload = JsonSerializer.Serialize(new
        {
            sessionId = "44c463",
            hypothesisId,
            location,
            message,
            data,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        }) + Environment.NewLine;

        foreach (var logPath in LogPaths)
        {
            try
            {
                var dir = Path.GetDirectoryName(logPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.AppendAllText(logPath, payload);
            }
            catch
            {
                // ignore debug log failures
            }
        }
    }
}
