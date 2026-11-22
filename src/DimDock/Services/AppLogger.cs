using System.Diagnostics;
using System.IO;

namespace DimDock.Services;

/// <summary>
/// Lightweight, never-throwing diagnostic logger that emits to System.Diagnostics.Trace
/// and appends to a size-bounded log file in %LocalAppData%\DimDock\dimdock.log.
/// </summary>
internal static class AppLogger
{
    private const long MaxLogBytes = 256 * 1024;

    private static readonly object WriteLock = new();
    private static string _logPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DimDock",
        "dimdock.log");

    public static string LogPath => _logPath;

    internal static void ConfigurePathForTests(string path)
    {
        lock (WriteLock)
        {
            _logPath = path;
        }
    }

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        string timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        string line = ex is null
            ? $"[{timestamp}] [{level}] {message}"
            : $"[{timestamp}] [{level}] {message} :: {ex.GetType().Name}: {ex.Message}";

        try
        {
            if (level == "ERROR")
            {
                Trace.TraceError(line);
            }
            else if (level == "WARN")
            {
                Trace.TraceWarning(line);
            }
            else
            {
                Trace.TraceInformation(line);
            }
        }
        catch
        {
            // Ignore trace listener failures.
        }

        try
        {
            lock (WriteLock)
            {
                string? dir = Path.GetDirectoryName(_logPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                if (File.Exists(_logPath))
                {
                    var info = new FileInfo(_logPath);
                    if (info.Length >= MaxLogBytes)
                    {
                        string backupPath = _logPath + ".old";
                        File.Move(_logPath, backupPath, overwrite: true);
                    }
                }

                File.AppendAllText(_logPath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never crash or disrupt the tray utility.
        }
    }
}
