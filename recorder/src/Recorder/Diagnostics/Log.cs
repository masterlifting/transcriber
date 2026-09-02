namespace LocalTranscriber.Recorder.Diagnostics;

/// <summary>Minimal timestamped console logger. No external logging framework.</summary>
public static class Log
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INF", message);

    public static void Warn(string message) => Write("WRN", message);

    public static void Error(string message) => Write("ERR", message);

    private static void Write(string level, string message)
    {
        lock (Gate)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}");
        }
    }
}
