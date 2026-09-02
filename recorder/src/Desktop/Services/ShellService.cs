using System.Diagnostics;
using System.IO;

namespace LocalTranscriber.Desktop.Services;

/// <summary>Opens files/folders with the default Windows shell behavior.</summary>
public static class ShellService
{
    public static void OpenFile(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public static void OpenFolder(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
    }
}
