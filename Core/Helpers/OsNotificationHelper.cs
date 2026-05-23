using System.Diagnostics;
using System.Runtime.InteropServices;
using LMP.Core.Models;

namespace LMP.Core.Helpers;

/// <summary>
/// Кроссплатформенные уведомления ОС.
/// Минимальная реализация без внешних зависимостей.
/// </summary>
public static class OsNotificationHelper
{
    /// <summary>
    /// Показывает уведомление ОС.
    /// </summary>
    public static async Task ShowAsync(string title, string message, NotificationSeverity severity)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                await ShowWindowsToastAsync(title, message);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                await ShowLinuxNotificationAsync(title, message, severity);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                await ShowMacNotificationAsync(title, message);
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"[OsNotification] Failed: {ex.Message}");
        }
    }

    private static async Task ShowWindowsToastAsync(string title, string message)
    {
        // PowerShell-based toast notification (works on Windows 10+)
        _ = title.Replace("'", "''").Replace("`", "``");
        _ = message.Replace("'", "''").Replace("`", "``");

        var script = $"""
            [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
            [Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null
            $xml = @"
            <toast>
                <visual>
                    <binding template='ToastGeneric'>
                        <text>{EscapeXml(title)}</text>
                        <text>{EscapeXml(message)}</text>
                    </binding>
                </visual>
                <audio silent='true'/>
            </toast>
            "@
            $XmlDocument = [Windows.Data.Xml.Dom.XmlDocument]::new()
            $XmlDocument.LoadXml($xml)
            $AppId = 'LiteMusicPlayer'
            [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($AppId).Show([Windows.UI.Notifications.ToastNotification]::new($XmlDocument))
            """;

        await RunProcessAsync("powershell", $"-NoProfile -NonInteractive -Command \"{script}\"", 5000);
    }

    private static async Task ShowLinuxNotificationAsync(string title, string message, NotificationSeverity severity)
    {
        var urgency = severity switch
        {
            NotificationSeverity.Error => "critical",
            NotificationSeverity.Warning => "normal",
            _ => "low"
        };

        await RunProcessAsync("notify-send",
            $"--urgency={urgency} --app-name=\"Lite Music Player\" \"{EscapeShell(title)}\" \"{EscapeShell(message)}\"",
            3000);
    }

    private static async Task ShowMacNotificationAsync(string title, string message)
    {
        var script = $"display notification \"{EscapeAppleScript(message)}\" with title \"{EscapeAppleScript(title)}\"";
        await RunProcessAsync("osascript", $"-e '{script}'", 3000);
    }

    private static async Task RunProcessAsync(string fileName, string arguments, int timeoutMs)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        process.Start();

        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(); } catch { /* ignore */ }
        }
    }

    private static string EscapeXml(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static string EscapeShell(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("$", "\\$").Replace("`", "\\`");

    private static string EscapeAppleScript(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}