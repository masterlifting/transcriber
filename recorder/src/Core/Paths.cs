using Transcriber.Core.Settings;

namespace Transcriber.Core;

public enum AppMode
{
    Portable,
    Development,
}

/// <summary>
/// Centralized path service. Resolves application files relative to
/// AppContext.BaseDirectory so the same binaries work as a portable package
/// (extracted anywhere) and as a development checkout.
///
/// Layouts:
///   Portable:  &lt;AppRoot&gt;\{Transcriber.Desktop.exe, recorder.exe,
///              python\python.exe, pipeline\, models\turbo}
///   Development: &lt;AppRoot&gt; = project root (D:\transcriber) with
///              .venv\Scripts\python.exe and recorder\src\Recorder\bin\...
///
/// User data (settings, logs, calls) is always written OUTSIDE AppRoot:
/// %LOCALAPPDATA%\Transcriber and Documents\Transcriber\Calls
/// (portable default; development keeps its project-local calls folder).
/// </summary>
public static class Paths
{
    private static readonly Lazy<AppMode> ModeLazy = new(DetectMode);
    private static readonly Lazy<string> AppRootLazy = new(ResolveAppRoot);

    public static AppMode Mode => ModeLazy.Value;

    public static string AppRoot => AppRootLazy.Value;

    /// <summary>Marker file at AppRoot: "full" or "lite" (portable builds only).</summary>
    public static string ModeMarkerPath => Path.Combine(AppRoot, ".portable-mode");

    public static bool IsLiteBuild =>
        Mode == AppMode.Portable &&
        File.Exists(ModeMarkerPath) &&
        string.Equals(File.ReadAllText(ModeMarkerPath).Trim(), "lite", StringComparison.OrdinalIgnoreCase);

    // ---------------- executables ----------------

    public static string RecorderExe =>
        Environment.GetEnvironmentVariable("TRANSCRIBER_RECORDER") ?? ResolveRecorderExe();

    public static string PythonExe => Mode switch
    {
        AppMode.Portable => Path.Combine(AppRoot, "python", "python.exe"),
        _ => Path.Combine(AppRoot, ".venv", "Scripts", "python.exe"),
    };

    // ---------------- pipeline / model ----------------

    public static string PipelineDir => Path.Combine(AppRoot, "pipeline");

    public static string PipelinePackage => Path.Combine(PipelineDir, "__init__.py");

    public static string PipelineWorkingDirectory => AppRoot;

    public static string ModelsDir => Path.Combine(AppRoot, "models");

    /// <summary>Bundled turbo model directory (&lt;AppRoot&gt;\models\turbo).</summary>
    public static string BundledModelPath => Path.Combine(ModelsDir, "turbo");

    // ---------------- user data (outside AppRoot) ----------------

    public static string SettingsDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Transcriber");

    public static string LogsDir => SettingsDir;

    public static string CallsRoot => ResolveCallsRoot();

    // ---------------- resolution ----------------

    private static AppMode DetectMode()
    {
        string baseDir = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(baseDir, "python", "python.exe")) &&
            File.Exists(Path.Combine(baseDir, "pipeline", "__init__.py")))
        {
            return AppMode.Portable;
        }
        return AppMode.Development;
    }

    private static string ResolveAppRoot()
    {
        string baseDir = AppContext.BaseDirectory;
        if (Mode == AppMode.Portable)
        {
            return baseDir;
        }

        // Development: walk up from the executable to the project root,
        // identified by the presence of pipeline\ and (.venv\ or recorder\).
        var dir = new DirectoryInfo(baseDir);
        while (dir is not null)
        {
            bool hasPipeline = Directory.Exists(Path.Combine(dir.FullName, "pipeline"));
            bool hasDev = Directory.Exists(Path.Combine(dir.FullName, ".venv")) ||
                          Directory.Exists(Path.Combine(dir.FullName, "recorder"));
            if (hasPipeline && hasDev)
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return Environment.GetEnvironmentVariable("TRANSCRIBER_ROOT") ?? baseDir;
    }

    private static string ResolveRecorderExe()
    {
        if (Mode == AppMode.Portable)
        {
            return Path.Combine(AppRoot, "recorder.exe");
        }
        string[] candidates =
        {
            Path.Combine(AppRoot, "recorder", "src", "Recorder", "bin", "Release", "net10.0-windows", "recorder.exe"),
            Path.Combine(AppRoot, "recorder", "src", "Recorder", "bin", "Debug", "net10.0-windows", "recorder.exe"),
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private static string ResolveCallsRoot()
    {
        // 1. persisted user setting wins
        string? configured = SettingsService.Load().CallsDirectory;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        // 2. portable default: Documents\Transcriber\Calls
        if (Mode == AppMode.Portable)
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return Path.Combine(documents, "Transcriber", "Calls");
        }

        // 3. development default: project-local calls folder (existing behavior)
        return Path.Combine(AppRoot, "calls");
    }
}
