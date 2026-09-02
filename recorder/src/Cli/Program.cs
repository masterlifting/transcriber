using System.Diagnostics;
using System.Globalization;
using System.Text;
using Transcriber.Core;
using Transcriber.Core.Operations;

namespace Transcriber.Cli;

/// <summary>
/// transcriber — console entry point. Renders the structured state
/// emitted by Transcriber.Core; all orchestration lives in Core.
///
/// Exit codes:
///   0  success
///   1  general failure
///   2  invalid arguments
///   3  recording failure
///   4  transcription failure
///   5  dependency/configuration failure
/// </summary>
internal static class Program
{
    public const int ExitOk = 0;
    public const int ExitGeneral = 1;
    public const int ExitInvalidArgs = 2;
    public const int ExitRecording = 3;
    public const int ExitTranscription = 4;
    public const int ExitDependency = 5;

    private static readonly CancellationTokenSource GlobalStopCts = new();

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length == 0)
        {
            PrintUsage();
            return ExitInvalidArgs;
        }

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // let us stop gracefully instead of killing the process
            GlobalStopCts.Cancel();
        };

        try
        {
            return args[0] switch
            {
                "list-inputs" => await Passthrough(args),
                "list-processes" => await Passthrough(args),
                "record-mic" => await Passthrough(args),
                "record-process" => await Passthrough(args),
                "record" => await RunRecordAsync(args.Skip(1).ToArray()),
                "transcribe" => await RunTranscribeAsync(args.Skip(1).ToArray()),
                "diag" => RunDiagnostics(),
                "help" or "-h" or "--help" => PrintUsageOk(),
                _ => UsageError($"unknown command: {args[0]}"),
            };
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return ExitInvalidArgs;
        }
        catch (DependencyException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return ExitDependency;
        }
        catch (RecordingException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return ExitRecording;
        }
        catch (TranscriptionException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return ExitTranscription;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("interrupted");
            return ExitGeneral;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"unexpected error: {ex}");
            return ExitGeneral;
        }
    }

    /// <summary>Low-level recorder commands run directly with the inherited console.</summary>
    private static async Task<int> Passthrough(string[] args)
    {
        DependencyValidator.RequireRecorder();
        var psi = new ProcessStartInfo { FileName = Paths.RecorderExe, UseShellExecute = false };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        using var proc = Process.Start(psi)
            ?? throw new DependencyException($"cannot start recorder: {Paths.RecorderExe}");
        await proc.WaitForExitAsync();
        return proc.ExitCode == 0 ? ExitOk : ExitGeneral;
    }

    private static async Task<int> RunRecordAsync(string[] args)
    {
        var opts = ParseOptions(args);
        string? processName = opts.GetValueOrDefault("--process");
        int? pid = ParsePid(opts.GetValueOrDefault("--pid"));
        string mic = opts.GetValueOrDefault("--mic") ?? "default";
        TimeSpan? duration = GetDuration(opts);
        bool noTranscribe = opts.ContainsKey("--no-transcribe");
        double? testFail = ParseTestFail(opts);

        if (processName is null && pid is null)
        {
            throw new UsageException("record requires --process <name> or --pid <n>");
        }
        if (processName is not null && pid is not null)
        {
            throw new UsageException("specify either --process or --pid, not both");
        }

        Console.WriteLine();
        Console.WriteLine("Transcriber");
        Console.WriteLine();
        Console.WriteLine($"Process     {processName ?? $"PID {pid}"}");
        Console.WriteLine($"Microphone  {mic}");
        Console.WriteLine();
        Console.WriteLine("Recording...");
        Console.WriteLine("Press Ctrl+C to stop.");
        Console.WriteLine();

        var orchestrator = new RecordingOrchestrator();
        orchestrator.Progress += p => Console.WriteLine(RenderProgress(p, orchestrator));
        orchestrator.ChildOutput += line => Console.WriteLine(line);

        var result = await orchestrator.RunAsync(
            new RecordingRequest(processName, pid, mic, duration, AutoTranscribe: !noTranscribe, TestFailAfterSeconds: testFail),
            stopRequested: GlobalStopCts.Token,
            abort: CancellationToken.None);

        if (!result.Success)
        {
            Console.WriteLine();
            Console.WriteLine("Recording did not complete cleanly:");
            Console.WriteLine($"  recorder exit code: {result.Recording.RecorderExitCode}");
            Console.WriteLine($"  me.wav:       {(result.Recording.MeExists ? "present" : "MISSING")}");
            Console.WriteLine($"  remote.wav:   {(result.Recording.RemoteExists ? "present" : "MISSING")}");
            Console.WriteLine($"  session.json: {(result.Recording.SessionExists ? "present" : "MISSING")}");
            if (result.Recording.SessionStatus is not null)
            {
                Console.WriteLine($"  session status: {result.Recording.SessionStatus} " +
                                  $"(component: {result.Recording.SessionErrorComponent ?? "?"}, " +
                                  $"message: {result.Recording.SessionErrorMessage ?? "?"})");
            }
            Console.WriteLine();
            Console.WriteLine($"Call directory preserved: {result.CallDirectory}");
            return ExitRecording;
        }

        if (result.Transcription is { Success: false } tx)
        {
            Console.WriteLine();
            Console.WriteLine("Recording succeeded. Transcription failed.");
            Console.WriteLine($"  component: {tx.ErrorComponent}, message: {tx.ErrorMessage}");
            Console.WriteLine("Recording preserved:");
            Console.WriteLine($"  {System.IO.Path.Combine(result.CallDirectory, "me.wav")}");
            Console.WriteLine($"  {System.IO.Path.Combine(result.CallDirectory, "remote.wav")}");
            Console.WriteLine($"  {System.IO.Path.Combine(result.CallDirectory, "session.json")}");
            Console.WriteLine();
            Console.WriteLine("Retry transcription later with:");
            Console.WriteLine($"  transcriber transcribe {result.CallDirectory}");
            return ExitTranscription;
        }

        Console.WriteLine();
        Console.WriteLine(result.CallDirectory);
        return ExitOk;
    }

    private static async Task<int> RunTranscribeAsync(string[] args)
    {
        if (args.Length != 1)
        {
            throw new UsageException("usage: transcriber transcribe <call-directory>");
        }
        string dir = System.IO.Path.GetFullPath(args[0]);
        Console.WriteLine($"Transcribing {dir} ...");
        Console.WriteLine();

        var outcome = await TranscriptionService.RunAsync(
            dir,
            p => Console.WriteLine(RenderProgress(p, null)),
            line => Console.WriteLine(line),
            CancellationToken.None);

        if (outcome.Success)
        {
            Console.WriteLine();
            Console.WriteLine("✓ transcript.json");
            Console.WriteLine("✓ transcript.md");
            Console.WriteLine();
            Console.WriteLine(dir);
            return ExitOk;
        }

        Console.WriteLine();
        Console.WriteLine($"Transcription failed: {outcome.ErrorMessage}");
        Console.WriteLine("Recording preserved.");
        return ExitTranscription;
    }

    /// <summary>Lightweight environment diagnostics for portable-machine debugging.</summary>
    private static int RunDiagnostics()
    {
        Console.WriteLine("Transcriber environment");
        Console.WriteLine($"- Mode:        {Paths.Mode}");
        Console.WriteLine($"- App root:    {Paths.AppRoot}");
        Console.WriteLine($"- Calls root:  {Paths.CallsRoot}");
        Console.WriteLine($"- Settings:    {Paths.SettingsDir}");
        Console.WriteLine($"- Python:      {Paths.PythonExe} (exists: {File.Exists(Paths.PythonExe)})");
        Console.WriteLine($"- Pipeline:    {Paths.PipelineDir} (exists: {Directory.Exists(Paths.PipelineDir)})");
        string model = Path.Combine(Paths.BundledModelPath, "model.bin");
        Console.WriteLine($"- Model:       {Paths.BundledModelPath} (exists: {File.Exists(model)})");
        Console.WriteLine($"- Recorder:    {Paths.RecorderExe} (exists: {File.Exists(Paths.RecorderExe)})");
        Console.WriteLine($"- Architecture: {(Environment.Is64BitOperatingSystem ? "x64" : "x86")} OS, " +
                          $"{(Environment.Is64BitProcess ? "x64" : "x86")} process");
        return 0;
    }

    private static string RenderProgress(OperationProgress p, RecordingOrchestrator? orchestrator)
    {
        return p.Stage switch
        {
            OperationStage.Recording when orchestrator?.CallDirectory is { } dir =>
                $"Directory   {dir}\nRecording...\nPress Ctrl+C to stop.",
            OperationStage.Stopping => "Stopping recording...",
            OperationStage.Transcribing => "Transcribing...",
            OperationStage.Completed => $"✓ {p.Message}",
            OperationStage.Failed => $"✗ {p.Message}",
            _ => p.Message,
        };
    }

    // ---------------- helpers ----------------

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }
            int eq = arg.IndexOf('=');
            if (eq > 0)
            {
                map[arg[..eq]] = arg[(eq + 1)..];
                continue;
            }
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                map[arg] = args[++i];
            }
            else
            {
                map[arg] = "";
            }
        }
        return map;
    }

    private static int? ParsePid(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        if (!int.TryParse(raw, out int pid) || pid <= 0)
        {
            throw new UsageException($"invalid PID value: '{raw}'");
        }
        return pid;
    }

    private static TimeSpan? GetDuration(Dictionary<string, string> opts)
    {
        if (!opts.TryGetValue("--duration-seconds", out string? raw) || string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) || seconds <= 0)
        {
            throw new UsageException($"invalid --duration-seconds value: '{raw}'");
        }
        return TimeSpan.FromSeconds(seconds);
    }

    private static double? ParseTestFail(Dictionary<string, string> opts)
    {
        if (opts.TryGetValue("--test-fail-after-seconds", out string? raw) &&
            double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double tf) && tf > 0)
        {
            return tf; // diagnostic only
        }
        return null;
    }

    private static int UsageError(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        PrintUsage();
        return ExitInvalidArgs;
    }

    private static int PrintUsageOk()
    {
        PrintUsage();
        return ExitOk;
    }

    private static void PrintUsage()
    {
        Console.WriteLine();
        Console.WriteLine("transcriber - record and transcribe calls (Phase 5)");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  transcriber list-inputs");
        Console.WriteLine("  transcriber list-processes [name-filter]");
        Console.WriteLine("  transcriber record-mic --device <name-or-id|default> --output <file.wav> [--duration-seconds N]");
        Console.WriteLine("  transcriber record-process (--process <name> | --pid <n>) --output <file.wav> [--duration-seconds N]");
        Console.WriteLine("  transcriber record --process <name> [--pid <n>] [--mic <name-or-id|default>] [--duration-seconds N] [--no-transcribe]");
        Console.WriteLine("  transcriber transcribe <call-directory>");
        Console.WriteLine();
        Console.WriteLine("Exit codes: 0 success | 1 general | 2 invalid arguments |");
        Console.WriteLine("            3 recording failure | 4 transcription failure | 5 dependency failure");
    }
}
