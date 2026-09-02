using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Transcriber.Core.Audio;
using Transcriber.Core.Processes;

namespace Transcriber.Core.Operations;

public sealed record RecordingRequest(
    string? ProcessName,
    int? Pid,
    string Microphone,
    TimeSpan? Duration,
    bool AutoTranscribe,
    string? CallsRoot = null,
    double? TestFailAfterSeconds = null);

public sealed record RecordingArtifacts(
    string CallDirectory,
    int RecorderExitCode,
    bool MeExists,
    bool RemoteExists,
    bool SessionExists,
    string? SessionStatus,
    string? SessionErrorComponent,
    string? SessionErrorMessage);

public sealed record RecordingResult(
    bool Success,
    RecordingArtifacts Recording,
    TranscriptionOutcome? Transcription,
    string CallDirectory);

/// <summary>
/// Reusable recording orchestration shared by CLI and Desktop: validates
/// dependencies and inputs, creates the call directory, runs recorder.exe
/// (hidden child, streamed output), stops it gracefully on request via a
/// stop-marker file (the same finalization path as Ctrl+C/duration), then
/// optionally runs the Phase 3 transcription under a per-call lock.
///
/// The stop marker is how the Desktop's Stop button reaches the recorder
/// child, which has no console and therefore no Ctrl+C delivery.
/// </summary>
public sealed class RecordingOrchestrator
{
    public const string StopMarkerFileName = ".stop";
    private static readonly TimeSpan GracefulStopTimeout = TimeSpan.FromSeconds(30);

    public event Action<OperationProgress>? Progress;
    public event Action<string>? ChildOutput;

    /// <summary>Set once the auto-named call directory has been created.</summary>
    public string? CallDirectory { get; private set; }

    /// <summary>Set by the Desktop for "Stop and Exit": skip auto-transcription.</summary>
    public bool SkipTranscription { get; set; }

    public async Task<RecordingResult> RunAsync(
        RecordingRequest request,
        CancellationToken stopRequested,
        CancellationToken abort)
    {
        // 1. dependencies
        Report(OperationStage.Preparing, "Validating dependencies");
        DependencyValidator.RequireAll();

        // 2. microphone pre-validation (direct enumeration via Core, no child parsing)
        Report(OperationStage.Preparing, "Validating microphone");
        ValidateMicrophone(request.Microphone);

        // 3. process pre-validation (direct process listing via Core)
        Report(OperationStage.Preparing, "Validating target process");
        ValidateProcess(request.ProcessName, request.Pid);

        // 4. call directory
        string callsRoot = request.CallsRoot ?? Paths.CallsRoot;
        string callDir = CreateCallDirectory(request.ProcessName ?? $"pid-{request.Pid}", callsRoot);
        CallDirectory = callDir;
        StateManager.Write(callDir, "recording", recording: new Dictionary<string, object?>
        {
            ["requestedProcess"] = request.ProcessName,
            ["requestedPid"] = request.Pid,
            ["microphone"] = request.Microphone,
            ["callDirectory"] = callDir,
        });

        // 5. run recorder child
        var psi = BuildRecorderArgs(request, callDir);
        Report(OperationStage.Recording, $"Recording to {callDir}");
        int recorderExit = await RunRecorderWithStopAsync(psi, callDir, stopRequested, abort);

        // 6. artifact inspection
        var artifacts = InspectArtifacts(callDir, recorderExit);
        var recSummary = new Dictionary<string, object?>
        {
            ["exitCode"] = recorderExit,
            ["meWav"] = artifacts.MeExists,
            ["remoteWav"] = artifacts.RemoteExists,
            ["sessionJson"] = artifacts.SessionExists,
            ["stoppedAtUtc"] = DateTime.UtcNow.ToString("O"),
        };

        if (recorderExit != 0 || !(artifacts.MeExists && artifacts.RemoteExists && artifacts.SessionExists))
        {
            StateManager.Write(callDir, "failed", recording: recSummary,
                error: new Dictionary<string, object?>
                {
                    ["component"] = "recording",
                    ["message"] = $"recorder exited with code {recorderExit}",
                    ["exitCode"] = recorderExit,
                });
            Report(OperationStage.Failed, "Recording failed");
            return new RecordingResult(false, artifacts, null, callDir);
        }

        StateManager.Write(callDir, "recorded", recording: recSummary);
        Report(OperationStage.Stopping, "Recording saved");

        if (!request.AutoTranscribe || SkipTranscription)
        {
            Report(OperationStage.Completed, "Recording complete");
            return new RecordingResult(true, artifacts, null, callDir);
        }

        // 7. automatic transcription (shared service, per-call lock)
        var outcome = await TranscriptionService.RunAsync(callDir, p => Report(p.Stage, p.Message), l => ChildOutput?.Invoke(l), abort);
        return new RecordingResult(outcome.Success, artifacts, outcome, callDir);
    }

    // ---------------- internals ----------------

    private void Report(OperationStage stage, string message)
    {
        Progress?.Invoke(new OperationProgress(stage, message));
    }

    private static void ValidateMicrophone(string mic)
    {
        var endpoints = AudioEndpointEnumerator.ListCaptureEndpoints();
        if (mic.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            if (!endpoints.Any(e => e.IsDefault))
            {
                throw new RecordingException(
                    "no default input device is available; specify an explicit microphone");
            }
            return;
        }
        if (!endpoints.Any(e =>
                e.FriendlyName.Contains(mic, StringComparison.OrdinalIgnoreCase) ||
                e.Id.Equals(mic, StringComparison.OrdinalIgnoreCase)))
        {
            throw new RecordingException(
                $"microphone not found: '{mic}'. Available devices:\n" +
                string.Join("\n", endpoints.Select(e => $"  {e.FriendlyName}")));
        }
    }

