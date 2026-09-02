using System.Diagnostics;
using System.Text;

namespace Transcriber.Core;

/// <summary>
/// Child-process helpers shared by CLI and Desktop. The recorder child is
/// spawned hidden with streamed output (no console window, no console-output
/// parsing by callers). The Python pipeline is spawned with streamed output
/// and clean cancellation (process tree killed on abort so no orphans remain).
/// </summary>
public static class ProcessRunner
{
    private static readonly object ConsoleGate = new();

    /// <summary>
    /// Run recorder.exe as a hidden child with stdout/stderr streamed via
    /// callbacks. Returns the child's exit code. Kills the tree only if
    /// <paramref name="abort"/> fires (exceptional path).
    /// </summary>
    public static async Task<int> RunRecorderStreamedAsync(
        IReadOnlyList<string> args,
        Action<string>? onOutput,
        Action<string>? onError,
        string workingDirectory,
        CancellationToken abort)
    {
        DependencyValidator.RequireRecorder();
        var psi = new ProcessStartInfo
        {
            FileName = Paths.RecorderExe,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = Process.Start(psi)
            ?? throw new DependencyException($"cannot start recorder: {Paths.RecorderExe}");
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) onOutput?.Invoke(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) onError?.Invoke(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        try
        {
            await proc.WaitForExitAsync(abort);
        }
        catch (OperationCanceledException)
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // already exited
            }
            throw;
        }
        return proc.ExitCode;
    }

    /// <summary>
    /// Tells the Python pipeline which Whisper model to use:
    /// - Full portable: LT_MODEL_PATH = &lt;AppRoot&gt;\models\turbo (no download).
    /// - Lite portable: LT_ALLOW_DOWNLOAD=1 (first transcription downloads into the
    ///   user profile HF cache; offline afterwards).
    /// - Development:   LT_ALLOW_DOWNLOAD=1 (HF cache, as before).
    /// No silent fallback: portable-full with a missing model is an error.
    /// </summary>
    private static void ApplyModelEnvironment(ProcessStartInfo psi)
    {
        string modelPath = Paths.BundledModelPath;
        if (Directory.Exists(modelPath) && File.Exists(Path.Combine(modelPath, "model.bin")))
        {
            psi.Environment["LT_MODEL_PATH"] = modelPath;
            return;
        }
        if (Paths.IsLiteBuild || Paths.Mode == AppMode.Development)
        {
            psi.Environment["LT_ALLOW_DOWNLOAD"] = "1";
            return;
        }
        throw new DependencyException(
            $"Whisper model is missing: '{modelPath}' does not contain model.bin. " +
            "Reinstall the full package or use the lite package.");
    }

    /// <summary>
    /// Run a short recorder query (list-inputs / list-processes) capturing stdout.
    /// </summary>
    public static async Task<string> RunRecorderCaptureAsync(string[] args)
    {
        DependencyValidator.RequireRecorder();
        var psi = new ProcessStartInfo
        {
            FileName = Paths.RecorderExe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        using var proc = Process.Start(psi)
            ?? throw new DependencyException($"cannot start recorder: {Paths.RecorderExe}");
        string stdout = await proc.StandardOutput.ReadToEndAsync();
        string stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        if (proc.ExitCode != 0)
        {
            string detail = string.IsNullOrWhiteSpace(stderr) ? "" : $" ({stderr.Trim()})";
            throw new RecordingException(
                $"recorder '{string.Join(' ', args)}' failed with exit code {proc.ExitCode}{detail}");
        }
        return stdout;
    }

    /// <summary>
    /// Run the Phase 3 Python pipeline, streaming stdout/stderr live. Working
    /// directory is pinned to the project root. On abort the child process
    /// tree is killed so no orphaned transcription jobs remain.
    /// </summary>
    public static async Task<int> RunPythonPipelineAsync(
        string callDir,
        Action<string>? onOutput,
        Action<string>? onError,
        CancellationToken abort)
    {
        DependencyValidator.RequirePython();
        var psi = new ProcessStartInfo
        {
            FileName = Paths.PythonExe,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Paths.PipelineWorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add("pipeline.cli");
        psi.ArgumentList.Add(callDir);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        ApplyModelEnvironment(psi);

        using var proc = Process.Start(psi)
            ?? throw new DependencyException($"cannot start python: {Paths.PythonExe}");
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (ConsoleGate)
                {
                    onOutput?.Invoke(e.Data);
                }
            }
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (ConsoleGate)
                {
                    onError?.Invoke(e.Data);
                }
            }
        };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        try
        {
            await proc.WaitForExitAsync(abort);
        }
        catch (OperationCanceledException)
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // already exited
            }
            throw;
        }
        return proc.ExitCode;
    }
}
