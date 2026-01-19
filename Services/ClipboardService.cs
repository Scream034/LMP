using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace MyLiteMusicPlayer.Services;

public interface IClipboardService
{
    Task SetTextAsync(string text);
}

public class ClipboardService : IClipboardService
{
    public async Task SetTextAsync(string text)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = desktop.MainWindow;
            if (window?.Clipboard != null)
            {
                await window.Clipboard.SetTextAsync(text);
            }
        }
    }
}