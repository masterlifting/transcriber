using System.ComponentModel;
using System.Windows;
using Transcriber.Desktop.ViewModels;

namespace Transcriber.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        switch (_viewModel.Stage)
        {
            case UiStage.Recording:
            case UiStage.Stopping:
            {
                var result = MessageBox.Show(
                    "A recording is currently in progress.\n\nStop recording and exit?",
                    "Transcriber",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                e.Cancel = true; // let the operation finalize gracefully first
                if (result == MessageBoxResult.Yes)
                {
                    _viewModel.RequestStopAndExit();
                }
                break;
            }
            case UiStage.Transcribing:
            {
                var result = MessageBox.Show(
                    "Transcription is still running.\n\nCancel transcription and exit?",
                    "Transcriber",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                e.Cancel = true;
                if (result == MessageBoxResult.Yes)
                {
                    _viewModel.RequestAbortAndExit();
                }
                break;
            }
            default:
                // nothing active: close normally
                break;
        }
        await Task.CompletedTask;
    }
}
