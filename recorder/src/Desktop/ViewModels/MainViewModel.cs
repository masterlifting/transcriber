using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Transcriber.Core;
using Transcriber.Core.Audio;
using Transcriber.Core.Operations;
using Transcriber.Core.Processes;
using Transcriber.Core.Settings;
using Transcriber.Desktop.Models;
using Transcriber.Desktop.Services;

namespace Transcriber.Desktop.ViewModels;

public enum UiStage
{
    Ready,
    Recording,
    Stopping,
    Transcribing,
    Completed,
    Failed,
}

public sealed class ApplicationOption
{
    public string Name { get; init; } = "";
    public string Process { get; init; } = "";
}

public sealed class MicrophoneOption
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string DisplayName => Name;
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly Dispatcher _dispatcher;
    private readonly AppSettings _settings;

    private UiStage _stage = UiStage.Ready;
    private string _statusText = "Select an application and microphone, then press Start Recording.";
    private string _elapsedText = "00:00:00";
    private string _resultDetail = "";
    private string _resultDuration = "";
    private string _resultApplication = "";
    private RecordingOrchestrator? _orchestrator;
    private CancellationTokenSource? _stopCts;
    private CancellationTokenSource? _abortCts;
    private DateTime _recordingStarted;
    private string? _lastCallDir;
    private DispatcherTimer? _timer;
    private bool _closeAfterOperation;
    private bool _skipTranscription;
    private readonly IClipboardService _clipboardService;
    private CancellationTokenSource? _feedbackCts;

    public MainViewModel()
        : this(new WpfClipboardService())
    {
    }

    public MainViewModel(IClipboardService clipboardService)
    {
        _dispatcher = Application.Current.Dispatcher;
        _settings = SettingsService.Load();
        _clipboardService = clipboardService;

        Applications.Add(new ApplicationOption { Name = "Telegram Desktop", Process = "Telegram" });
        Applications.Add(new ApplicationOption { Name = "Firefox / Browser Meeting", Process = "Firefox" });

        StartCommand = new AsyncRelayCommand(_ => StartAsync(), _ => Stage == UiStage.Ready);
        StopCommand = new RelayCommand(_ => StopRecording());
        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync());
        OpenTranscriptCommand = new AsyncRelayCommand(p => OpenTranscriptAsync(p));
        OpenFolderCommand = new AsyncRelayCommand(p => OpenFolderAsync(p));
        RetryTranscriptionCommand = new AsyncRelayCommand(p => RetryTranscriptionAsync(p));
        DeleteCallCommand = new AsyncRelayCommand(p => DeleteCallAsync(p));
        CopyTranscriptCommand = new AsyncRelayCommand(p => CopyTranscriptAsync(p));
        ChangeCallsFolderCommand = new RelayCommand(_ => ChangeCallsFolder());
        RecordAnotherCommand = new RelayCommand(_ => RecordAnother());

        RestoreSelections();
        RefreshTargetStatus();
        RefreshCallsFolderDisplay();
        _ = RefreshAsync();
    }

    // ---------------- collections ----------------

    public ObservableCollection<ApplicationOption> Applications { get; } = new();
    public ObservableCollection<MicrophoneOption> Microphones { get; } = new();
    public ObservableCollection<RecentCallViewModel> RecentCalls { get; } = new();

    // ---------------- commands ----------------

    public AsyncRelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand OpenTranscriptCommand { get; }
    public AsyncRelayCommand OpenFolderCommand { get; }
    public AsyncRelayCommand RetryTranscriptionCommand { get; }
    public AsyncRelayCommand DeleteCallCommand { get; }
    public AsyncRelayCommand CopyTranscriptCommand { get; }
    public RelayCommand ChangeCallsFolderCommand { get; }
    public RelayCommand RecordAnotherCommand { get; }

    // ---------------- selection ----------------

    private ApplicationOption? _selectedApplication;
    public ApplicationOption? SelectedApplication
    {
        get => _selectedApplication;
        set
        {
            if (_selectedApplication == value) return;
            _selectedApplication = value;
            OnPropertyChanged();
            RefreshTargetStatus();
            SaveSettings();
        }
    }

    private MicrophoneOption? _selectedMicrophone;
    public MicrophoneOption? SelectedMicrophone
    {
        get => _selectedMicrophone;
        set
        {
            if (_selectedMicrophone == value) return;
            _selectedMicrophone = value;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    // ---------------- state ----------------

    public UiStage Stage
    {
        get => _stage;
        private set
        {
            if (_stage == value) return;
            _stage = value;
            OnPropertyChanged(nameof(Stage));
            OnPropertyChanged(nameof(StageText));
            OnPropertyChanged(nameof(SelectorsEnabled));
            OnPropertyChanged(nameof(StartEnabled));
            OnPropertyChanged(nameof(StopEnabled));
            OnPropertyChanged(nameof(TimerVisible));
            OnPropertyChanged(nameof(ProgressVisible));
            OnPropertyChanged(nameof(StartStopPanelVisible));
            OnPropertyChanged(nameof(ResultActionsVisible));
            OnPropertyChanged(nameof(RetryVisible));
            OnPropertyChanged(nameof(CanCopyTranscript));
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set { _statusText = value; OnPropertyChanged(); }
    }

    public string ElapsedText
    {
        get => _elapsedText;
        private set { _elapsedText = value; OnPropertyChanged(); }
    }

    public string ResultDetail
    {
        get => _resultDetail;
        private set { _resultDetail = value; OnPropertyChanged(); }
    }

    public string ResultDuration
    {
        get => _resultDuration;
        private set { _resultDuration = value; OnPropertyChanged(); }
    }

    public string ResultApplication
    {
        get => _resultApplication;
        private set { _resultApplication = value; OnPropertyChanged(); }
    }

    public string StageText => Stage switch
    {
        UiStage.Ready => StatusText,
        UiStage.Recording => "● Recording",
        UiStage.Stopping => "Stopping recording...",
        UiStage.Transcribing => "Transcribing...",
        UiStage.Completed => "Transcript ready",
        UiStage.Failed => "Failed",
        _ => StatusText,
    };

    public bool SelectorsEnabled => Stage is UiStage.Ready or UiStage.Completed or UiStage.Failed;
    public bool StartEnabled => Stage == UiStage.Ready;
    public bool StopEnabled => Stage == UiStage.Recording;
    public bool TimerVisible => Stage == UiStage.Recording;
    public bool ProgressVisible => Stage == UiStage.Transcribing;
    public bool StartStopPanelVisible => Stage is UiStage.Ready or UiStage.Recording or UiStage.Stopping;
    public bool ResultActionsVisible => Stage is UiStage.Completed or UiStage.Failed;
    public bool RetryVisible => Stage == UiStage.Failed && _lastCallDir is not null &&
        (File.Exists(Path.Combine(_lastCallDir, "me.wav")) || File.Exists(Path.Combine(_lastCallDir, "remote.wav")));

    /// <summary>Copy Transcript is enabled only when transcript.md exists.</summary>
    public bool CanCopyTranscript =>
        _lastCallDir is not null && File.Exists(Path.Combine(_lastCallDir, "transcript.md"));

    // ---------------- copy feedback ----------------

    private string _copyFeedback = "";
    public string CopyFeedback
    {
        get => _copyFeedback;
        private set
        {
            _copyFeedback = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CopyFeedbackVisible));
        }
    }

    public bool CopyFeedbackVisible => CopyFeedback.Length > 0;

    private async Task ShowCopyFeedbackAsync(string text)
    {
        _feedbackCts?.Cancel();
        var cts = _feedbackCts = new CancellationTokenSource();
        CopyFeedback = text;
        try
        {
            await Task.Delay(2500, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        CopyFeedback = "";
    }

    // ---------------- target running indicator ----------------

    private string _targetStatusText = "";
    private Brush _targetStatusBrush = new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0x90));

    public string TargetStatusText
    {
        get => _targetStatusText;
        private set { _targetStatusText = value; OnPropertyChanged(); }
    }

    public Brush TargetStatusBrush
    {
        get => _targetStatusBrush;
        private set { _targetStatusBrush = value; OnPropertyChanged(); }
    }

    private void RefreshTargetStatus()
    {
        var app = SelectedApplication;
        if (app is null)
        {
            TargetStatusText = "";
            return;
        }
        try
        {
            bool running = ProcessEnumerator.Find(app.Process).Any();
            TargetStatusText = running ? $"{app.Process} is running" : $"{app.Process} is not running";
            TargetStatusBrush = running
                ? new SolidColorBrush(Color.FromRgb(0x1E, 0x8E, 0x3E))
                : new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
        }
        catch (Exception)
        {
            TargetStatusText = "cannot check process state";
            TargetStatusBrush = new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0x90));
        }
    }

    // ---------------- flow ----------------

    private async Task StartAsync()
    {
        if (Stage != UiStage.Ready) return;
        var app = SelectedApplication;
        var mic = SelectedMicrophone;
        if (app is null) { StatusText = "Select an application first."; return; }
        if (mic is null) { StatusText = "Select a microphone first."; return; }

        StatusText = "Checking target...";
        if (!ProcessEnumerator.Find(app.Process).Any())
        {
            StatusText = $"{app.Name} is not running. Start it, then press Start Recording again.";
            return;
        }
        if (mic.Id == "default" && !AudioEndpointEnumerator.ListCaptureEndpoints().Any(e => e.IsDefault))
        {
            StatusText = "No default microphone is available.";
            return;
        }

        SaveSettings();

        _stopCts = new CancellationTokenSource();
        _abortCts = new CancellationTokenSource();
        _skipTranscription = false;
        _closeAfterOperation = false;

        _orchestrator = new RecordingOrchestrator();
        _orchestrator.Progress += OnProgress;
        _orchestrator.ChildOutput += _ => { };

        _recordingStarted = DateTime.Now;
        Stage = UiStage.Recording;
        StatusText = $"Recording {app.Name}... Press Stop when the meeting is over.";
        StartTimer();

        try
        {
            var request = new RecordingRequest(app.Process, null, mic.Id, null, AutoTranscribe: true);
            var result = await _orchestrator.RunAsync(request, _stopCts.Token, _abortCts.Token);
            _lastCallDir = result.CallDirectory;

            if (_skipTranscription)
            {
                // window close during recording: call preserved, no transcription
                StatusText = "Recording saved. Exiting...";
            }
            else if (result.Success && result.Transcription?.Success == true)
            {
                ShowCompleted(result.CallDirectory, app.Name);
            }
            else if (result.Success && result.Transcription is { Success: false } tx)
            {
                Stage = UiStage.Failed;
                ResultDetail = $"Transcription failed — component: {tx.ErrorComponent}, message: {tx.ErrorMessage} (exit {tx.ExitCode})\nThe recording was preserved.";
                StatusText = "Recording succeeded, transcription failed.";
            }
            else
            {
                Stage = UiStage.Failed;
                ResultDetail = BuildRecordingFailureDetail(result);
                StatusText = "Recording failed.";
            }
        }
        catch (DependencyException ex)
        {
            Stage = UiStage.Failed;
            StatusText = "Dependency problem.";
            ResultDetail = ex.Message;
        }
        catch (RecordingException ex)
        {
            Stage = UiStage.Failed;
            StatusText = "Recording failed.";
            ResultDetail = ex.Message;
        }
        catch (TranscriptionException ex)
        {
            Stage = UiStage.Failed;
            StatusText = "Transcription failed.";
            ResultDetail = ex.Message;
        }
        catch (OperationCanceledException)
        {
            Stage = UiStage.Failed;
            StatusText = "Interrupted.";
        }
        finally
        {
            StopTimer();
            RefreshRecentCalls();
            if (_closeAfterOperation)
            {
                _ = _dispatcher.BeginInvoke(() => Application.Current.Shutdown());
            }
        }
    }

    private static string BuildRecordingFailureDetail(RecordingResult result)
    {
        var r = result.Recording;
        string detail = $"Recorder exit code: {r.RecorderExitCode}\n" +
                        $"me.wav: {(r.MeExists ? "present" : "MISSING")}\n" +
                        $"remote.wav: {(r.RemoteExists ? "present" : "MISSING")}\n" +
                        $"session.json: {(r.SessionExists ? "present" : "MISSING")}";
        if (r.SessionErrorComponent is not null)
        {
            detail += $"\nSession: {r.SessionStatus} (component: {r.SessionErrorComponent}, message: {r.SessionErrorMessage})";
        }
        return detail;
    }

    private void ShowCompleted(string callDir, string appName)
    {
        var call = RecentCallReader.ReadOne(callDir);
        ResultDuration = call.DurationSeconds is double d ? FormatDuration(d) : "—";
        ResultApplication = appName;
        Stage = UiStage.Completed;
        StatusText = "Transcript ready";
    }

    private void StopRecording()
    {
        if (Stage != UiStage.Recording) return;
        Stage = UiStage.Stopping;
        StatusText = "Stopping recording...";
        _stopCts?.Cancel();
    }

    private void RecordAnother()
    {
        _lastCallDir = null;
        StatusText = "Select an application and microphone, then press Start Recording.";
        Stage = UiStage.Ready;
    }

    private async Task RetryTranscriptionAsync(object? param)
    {
        string? dir = (param as RecentCallViewModel)?.Directory ?? _lastCallDir;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

        Stage = UiStage.Transcribing;
        StatusText = "Transcribing...";
        _abortCts ??= new CancellationTokenSource();

        try
        {
            var outcome = await TranscriptionService.RunAsync(
                dir, p => OnProgress(p), _ => { }, _abortCts.Token);
            _lastCallDir = dir;
            if (outcome.Success)
            {
                var app = SelectedApplication?.Name ?? "Call";
                ShowCompleted(dir, app);
            }
            else
            {
                Stage = UiStage.Failed;
                ResultDetail = $"Transcription failed — component: {outcome.ErrorComponent}, message: {outcome.ErrorMessage} (exit {outcome.ExitCode})";
                StatusText = "Transcription failed.";
            }
        }
        catch (TranscriptionException ex)
        {
            Stage = UiStage.Failed;
            StatusText = "Transcription failed.";
            ResultDetail = ex.Message;
        }
        finally
        {
            RefreshRecentCalls();
        }
    }

    // ---------------- recent calls + refresh ----------------

    private async Task RefreshAsync()
    {
        await RefreshMicrophonesAsync();
        RefreshRecentCalls();
        RefreshTargetStatus();
    }

    private async Task RefreshMicrophonesAsync()
    {
        var selectedId = SelectedMicrophone?.Id;
        var selectedName = SelectedMicrophone?.Name;
        Microphones.Clear();

        List<MicrophoneOption> options = new();
        try
        {
            var endpoints = await Task.Run(() => AudioEndpointEnumerator.ListCaptureEndpoints());
            foreach (var e in endpoints)
            {
                string name = e.IsDefault ? $"{e.FriendlyName} (Default)" : e.FriendlyName;
                options.Add(new MicrophoneOption { Id = e.Id, Name = name });
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Microphone enumeration failed: {ex.Message}";
        }

        if (options.Count == 0)
        {
            options.Add(new MicrophoneOption { Id = "default", Name = "Default Microphone" });
        }
        foreach (var o in options)
        {
            Microphones.Add(o);
        }

        SelectedMicrophone =
            Microphones.FirstOrDefault(m => m.Id == selectedId) ??
            Microphones.FirstOrDefault(m => m.Name == selectedName) ??
            Microphones.FirstOrDefault(m => m.Name.Contains("(Default)", StringComparison.OrdinalIgnoreCase)) ??
            Microphones.FirstOrDefault();
    }

    private void RefreshRecentCalls()
    {
        try
        {
            var previous = RecentCalls.ToDictionary(c => c.Directory);
            RecentCalls.Clear();
            foreach (var call in RecentCallReader.ReadRecent(Paths.CallsRoot))
            {
                RecentCalls.Add(new RecentCallViewModel(call));
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Recent calls could not be loaded: {ex.Message}";
        }
    }

    private void ChangeCallsFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select the calls folder",
            InitialDirectory = Paths.CallsRoot,
        };
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            _settings.CallsDirectory = dialog.FolderName;
            SettingsService.Save(_settings);
            RefreshCallsFolderDisplay();
            RefreshRecentCalls();
            _ = ShowCopyFeedbackAsync("Calls folder updated");
        }
    }

    /// <summary>Current calls folder shown above Recent Calls.</summary>
    private string _callsFolderDisplay = "";
    public string CallsFolderDisplay
    {
        get => _callsFolderDisplay;
        private set { _callsFolderDisplay = value; OnPropertyChanged(); }
    }

    private void RefreshCallsFolderDisplay()
    {
        CallsFolderDisplay = Paths.CallsRoot;
    }

    private async Task DeleteCallAsync(object? param)
    {
        if (param is not RecentCallViewModel call)
        {
            return;
        }
        var confirm = MessageBox.Show(
            $"Delete call '{Path.GetFileName(call.Directory)}'?\n\nAll files in this call will be removed permanently.",
            "Delete call",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        string? error = await Task.Run(() => CallDirectoryService.TryDelete(call.Directory, Paths.CallsRoot));
        if (error is not null)
        {
            StatusText = error;
            MessageBox.Show(error, "Delete call", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else
        {
            StatusText = $"Call '{Path.GetFileName(call.Directory)}' deleted.";
        }
        RefreshRecentCalls();
    }

    // ---------------- copy transcript ----------------

    private async Task CopyTranscriptAsync(object? param)
    {
        string? dir = (param as RecentCallViewModel)?.Directory ?? _lastCallDir;
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }
        string md = Path.Combine(dir, "transcript.md");
        if (!File.Exists(md))
        {
            await ShowCopyFeedbackAsync("Transcript not found");
            return;
        }

        string text;
        try
        {
            text = await File.ReadAllTextAsync(md, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"copy transcript read failed: {ex}");
            await ShowCopyFeedbackAsync("Could not copy transcript");
            return;
        }

        bool copied = await CopyToClipboardWithRetryAsync(text);
        await ShowCopyFeedbackAsync(copied ? "✓ Transcript copied" : "Could not copy transcript");
    }

    /// <summary>
    /// Sets clipboard text on the UI/STA thread via the Dispatcher, with a
    /// small bounded retry for the "clipboard busy" case. Never blocks the UI.
    /// </summary>
    private async Task<bool> CopyToClipboardWithRetryAsync(string text)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            bool ok = await _dispatcher.InvokeAsync(() =>
            {
                try
                {
                    _clipboardService.SetText(text);
                    return true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"clipboard set failed (attempt {attempt + 1}): {ex.Message}");
                    return false;
                }
            });
            if (ok)
            {
                return true;
            }
            if (attempt < 2)
            {
                await Task.Delay(200);
            }
        }
        return false;
    }

    // ---------------- open / shell ----------------

    private Task OpenTranscriptAsync(object? param)
    {
        string? dir = (param as RecentCallViewModel)?.Directory ?? _lastCallDir;
        if (string.IsNullOrEmpty(dir)) return Task.CompletedTask;
        ShellService.OpenFile(Path.Combine(dir, "transcript.md"));
        return Task.CompletedTask;
    }

    private Task OpenFolderAsync(object? param)
    {
        string? dir = (param as RecentCallViewModel)?.Directory ?? _lastCallDir;
        if (string.IsNullOrEmpty(dir)) return Task.CompletedTask;
        ShellService.OpenFolder(dir);
        return Task.CompletedTask;
    }

    // ---------------- window close support ----------------

    public void RequestStopAndExit()
    {
        _skipTranscription = true;
        _closeAfterOperation = true;
        if (_orchestrator is not null) _orchestrator.SkipTranscription = true;
        _stopCts?.Cancel();
    }

    public void RequestAbortAndExit()
    {
        _closeAfterOperation = true;
        _abortCts?.Cancel();
    }

    // ---------------- helpers ----------------

    private void OnProgress(OperationProgress p)
    {
        _dispatcher.BeginInvoke(() =>
        {
            switch (p.Stage)
            {
                case OperationStage.Recording:
                    Stage = UiStage.Recording;
                    StatusText = "Recording...";
                    break;
                case OperationStage.Stopping:
                    Stage = UiStage.Stopping;
                    StatusText = "Stopping recording...";
                    break;
                case OperationStage.Transcribing:
                    Stage = UiStage.Transcribing;
                    StatusText = "Transcribing...";
                    break;
                case OperationStage.Completed:
                    Stage = UiStage.Completed;
                    StatusText = "Transcript ready";
                    break;
                case OperationStage.Failed:
                    Stage = UiStage.Failed;
                    StatusText = "Failed";
                    break;
            }
        });
    }

    private void StartTimer()
    {
        _timer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => ElapsedText = (DateTime.Now - _recordingStarted).ToString(@"hh\:mm\:ss");
        _timer.Start();
    }

    private void StopTimer()
    {
        _timer?.Stop();
    }

    private static string FormatDuration(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? t.ToString(@"hh\:mm\:ss") : t.ToString(@"mm\:ss");
    }

    private void RestoreSelections()
    {
        SelectedApplication = Applications.FirstOrDefault(a =>
            a.Process.Equals(_settings.LastApplication, StringComparison.OrdinalIgnoreCase)) ?? Applications.First();
    }

    private void SaveSettings()
    {
        _settings.LastApplication = SelectedApplication?.Process;
        _settings.LastMicrophone = SelectedMicrophone?.Id;
        SettingsService.Save(_settings);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
