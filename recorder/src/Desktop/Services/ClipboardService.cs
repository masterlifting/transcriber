using System.Windows;

namespace Transcriber.Desktop.Services;

/// <summary>Minimal clipboard abstraction. Implementation must run on the UI/STA thread.</summary>
public interface IClipboardService
{
    void SetText(string text);
}

/// <summary>WPF clipboard implementation (System.Windows.Clipboard).</summary>
public sealed class WpfClipboardService : IClipboardService
{
    public void SetText(string text)
    {
        Clipboard.SetText(text);
    }
}
