using System;
using System.IO;
using System.Text;

namespace TaskbarLyrics.Core.Utilities;

public static class Log
{
    private const long MaxLogFileSizeBytes = 2 * 1024 * 1024;
    private static bool _isVerboseEnabled;

    public enum Level
    {
        Debug,
        Info,
        Warn,
        Error
    }

    public static void Write(Level level, string message)
    {
        if (!_isVerboseEnabled && level < Level.Warn)
        {
            return;
        }

        try
        {
            var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_debug.log");
            LogFileWriter.AppendLine(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level.ToString().ToUpper()}] {message}", MaxLogFileSizeBytes);
        }
        catch
        {
            // 忽略写日志时的异常，防止影响主流程
        }
    }

    public static void Debug(string message) => Write(Level.Debug, message);
    public static void Info(string message) => Write(Level.Info, message);
    public static void Warn(string message) => Write(Level.Warn, message);
    public static void Error(string message) => Write(Level.Error, message);

    public static void SetVerboseEnabled(bool enabled)
    {
        _isVerboseEnabled = enabled;
    }

}

public static class LogFileWriter
{
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);
    private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };
    private static readonly object FileLock = new();

    public static void AppendLine(string logPath, string message, long maxFileSizeBytes = 2 * 1024 * 1024)
    {
        lock (FileLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath) ?? AppDomain.CurrentDomain.BaseDirectory);
            RotateLegacyEncodingIfNeeded(logPath);
            TruncateIfNeeded(logPath, maxFileSizeBytes);
            using var writer = new StreamWriter(logPath, append: true, Utf8WithBom);
            writer.WriteLine(message);
        }
    }

    private static void RotateLegacyEncodingIfNeeded(string logPath)
    {
        if (!File.Exists(logPath))
        {
            return;
        }

        var info = new FileInfo(logPath);
        if (info.Length == 0)
        {
            return;
        }

        byte[] prefix;
        using (var stream = File.OpenRead(logPath))
        {
            prefix = new byte[Math.Min(Utf8Bom.Length, stream.Length)];
            _ = stream.Read(prefix, 0, prefix.Length);
        }

        if (prefix.Length >= Utf8Bom.Length &&
            prefix[0] == Utf8Bom[0] &&
            prefix[1] == Utf8Bom[1] &&
            prefix[2] == Utf8Bom[2])
        {
            return;
        }

        var legacyPath = Path.Combine(
            Path.GetDirectoryName(logPath) ?? AppDomain.CurrentDomain.BaseDirectory,
            $"{Path.GetFileNameWithoutExtension(logPath)}.legacy-{DateTime.Now:yyyyMMddHHmmss}{Path.GetExtension(logPath)}");
        try
        {
            File.Move(logPath, legacyPath);
        }
        catch (IOException)
        {
            // Another running instance may still hold the old log. Keep logging best-effort.
        }
    }

    private static void TruncateIfNeeded(string logPath, long maxFileSizeBytes)
    {
        if (File.Exists(logPath) && new FileInfo(logPath).Length >= maxFileSizeBytes)
        {
            File.WriteAllText(logPath, string.Empty, Utf8WithBom);
        }
    }
}
