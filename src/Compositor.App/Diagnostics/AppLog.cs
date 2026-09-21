using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Compositor.App.Diagnostics;

/// <summary>A local, per-run diagnostic log for alpha testing and crash investigation.</summary>
internal static class AppLog
{
    private static readonly object Gate = new();
    private static StreamWriter? _writer;

    public static string? CurrentPath { get; private set; }

    public static void Initialize(string? directory = null)
    {
        lock (Gate)
        {
            if (_writer is not null)
            {
                return;
            }

            directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Compositor", "Logs");
            Directory.CreateDirectory(directory);
            CurrentPath = Path.Combine(directory, $"compositor-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
            _writer = new StreamWriter(new FileStream(CurrentPath, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
            Trace.Listeners.Add(new AppTraceListener());
            Info("Application", $"Starting Compositor; version={typeof(AppLog).Assembly.GetName().Version}; os={RuntimeInformation.OSDescription}; arch={RuntimeInformation.ProcessArchitecture}; runtime={RuntimeInformation.FrameworkDescription}");
            Info("Application", $"Log file: {CurrentPath}");
        }
    }

    public static void Info(string area, string message) => Write("INFO", area, message, null);

    public static void Error(string area, string message, Exception? exception = null) => Write("ERROR", area, message, exception);

    public static void Shutdown()
    {
        lock (Gate)
        {
            if (_writer is null)
            {
                return;
            }

            WriteCore("INFO", "Application", "Stopping Compositor", null);
            _writer.Dispose();
            _writer = null;
        }
    }

    private static void Write(string level, string area, string message, Exception? exception)
    {
        lock (Gate)
        {
            WriteCore(level, area, message, exception);
        }
    }

    private static void WriteCore(string level, string area, string message, Exception? exception)
    {
        if (_writer is null)
        {
            return;
        }

        var clean = message.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
        _writer.WriteLine($"{DateTimeOffset.Now:O} [{level}] [{area}] {clean}");
        if (exception is not null)
        {
            _writer.WriteLine(exception);
        }
    }

    private sealed class AppTraceListener : TraceListener
    {
        public override void Write(string? message)
        {
            if (!string.IsNullOrEmpty(message))
            {
                Info("Trace", message);
            }
        }

        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrEmpty(message))
            {
                Info("Trace", message);
            }
        }
    }
}
