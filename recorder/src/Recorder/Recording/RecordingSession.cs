using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using LocalTranscriber.Recorder.Audio;
using LocalTranscriber.Recorder.Diagnostics;
using LocalTranscriber.Recorder.ProcessAudio;
using LocalTranscriber.Core.Audio;
using LocalTranscriber.Core.Processes;

namespace LocalTranscriber.Recorder.Recording;

/// <summary>
/// Combined two-track recording: microphone -> me.wav and process loopback
/// -> remote.wav, started as close together as practical, plus session.json
/// with timing metadata.
///
/// session.json is ALWAYS written once recording has started: status
/// "recorded" on success or status "failed" + error details on failure, so
/// downstream orchestration can inspect what actually happened.
/// </summary>
public static class RecordingSession
{
    private static readonly Regex HresultPattern = new("0x[0-9A-Fa-f]{8}", RegexOptions.Compiled);

    public static async Task RunAsync(
        string micSelector,
        string processName,
        int? pidOverride,
        string outputDir,
        TimeSpan? duration,
        CancellationToken globalStop,
        double? testFailAfterSeconds = null)
    {
        // 1. Resolve inputs up front so errors are reported before recording starts.
        var (micDev, micInfo) = AudioEndpointEnumerator.ResolveCaptureDevice(micSelector);
        Marshal.ReleaseComObject(micDev);
        Log.Info($"Microphone resolved: {micInfo.FriendlyName} (id: {micInfo.Id})");

        int pid = ProcessEnumerator.ResolvePid(processName, pidOverride);
        Log.Info($"Target process resolved: PID={pid} ({processName})");

        // 2. Create output directory.
        try
        {
            Directory.CreateDirectory(outputDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException(
                $"Cannot create output directory '{outputDir}': {ex.Message}");
        }

        string mePath = Path.Combine(outputDir, "me.wav");
        string remotePath = Path.Combine(outputDir, "remote.wav");
        string sessionPath = Path.Combine(outputDir, "session.json");

        DateTime sessionStartUtc = DateTime.UtcNow;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(globalStop);
            if (duration.HasValue)
            {
                cts.CancelAfter(duration.Value);
            }

            // Stop-marker support: an external orchestrator (Desktop UI) writes
            // <outputDir>/.stop to request a graceful stop without a console
            // signal. Same finalization path as Ctrl+C / duration expiry.
            string stopMarkerPath = Path.Combine(outputDir, ".stop");
            var markerToken = cts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        await Task.Delay(200, markerToken).ConfigureAwait(false);
                        if (File.Exists(stopMarkerPath))
                        {
                            try { File.Delete(stopMarkerPath); } catch { /* best effort */ }
                            Log.Info("Stop requested via stop-marker file");
                            cts.Cancel();
                            return;
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Log.Warn($"stop-marker watcher: {ex.Message}"); }
            });

            Task<MicrophoneRecorder.Result> micTask =
                Task.Run(() => MicrophoneRecorder.Record(micSelector, mePath, cts.Token, testFailAfterSeconds));
            Task<ProcessLoopbackRecorder.Result> remoteTask =
                Task.Run(() => ProcessLoopbackRecorder.Record(pid, true, remotePath, cts.Token));

            Task allDone = Task.WhenAll(new Task[] { micTask, remoteTask });
            Task stopWait = Task.Delay(Timeout.Infinite, cts.Token);

            var first = await Task.WhenAny(allDone, stopWait);
            if (first == stopWait)
            {
                Log.Info("Stop requested; stopping both capture streams");
            }
            else
            {
                Log.Warn("One capture stream ended early; stopping the other");
            }
            cts.Cancel();

            MicrophoneRecorder.Result micResult;
            ProcessLoopbackRecorder.Result remoteResult;
            try
            {
                await allDone;
                micResult = await micTask;
                remoteResult = await remoteTask;
            }
            catch (Exception ex)
            {
                Log.Error($"Recording failed: {ex.Message}");
                string component = micTask.IsFaulted
                    ? "microphone"
                    : remoteTask.IsFaulted ? "remote" : "session";
                WriteFailureSession(sessionPath, outputDir, sessionStartUtc, component, ex);
                throw;
            }