    private static void ValidateProcess(string? name, int? pid)
    {
        if (pid is int p)
        {
            if (ProcessEnumerator.Find(null).All(x => x.Pid != p))
            {
                throw new RecordingException($"process not found: PID {p}");
            }
            return;
        }
        if (string.IsNullOrWhiteSpace(name) || ProcessEnumerator.Find(name).Count == 0)
        {
            throw new RecordingException($"process not found: '{name}'");
        }
    }

    private static string CreateCallDirectory(string? processName, string callsRoot)
    {
        string safe = SanitizeName(processName ?? "call");
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        string baseName = $"{stamp}_{safe}";
        string dir = Path.Combine(callsRoot, baseName);
        int suffix = 2;
        while (Directory.Exists(dir))
        {
            dir = Path.Combine(callsRoot, $"{baseName}-{suffix++}");
        }
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new RecordingException($"cannot create call directory '{dir}': {ex.Message}");
        }
        return dir;
    }

    private static string SanitizeName(string name)
    {
        var chars = name
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : (c is '_' or '-') ? c : '_')
            .ToArray();
        string result = new string(chars);
        while (result.Contains("__", StringComparison.Ordinal))
        {
            result = result.Replace("__", "_", StringComparison.Ordinal);
        }
        return result.Trim('_');
    }

    private static ProcessStartInfo BuildRecorderArgs(RecordingRequest request, string callDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Paths.RecorderExe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        psi.ArgumentList.Add("record");
        psi.ArgumentList.Add("--mic");
        psi.ArgumentList.Add(request.Microphone);
        if (request.ProcessName is not null)
        {
            psi.ArgumentList.Add("--process");
            psi.ArgumentList.Add(request.ProcessName);
        }
        if (request.Pid is not null)
        {
            psi.ArgumentList.Add("--pid");
            psi.ArgumentList.Add(request.Pid.Value.ToString(CultureInfo.InvariantCulture));
        }
        psi.ArgumentList.Add("--output-dir");
        psi.ArgumentList.Add(callDir);
        if (request.Duration is TimeSpan d)
        {
            psi.ArgumentList.Add("--duration-seconds");
            psi.ArgumentList.Add(d.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        }
        if (request.TestFailAfterSeconds is double tf)
        {
            psi.ArgumentList.Add("--test-fail-after-seconds");
            psi.ArgumentList.Add(tf.ToString("0.###", CultureInfo.InvariantCulture));
        }
        return psi;
    }

    private async Task<int> RunRecorderWithStopAsync(
        ProcessStartInfo psi,
        string callDir,
        CancellationToken stopRequested,
        CancellationToken abort)
    {
        int exit;
        using (var proc = Process.Start(psi)
               ?? throw new RecordingException($"cannot start recorder: {Paths.RecorderExe}"))
        {
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) ChildOutput?.Invoke(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) ChildOutput?.Invoke(e.Data); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            var exitTask = proc.WaitForExitAsync(abort);
            var stopTask = Task.Delay(Timeout.Infinite, stopRequested);
            var first = await Task.WhenAny(exitTask, stopTask).ConfigureAwait(false);

            if (first == stopTask)
            {
                Report(OperationStage.Stopping, "Stopping recording...");
                WriteStopMarker(callDir);
                // graceful finalization: recorder patches WAV headers + writes session.json
                using var grace = new CancellationTokenSource(GracefulStopTimeout);
                try
                {
                    await proc.WaitForExitAsync(grace.Token);
                }
                catch (OperationCanceledException)
                {
                    // graceful shutdown genuinely failed; forced kill is the last resort
                    try
                    {
                        proc.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException)
                    {
                        // already exited
                    }
                }
            }
            else
            {
                // child exited on its own (duration expiry or its own Ctrl+C)
                await exitTask;
            }
            exit = proc.ExitCode;
        }
        return exit;
    }

    private static void WriteStopMarker(string callDir)
    {
        try
        {
            File.WriteAllText(Path.Combine(callDir, StopMarkerFileName), string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RecordingException($"cannot write stop marker in '{callDir}': {ex.Message}");
        }
    }

    private static RecordingArtifacts InspectArtifacts(string callDir, int recorderExit)
    {
        string me = Path.Combine(callDir, "me.wav");
        string remote = Path.Combine(callDir, "remote.wav");
        string session = Path.Combine(callDir, "session.json");
        bool meExists = File.Exists(me);
        bool remoteExists = File.Exists(remote);
        bool sessionExists = File.Exists(session);

        string? sessionStatus = null;
        string? errComponent = null;
        string? errMessage = null;
        if (sessionExists)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(session));
                if (doc.RootElement.TryGetProperty("status", out var st))
                {
                    sessionStatus = st.GetString();
                }
                if (doc.RootElement.TryGetProperty("error", out var err))
                {
                    errComponent = err.TryGetProperty("component", out var c) ? c.GetString() : null;
                    errMessage = err.TryGetProperty("message", out var m) ? m.GetString() : null;
                }
            }
            catch (Exception)
            {
                sessionStatus = "unreadable";
            }
        }

        return new RecordingArtifacts(
            callDir, recorderExit, meExists, remoteExists, sessionExists,
            sessionStatus, errComponent, errMessage);
    }
}
