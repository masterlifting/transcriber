using System.IO;
using System.Windows;

namespace LocalTranscriber.Desktop;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private bool _comInitialized;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                string logPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LocalTranscriber", "crash.log");
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                File.AppendAllText(logPath,
                    $"[{DateTime.Now:O}] {args.Exception}\n\n");
            }
            catch
            {
                // logging must never make the crash worse
            }
        };

        // COM MTA needed for WASAPI device enumeration (IMMDeviceEnumerator).
        int hr = LocalTranscriber.Core.Interop.Win32.CoInitializeEx(IntPtr.Zero, LocalTranscriber.Core.Interop.Win32.COINIT_MULTITHREADED);
        _comInitialized = hr == 0;

        // Prevent multiple instances from recording at the same time.
        _singleInstanceMutex = new Mutex(true, @"Local\LocalTranscriber.Desktop.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "Local Transcriber is already running.",
                "Local Transcriber",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var window = new MainWindow();
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        if (_comInitialized)
        {
            LocalTranscriber.Core.Interop.Win32.CoUninitialize();
        }
        base.OnExit(e);
    }
}