            DateTime sessionStopUtc = DateTime.UtcNow;

            double offsetMs = (micResult.FirstPacketAtUtc - remoteResult.FirstPacketAtUtc).TotalMilliseconds;
            Log.Info($"Mic first packet   : {micResult.FirstPacketAtUtc:HH:mm:ss.fff}Z");
            Log.Info($"Remote first packet: {remoteResult.FirstPacketAtUtc:HH:mm:ss.fff}Z");
            Log.Info($"Start offset (mic - remote): {offsetMs:F1} ms");

            var meta = new SessionMetadata
            {
                Status = "recorded",
                StartedAtUtc = sessionStartUtc.ToString("O"),
                StoppedAtUtc = sessionStopUtc.ToString("O"),
                StartOffsetMicMinusRemoteMs = offsetMs,
                Microphone = new SessionMetadata.TrackInfo
                {
                    Device = micInfo.FriendlyName,
                    DeviceId = micInfo.Id,
                    File = Path.GetFileName(mePath),
                    Format = micResult.Format.ToString(),
                    DurationSeconds = micResult.Duration.TotalSeconds,
                    Bytes = micResult.BytesWritten,
                    FirstPacketAtUtc = micResult.FirstPacketAtUtc.ToString("O"),
                },
                Remote = new SessionMetadata.TrackInfo
                {
                    Process = processName,
                    Pid = pid,
                    File = Path.GetFileName(remotePath),
                    Format = remoteResult.Format.ToString(),
                    DurationSeconds = remoteResult.Duration.TotalSeconds,
                    Bytes = remoteResult.BytesWritten,
                    FirstPacketAtUtc = remoteResult.FirstPacketAtUtc.ToString("O"),
                },
            };

            WriteSession(sessionPath, meta);

            Console.WriteLine();
            Console.WriteLine($"me.wav     : {mePath}   ({micResult.Format})   {micResult.Duration:g}");
            Console.WriteLine($"remote.wav : {remotePath}   ({remoteResult.Format})   {remoteResult.Duration:g}");
            Console.WriteLine($"session.json: {sessionPath}");
            Log.Info("Recording session complete");
        }
        catch
        {
            // session.json (status=failed) was already written inside the inner
            // catch for capture failures; guard the rethrow for other failures.
            if (Directory.Exists(outputDir) && !File.Exists(sessionPath))
            {
                try
                {
                    WriteFailureSession(sessionPath, outputDir, sessionStartUtc, "session", new InvalidOperationException("recording aborted"));
                }
                catch (Exception writeEx)
                {
                    Log.Warn($"Could not write failed-session metadata: {writeEx.Message}");
                }
            }
            throw;
        }
    }

    private static void WriteFailureSession(
        string sessionPath, string outputDir, DateTime startedUtc, string component, Exception ex)
    {
        try
        {
            var failed = new SessionMetadata
            {
                Status = "failed",
                StartedAtUtc = startedUtc.ToString("O"),
                StoppedAtUtc = DateTime.UtcNow.ToString("O"),
                Error = new SessionMetadata.ErrorInfo
                {
                    Component = component,
                    Message = ex.Message,
                    HResult = ExtractHresult(ex.Message),
                },
            };
            WriteSession(sessionPath, failed);
            Log.Info($"Wrote failed-session metadata: {sessionPath} (component: {component})");
        }
        catch (Exception writeEx)
        {
            Log.Warn($"Could not write failed-session metadata '{sessionPath}': {writeEx.Message}");
        }
    }

    private static void WriteSession(string sessionPath, SessionMetadata meta)
    {
        string json = JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(sessionPath, json);
    }

    private static string? ExtractHresult(string message)
    {
        var match = HresultPattern.Match(message);
        return match.Success ? match.Value : null;
    }
}
