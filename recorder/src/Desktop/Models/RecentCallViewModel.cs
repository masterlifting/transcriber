using System.Windows.Media;
using Transcriber.Core.Operations;

namespace Transcriber.Desktop.Models;

/// <summary>Row model for the Recent Calls list.</summary>
public sealed class RecentCallViewModel
{
    public RecentCallViewModel(RecentCall call)
    {
        Directory = call.Directory;
        ProcessName = call.ProcessName;
        Status = call.Status;
        TimeLabel = FormatTime(call.StartedAtUtc);
        DurationLabel = call.DurationSeconds is double d ? FormatDuration(d) : "—";
        HasTranscript = call.HasTranscript;
    }

    public string Directory { get; }
    public string ProcessName { get; }
    public string Status { get; }
    public string TimeLabel { get; }
    public string DurationLabel { get; }

    /// <summary>Copy Transcript is available only when transcript.md exists.</summary>
    public bool HasTranscript { get; }

    public bool RetryVisible => Status is "Recorded" or "Failed" or "Interrupted";

    public Brush StatusBrush => Status switch
    {
        "Completed" => new SolidColorBrush(Color.FromRgb(0x1E, 0x8E, 0x3E)),
        "Failed" => new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28)),
        "Interrupted" => new SolidColorBrush(Color.FromRgb(0xE6, 0x5C, 0x00)),
        "Transcribing" => new SolidColorBrush(Color.FromRgb(0x15, 0x65, 0xC0)),
        "Recording" => new SolidColorBrush(Color.FromRgb(0x15, 0x65, 0xC0)),
        _ => new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60)),
    };

    /// <summary>Translucent chip background derived from the status color.</summary>
    public Brush StatusBackground
    {
        get
        {
            var c = ((SolidColorBrush)StatusBrush).Color;
            return new SolidColorBrush(Color.FromArgb(0x22, c.R, c.G, c.B));
        }
    }

    private static string FormatTime(DateTime? utc)
    {
        if (utc is not DateTime t) return "—";
        DateTime local = t.ToLocalTime();
        DateTime today = DateTime.Today;
        if (local.Date == today)
        {
            return $"Today {local:HH:mm}";
        }
        if (local.Date == today.AddDays(-1))
        {
            return $"Yesterday {local:HH:mm}";
        }
        return $"{local:dd MMM HH:mm}";
    }

    private static string FormatDuration(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? t.ToString(@"hh\:mm\:ss") : t.ToString(@"mm\:ss");
    }
}
