using System.Diagnostics;
using System.Runtime.InteropServices;
using Transcriber.Core.Diagnostics;
using Transcriber.Core.Interop;

namespace Transcriber.Core.Processes;

public sealed record ProcessInfo(
    int Pid,
    string Name,
    int ParentPid,
    bool HasMainWindow,
    string Title,
    DateTime StartTime);

/// <summary>
/// Process discovery with parent-PID lookup (NtQueryInformationProcess).
/// </summary>
public static class ProcessEnumerator
{
    public static List<ProcessInfo> Find(string? nameFilter)
    {
        var result = new List<ProcessInfo>();
        foreach (var p in Process.GetProcesses())
        {
            if (nameFilter is not null &&
                !p.ProcessName.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string title = Safe(() => p.MainWindowTitle) ?? "";
            IntPtr hwnd = Safe(() => p.MainWindowHandle);
            bool hasWindow = !string.IsNullOrEmpty(title) || hwnd != IntPtr.Zero;
            int parentPid = Safe(() => GetParentPid(p));
            DateTime startTime = Safe(() => p.StartTime);

            result.Add(new ProcessInfo(
                p.Id,
                p.ProcessName,
                parentPid,
                hasWindow,
                title,
                startTime));
        }
        return result
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Pid)
            .ToList();
    }

    /// <summary>
    /// Resolves a target PID from an explicit PID or a process-name filter.
    /// With multiple matches the selection is deterministic: prefer the
    /// process with a main window, otherwise the lowest PID. Never hardcoded.
    /// </summary>
    public static int ResolvePid(string? name, int? explicitPid)
    {
        if (explicitPid is int pid)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                return pid;
            }
            catch (ArgumentException)
            {
                throw new InvalidOperationException($"Process not found: PID {pid}");
            }
            catch (InvalidOperationException)
            {
                throw new InvalidOperationException($"Process not found: PID {pid}");
            }
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Specify --process <name> or --pid <n>");
        }

        var matches = Find(name);
        if (matches.Count == 0)
        {
            throw new InvalidOperationException($"Process not found: '{name}'");
        }

        if (matches.Count > 1)
        {
            Log.Warn($"Multiple processes match '{name}':");
            foreach (var m in matches)
            {
                Log.Warn($"  PID {m.Pid}  {m.Name}  parent={m.ParentPid}  window=\"{m.Title}\"");
            }
        }

        var chosen = matches.FirstOrDefault(m => m.HasMainWindow) ?? matches.OrderBy(m => m.Pid).First();
        if (matches.Count > 1)
        {
            Log.Info($"Selecting PID {chosen.Pid} ({chosen.Name})");
        }
        return chosen.Pid;
    }

    private static int GetParentPid(Process p)
    {
        try
        {
            int hr = Win32.NtQueryInformationProcess(
                p.Handle,
                0, // ProcessBasicInformation
                out var pbi,
                (uint)Marshal.SizeOf<Win32.PROCESS_BASIC_INFORMATION>(),
                out _);
            return hr == 0 ? (int)pbi.InheritedFromUniqueProcessId : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static T? Safe<T>(Func<T> f)
    {
        try
        {
            return f();
        }
        catch
        {
            return default;
        }
    }
}
