using ReactiveUI;
using LMP.Core.Services;

namespace LMP.Features.Settings;

public class LoginViewModel : ReactiveObject
{
    private readonly CookieAuthService _authService;
    private string _cookieInput = "";

    public string CookieInput
    {
        get => _cookieInput;
        set => this.RaiseAndSetIfChanged(ref _cookieInput, value);
    }

    public LoginViewModel(CookieAuthService authService)
    {
        _authService = authService;
    }

    public void Save()
    {
        if (string.IsNullOrWhiteSpace(CookieInput)) return;
        
        // Убираем лишние пробелы и переносы
        var clean = CookieInput.Replace("\n", "").Replace("\r", "").Trim();
        
        _authService.SaveCookies(clean);
        
        // Закрываем окно (через события или интерфейс окна)
    }

    public void Help()
    {
        // Можно открыть ссылку на инструкцию
        // "1. Go to music.youtube.com"
        // "2. Press F12 -> Network"
        // "3. Refresh page, click any request (e.g. 'browse')"
        // "4. Copy 'Cookie' header value"
        try 
        { 
            var url = "https://github.com/sigma67/ytmusicapi/wiki/Authentication#browser"; // Хорошая инструкция
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }
}