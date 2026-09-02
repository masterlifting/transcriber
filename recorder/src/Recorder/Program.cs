using Transcriber.Recorder.Audio;
using Transcriber.Recorder.Diagnostics;
using Transcriber.Core.Audio;
using Transcriber.Core.Interop;
using Transcriber.Recorder.ProcessAudio;
using Transcriber.Core.Processes;
using Transcriber.Recorder.Recording;

namespace Transcriber.Recorder;

internal static class Program
{
    private static readonly CancellationTokenSource GlobalStopCts = new();

    private static async Task<int> Main(string[] args)
    {
        int initHr = Win32.CoInitializeEx(IntPtr.Zero, Win32.COINIT_MULTITHREADED);
        bool comInitialized = initHr == 0;
        if (initHr != 0 && initHr != unchecked((int)Win32.RPC_E_CHANGED_MODE))
        {
            Win32.HResult.Check(initHr, "CoInitializeEx");
        }

        try
        {
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true; // let us stop gracefully instead of killing the process
                Log.Info("Stop requested (Ctrl+C)");
                GlobalStopCts.Cancel();
            };

            if (args.Length == 0)
            {
                PrintUsage();
                return 1;
            }

            try
            {
                return args[0] switch
                {
                    "list-inputs" => RunListInputs(),
                    "list-processes" => RunListProcesses(args.Skip(1).ToArray()),
                    "record-mic" => await RunRecordMicAsync(args.Skip(1).ToArray()),
                    "record-process" => await RunRecordProcessAsync(args.Skip(1).ToArray()),
                    "record" => await RunRecordAsync(args.Skip(1).ToArray()),
                    _ => UsageError($"Unknown command: {args[0]}"),
                };
            }
            catch (OperationCanceledException)
            {
                Log.Info("Recording canceled.");
                return 0;
            }
            catch (Exception ex)
            {
                Log.Error($"Error: {ex.Message}");
                if (ex is not (InvalidOperationException or ArgumentException or IOException
                                   or TimeoutException or UnauthorizedAccessException
                                   or System.ComponentModel.Win32Exception))
                {
                    Log.Error(ex.ToString());
                }
                return 1;
            }
        }
        finally
        {
            if (comInitialized)
            {
                Win32.CoUninitialize();
            }
        }
    }

    // ---------------- commands ----------------

    private static int RunListInputs()
    {
        var endpoints = AudioEndpointEnumerator.ListCaptureEndpoints();
        Console.WriteLine();
        Console.WriteLine("Available input devices:");
        Console.WriteLine();
        if (endpoints.Count == 0)
        {
            Console.WriteLine("(none)");
            return 0;
        }

        foreach (var e in endpoints)
        {
            string def = e.IsDefault ? "   [default]" : "";
            Console.WriteLine($"[{e.Index}] {e.FriendlyName}{def}");
            Console.WriteLine($"       id:   {e.Id}");
            if (!string.IsNullOrEmpty(e.DeviceDesc) &&
                !e.DeviceDesc.Equals(e.FriendlyName, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"       desc: {e.DeviceDesc}");
            }
        }
        return 0;
    }

    private static int RunListProcesses(string[] args)
    {
        string? filter = args.Length > 0 ? args[0] : null;
        var procs = ProcessEnumerator.Find(filter);

        Console.WriteLine();
        Console.WriteLine("PID     Parent  Process              Window");
        foreach (var p in procs)
        {
            string window = p.HasMainWindow ? $"yes \"{p.Title}\"" : "-";
            Console.WriteLine($"{p.Pid,6}  {p.ParentPid,6}  {p.Name,-20} {window}");
        }
        Console.WriteLine();
        Console.WriteLine($"{procs.Count} process(es)");
        return 0;
    }

    private static async Task<int> RunRecordMicAsync(string[] args)
    {
        var opts = ParseOptions(args);
        string device = opts.GetValueOrDefault("--device") ?? "default";
        string output = GetRequiredOption(opts, "--output");
        TimeSpan? duration = GetDuration(opts);

        Log.Info($"Recording microphone ('{device}') -> {output}. Press Ctrl+C to stop." +
                 (duration.HasValue ? $" Auto-stop after {duration.Value.TotalSeconds:0}s." : ""));

        using var cts = CreateLinkedStop(duration);
        var result = await Task.Run(() => MicrophoneRecorder.Record(device, output, cts.Token));

        Console.WriteLine();
        Console.WriteLine($"Device     : {result.DeviceName}");
        Console.WriteLine($"Device ID  : {result.DeviceId}");
        Console.WriteLine($"Format     : {result.Format}");
        Console.WriteLine($"Duration   : {result.Duration:g}");
        Console.WriteLine($"Output     : {result.OutputPath}");
        return 0;
    }

    private static async Task<int> RunRecordProcessAsync(string[] args)
    {
        var opts = ParseOptions(args);
        string output = GetRequiredOption(opts, "--output");
        string? processName = opts.GetValueOrDefault("--process");
        int? pid = ParsePid(opts.GetValueOrDefault("--pid"));
        TimeSpan? duration = GetDuration(opts);

        int targetPid = ProcessEnumerator.ResolvePid(processName, pid);

        Log.Info($"Recording process loopback PID={targetPid} -> {output}. Press Ctrl+C to stop." +
                 (duration.HasValue ? $" Auto-stop after {duration.Value.TotalSeconds:0}s." : ""));

        using var cts = CreateLinkedStop(duration);
        var result = await Task.Run(() => ProcessLoopbackRecorder.Record(targetPid, true, output, cts.Token));

        Console.WriteLine();
        Console.WriteLine($"PID          : {result.Pid}");
        Console.WriteLine($"Loopback mode: include process tree");
        Console.WriteLine($"Format       : {result.Format}");
        Console.WriteLine($"Duration     : {result.Duration:g}");
        Console.WriteLine($"Output       : {result.OutputPath}");
        return 0;
    }

    private static async Task<int> RunRecordAsync(string[] args)
    {
        var opts = ParseOptions(args);
        string mic = GetRequiredOption(opts, "--mic");
        string? processName = opts.GetValueOrDefault("--process");
        int? pid = ParsePid(opts.GetValueOrDefault("--pid"));
        string outputDir = GetRequiredOption(opts, "--output-dir");
        TimeSpan? duration = GetDuration(opts);
        double? testFailAfterSeconds = null;
        if (opts.TryGetValue("--test-fail-after-seconds", out string? rawTf) &&
            double.TryParse(rawTf, out double tf) && tf > 0)
        {
            testFailAfterSeconds = tf;
        }

        if (processName is null && pid is null)
        {
            throw new ArgumentException("Specify --process <name> or --pid <n>");
        }

        Log.Info($"Two-track recording: mic='{mic}', process='{processName ?? pid?.ToString()}' -> {outputDir}");
        await RecordingSession.RunAsync(
            mic, processName ?? "", pid, outputDir, duration, GlobalStopCts.Token, testFailAfterSeconds);
        return 0;
    }

    // ---------------- helpers ----------------

    private static CancellationTokenSource CreateLinkedStop(TimeSpan? duration)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(GlobalStopCts.Token);
        if (duration.HasValue)
        {
            cts.CancelAfter(duration.Value);
        }
        return cts;
    }

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
            if (i + 1 >= args.Length)
            {
                throw new ArgumentException($"Missing value for {arg}");
            }
            map[arg] = args[++i];
        }
        return map;
    }

    private static string GetRequiredOption(Dictionary<string, string> opts, string name) =>
        opts.GetValueOrDefault(name) ?? throw new ArgumentException($"{name} is required");

    private static TimeSpan? GetDuration(Dictionary<string, string> opts)
    {
        if (!opts.TryGetValue("--duration-seconds", out string? raw) || string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        if (!double.TryParse(raw, out double seconds) || seconds <= 0)
        {
            throw new ArgumentException($"Invalid --duration-seconds value: '{raw}'");
        }
        return TimeSpan.FromSeconds(seconds);
    }

    private static int? ParsePid(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        if (!int.TryParse(raw, out int pid) || pid <= 0)
        {
            throw new ArgumentException($"Invalid PID value: '{raw}'");
        }
        return pid;
    }

    private static int UsageError(string message)
    {
        Log.Error(message);
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine();
        Console.WriteLine("Transcriber.Recorder - two-track Windows call recorder (prototype)");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  recorder list-inputs");
        Console.WriteLine("  recorder list-processes [name-filter]");
        Console.WriteLine("  recorder record-mic --device <name-or-id|default> --output <file.wav> [--duration-seconds N]");
        Console.WriteLine("  recorder record-process (--process <name> | --pid <n>) --output <file.wav> [--duration-seconds N]");
        Console.WriteLine("  recorder record --mic <name-or-id> --process <name> [--pid <n>] --output-dir <dir> [--duration-seconds N]");
        Console.WriteLine("  recorder record ... [--test-fail-after-seconds N]   (diagnostic: inject a mid-session mic failure)");
        Console.WriteLine();
        Console.WriteLine("Stop recording with Ctrl+C. --duration-seconds is an optional auto-stop for automation.");
    }
}
