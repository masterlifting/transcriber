namespace LocalTranscriber.Core.Operations;

/// <summary>Outcome of one transcription run for a call directory.</summary>
public sealed record TranscriptionOutcome(
    bool Success,
    int ExitCode,
    bool TranscriptJson,
    bool TranscriptMarkdown,
    string? ErrorComponent,
    string? ErrorMessage);

/// <summary>
/// Shared transcription orchestration used by the CLI, the Desktop, and the
/// recording orchestrator. Runs the Phase 3 Python pipeline as a child process
/// under an OS-backed per-call lock, updates state.json, and verifies outputs.
/// </summary>
public static class TranscriptionService
{
    public static async Task<TranscriptionOutcome> RunAsync(
        string callDir,
        Action<OperationProgress>? progress,
        Action<string>? output,
        CancellationToken abort)
    {
        if (!Directory.Exists(callDir))
        {
            throw new TranscriptionException($"call directory does not exist: {callDir}");
        }
        if (!File.Exists(Path.Combine(callDir, "me.wav")) || !File.Exists(Path.Combine(callDir, "remote.wav")))
        {
            throw new TranscriptionException(
                $"call directory is missing me.wav/remote.wav: {callDir}");
        }
        DependencyValidator.RequirePython();

        using var lockHandle = TranscriptionLock.TryAcquire(callDir);
        if (lockHandle is null)
        {
            return new TranscriptionOutcome(
                false, -1, false, false, "lock",
                "another transcription is already running for this call directory");
        }

        // A leftover "transcribing" state means a previous run was interrupted.
        string statePath = Path.Combine(callDir, "state.json");
        if (File.Exists(statePath))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(statePath));
                if (doc.RootElement.TryGetProperty("status", out var st) && st.GetString() == "transcribing")
                {
                    output?.Invoke("[note] previous transcription was interrupted; re-running.");
                }
            }
            catch (Exception)
            {
                // unreadable state.json: proceed anyway
            }
        }

        progress?.Invoke(new OperationProgress(OperationStage.Transcribing, "Transcribing..."));
        StateManager.Write(callDir, "transcribing");

        int exit;
        try
        {
            exit = await ProcessRunner.RunPythonPipelineAsync(callDir, output, output, abort);
        }
        catch (OperationCanceledException)
        {
            StateManager.Write(callDir, "failed",
                error: new Dictionary<string, object?> { ["component"] = "transcription", ["message"] = "transcription aborted", ["exitCode"] = -1 });
            return new TranscriptionOutcome(false, -1, false, false, "transcription", "aborted");
        }

        bool jsonExists = File.Exists(Path.Combine(callDir, "transcript.json"));
        bool mdExists = File.Exists(Path.Combine(callDir, "transcript.md"));

        if (exit == 0 && jsonExists && mdExists)
        {
            StateManager.Write(callDir, "completed",
                transcription: new Dictionary<string, object?>
                {
                    ["exitCode"] = exit,
                    ["transcriptJson"] = "transcript.json",
                    ["transcriptMarkdown"] = "transcript.md",
                    ["completedAtUtc"] = DateTime.UtcNow.ToString("O"),
                });
            progress?.Invoke(new OperationProgress(OperationStage.Completed, "Transcript ready"));
            return new TranscriptionOutcome(true, exit, true, true, null, null);
        }

        StateManager.Write(callDir, "failed",
            error: new Dictionary<string, object?> { ["component"] = "transcription", ["message"] = $"pipeline exited with code {exit}", ["exitCode"] = exit });
        progress?.Invoke(new OperationProgress(OperationStage.Failed, $"Transcription failed (pipeline exit {exit})"));
        return new TranscriptionOutcome(false, exit, jsonExists, mdExists, "transcription", $"pipeline exited with code {exit}");
    }
}
