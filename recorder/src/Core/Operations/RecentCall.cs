using System.Text.Json;

namespace LocalTranscriber.Core.Operations;

/// <summary>A discovered call directory shown in the Recent Calls list.</summary>
public sealed record RecentCall(
    string Directory,
    string ProcessName,
    DateTime? StartedAtUtc,
    double? DurationSeconds,
    string Status,          // Completed | Recorded | Failed | Interrupted | Transcribing | Recording
    bool HasTranscript,
    bool HasSession);

/// <summary>
/// Reads recent calls directly from the calls root (no database). Status is
/// derived from state.json/session.json/transcript.json plus the live
/// transcription lock, so an interrupted "transcribing" state is reported as
/// Interrupted (retryable).
/// </summary>
public static class RecentCallReader
{
    private static readonly string[] StatusCompleted = { "completed" };
    private static readonly string[] StatusFailed = { "failed" };
    private static readonly string[] StatusTranscribing = { "transcribing" };
    private static readonly string[] StatusRecording = { "recording" };
    private static readonly string[] StatusRecorded = { "recorded" };

    public static List<RecentCall> ReadRecent(string callsRoot, int max = 20)
    {
        var result = new List<RecentCall>();
        if (!Directory.Exists(callsRoot))
        {
            return result;
        }

        var dirs = Directory.GetDirectories(callsRoot)
            .Select(d => new DirectoryInfo(d))
            .Where(d => TryParseStamp(d.Name, out _, out _))
            .OrderByDescending(d => d.Name)
            .Take(max);

        foreach (var dir in dirs)
        {
            result.Add(ReadOne(dir.FullName));
        }
        return result;
    }

    public static RecentCall ReadOne(string callDir)
    {
        string process = "call";
        DateTime? startedAt = null;
        double? duration = null;
        string? stateStatus = null;
        string? sessionStatus = null;
        DateTime? stateUpdatedAt = null;

        if (TryParseStamp(Path.GetFileName(callDir), out var stamp, out var procName))
        {
            startedAt = stamp;
            process = procName;
        }

        string statePath = Path.Combine(callDir, "state.json");
        if (File.Exists(statePath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(statePath));
                if (doc.RootElement.TryGetProperty("status", out var st))
                {
                    stateStatus = st.GetString();
                }
                if (doc.RootElement.TryGetProperty("updatedAtUtc", out var upd) &&
                    DateTime.TryParse(upd.GetString(), out var updDate))
                {
                    stateUpdatedAt = updDate;
                }
            }
            catch (Exception)
            {
                // ignore corrupt state.json
            }
        }

        string sessionPath = Path.Combine(callDir, "session.json");
        if (File.Exists(sessionPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(sessionPath));
                if (doc.RootElement.TryGetProperty("status", out var st))
                {
                    sessionStatus = st.GetString();
                }
                if (doc.RootElement.TryGetProperty("startedAtUtc", out var sa) &&
                    DateTime.TryParse(sa.GetString(), out var saDate))
                {
                    startedAt = saDate.ToLocalTime();
                }
                if (doc.RootElement.TryGetProperty("microphone", out var mic) &&
                    mic.TryGetProperty("durationSeconds", out var dur))
                {
                    duration = dur.GetDouble();
                }
                if (doc.RootElement.TryGetProperty("remote", out var rem))
                {
                    if (rem.TryGetProperty("process", out var pr) && pr.GetString() is { Length: > 0 } pn)
                    {
                        process = pn;
                    }
                    if (duration is null && rem.TryGetProperty("durationSeconds", out var rdur))
                    {
                        duration = rdur.GetDouble();
                    }
                }
            }
            catch (Exception)
            {
                // ignore corrupt session.json
            }
        }

        bool transcript = File.Exists(Path.Combine(callDir, "transcript.json")) &&
                          File.Exists(Path.Combine(callDir, "transcript.md"));
        bool hasSession = File.Exists(sessionPath);
        string status = DeriveStatus(stateStatus, sessionStatus, transcript, callDir, stateUpdatedAt);

        return new RecentCall(callDir, process, startedAt, duration, status, transcript, hasSession);
    }

    private static string DeriveStatus(
        string? stateStatus, string? sessionStatus, bool transcript, string callDir, DateTime? stateUpdatedAt)
    {
        bool stateIsStale = stateUpdatedAt is DateTime upd && (DateTime.UtcNow - upd).TotalSeconds > 90;
        if (stateStatus is not null)
        {
            if (StatusCompleted.Contains(stateStatus) || (transcript && stateStatus == "recorded"))
            {
                return "Completed";
            }
            if (StatusFailed.Contains(stateStatus))
            {
                return "Failed";
            }
            if (StatusRecording.Contains(stateStatus))
            {
                // stale "recording" (no live recorder) => interrupted
                return stateIsStale ? "Interrupted" : "Recording";
            }
            if (StatusTranscribing.Contains(stateStatus))
            {
                // stale "transcribing" without a live lock => interrupted, retryable
                return TranscriptionLock.IsHeld(callDir) ? "Transcribing" : "Interrupted";
            }
            if (StatusRecorded.Contains(stateStatus))
            {
                return transcript ? "Completed" : "Recorded";
            }
        }

        if (transcript)
        {
            return "Completed";
        }
        if (sessionStatus == "failed")
        {
            return "Failed";
        }
        return "Recorded";
    }

    private static bool TryParseStamp(string name, out DateTime localStamp, out string process)
    {
        localStamp = default;
        process = "call";
        var parts = name.Split('_');
        if (parts.Length < 3)
        {
            return false;
        }
        // Directory naming from the orchestrator: "yyyy-MM-dd_HH-mm-ss_<process>"
        if (DateTime.TryParseExact(
                $"{parts[0]}_{parts[1]}",
                "yyyy-MM-dd_HH-mm-ss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out localStamp))
        {
            process = string.Join("_", parts.Skip(2));
            return true;
        }
        return false;
    }
}
