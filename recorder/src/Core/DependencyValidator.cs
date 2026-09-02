namespace LocalTranscriber.Core;

/// <summary>Pre-flight checks before recording or transcription.</summary>
public static class DependencyValidator
{
    public static void RequireRecorder()
    {
        if (!File.Exists(Paths.RecorderExe))
        {
            throw new DependencyException(
                $"recorder executable not found: {Paths.RecorderExe} " +
                "(build it with: dotnet build D:\\local-transcriber\\recorder\\LocalTranscriber.Recorder.sln)");
        }
    }

    public static void RequirePython()
    {
        if (!File.Exists(Paths.PythonExe))
        {
            throw new DependencyException($"python not found: {Paths.PythonExe}");
        }
        if (!File.Exists(Paths.PipelinePackage))
        {
            throw new DependencyException($"pipeline package not found: {Paths.PipelinePackage}");
        }
    }

    public static void RequireCallsRoot()
    {
        try
        {
            Directory.CreateDirectory(Paths.CallsRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DependencyException(
                $"cannot create calls root '{Paths.CallsRoot}': {ex.Message}");
        }
    }

    public static void RequireAll()
    {
        RequireRecorder();
        RequirePython();
        RequireCallsRoot();
    }
}
