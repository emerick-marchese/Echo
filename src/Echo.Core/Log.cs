namespace Echo.Core;

/// <summary>Tiny file logger (one file per day, kept 14 days).</summary>
public static class Log
{
    private static readonly object Lock = new();
    private static string? _directory;

    public static string? Directory => _directory;

    public static void Initialize(string directory)
    {
        _directory = directory;
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            foreach (var file in System.IO.Directory.GetFiles(directory, "echo-*.log"))
            {
                if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-14)) File.Delete(file);
            }
        }
        catch (IOException)
        {
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message} — {ex}");

    private static void Write(string level, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        System.Diagnostics.Debug.WriteLine(line);
        if (_directory is null) return;
        lock (Lock)
        {
            try
            {
                File.AppendAllText(Path.Combine(_directory, $"echo-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine);
            }
            catch (IOException)
            {
            }
        }
    }
}
