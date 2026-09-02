using System.Text.Json;

namespace LocalTranscriber.Core;

/// <summary>
/// Minimal lifecycle state for a call directory (state.json). Written at
/// meaningful lifecycle boundaries only (recording -> recorded -> transcribing
/// -> completed | failed). Never touches session.json, which keeps the Phase 2
/// timing metadata that Phase 3 synchronization depends on.
/// </summary>
public static class StateManager
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static void Write(
        string callDir,
        string status,
        object? recording = null,
        object? transcription = null,
        object? error = null)
    {
        var state = new Dictionary<string, object?>
        {
            ["version"] = 1,
            ["status"] = status,
            ["updatedAtUtc"] = DateTime.UtcNow.ToString("O"),
        };
        if (recording is not null)
        {
            state["recording"] = recording;
        }
        if (transcription is not null)
        {
            state["transcription"] = transcription;
        }
        if (error is not null)
        {
            state["error"] = error;
        }

        string path = Path.Combine(callDir, "state.json");
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(state, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[warn] cannot write {path}: {ex.Message}");
        }
    }
}
