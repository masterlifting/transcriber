using System.Text.Json;
using System.Text.RegularExpressions;

namespace Transcriber.Core.Operations;

/// <summary>
/// Safe deletion of a call directory. Guards: the directory must be directly
/// under the calls root, must look like a call directory (timestamped name),
/// must not be actively transcribing (lock held) and must not be a live
/// recording (fresh "recording" state). Returns an error message or null on
/// success.
/// </summary>
public static class CallDirectoryService
{
    private static readonly Regex CallDirPattern =
        new(@"^\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}(_\w[\w-]*)?$", RegexOptions.Compiled);

    public static string? TryDelete(string callDir, string callsRoot)
    {
        string rootFull = Path.GetFullPath(callsRoot).TrimEnd('\\');
        string dirFull;
        try
        {
            dirFull = Path.GetFullPath(callDir).TrimEnd('\\');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return $"invalid path '{callDir}': {ex.Message}";
        }

        if (!dirFull.StartsWith(rootFull + "\\", StringComparison.OrdinalIgnoreCase))
        {
            return $"refusing to delete '{dirFull}': not inside calls root '{rootFull}'";
        }
        string name = Path.GetFileName(dirFull);
        if (!CallDirPattern.IsMatch(name))
        {
            return $"refusing to delete '{dirFull}': directory name does not look like a call";
        }
        if (!Directory.Exists(dirFull))
        {
            return $"call directory not found: {dirFull}";
        }
        if (TranscriptionLock.IsHeld(dirFull))
        {
            return "cannot delete: transcription is currently running for this call";
        }

        string statePath = Path.Combine(dirFull, "state.json");
        if (File.Exists(statePath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(statePath));
                if (doc.RootElement.TryGetProperty("status", out var st) && st.GetString() == "recording")
                {
                    bool stale = false;
                    if (doc.RootElement.TryGetProperty("updatedAtUtc", out var upd) &&
                        DateTime.TryParse(upd.GetString(), out var updDate))
                    {
                        stale = (DateTime.UtcNow - updDate).TotalSeconds > 90;
                    }
                    if (!stale)
                    {
                        return "cannot delete: a recording appears to be in progress for this call";
                    }
                }
            }
            catch (Exception)
            {
                // unreadable state.json: continue with deletion
            }
        }

        try
        {
            Directory.Delete(dirFull, recursive: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"could not delete '{dirFull}': {ex.Message}";
        }
    }
}
