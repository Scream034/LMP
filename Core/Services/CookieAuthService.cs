using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace MyLiteMusicPlayer.Core.Services;

public class CookieAuthService
{
    private readonly string _storagePath;
    private string _rawCookies = "";
    
    // Используем Firefox UA, так как он более стабилен для эмуляции в связке с клиентом WEB_REMIX
    private const string DefaultUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:128.0) Gecko/20100101 Firefox/128.0";

    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(_rawCookies);
    public static string UserAgent => DefaultUserAgent;

    public event Action? OnAuthStateChanged;

    public CookieAuthService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var folder = Path.Combine(appData, "LiteMusicPlayer");
        Directory.CreateDirectory(folder);
        _storagePath = Path.Combine(folder, "auth_cookies.txt");

        Load();
    }

    private void Load()
    {
        if (File.Exists(_storagePath))
        {
            _rawCookies = File.ReadAllText(_storagePath);
        }
    }

    public void SaveCookies(string cookies)
    {
        if (string.IsNullOrWhiteSpace(cookies)) return;

        var clean = Regex.Replace(cookies, @"^Cookie:\s*", "", RegexOptions.IgnoreCase);
        clean = clean.Replace("\r", "").Replace("\n", "");
        clean = clean.Trim().Trim('"');

        _rawCookies = clean;
        File.WriteAllText(_storagePath, _rawCookies);
        OnAuthStateChanged?.Invoke();
    }
    
    public void SaveUserAgent(string userAgent) 
    {
        // Заглушка, используем константу
    }

    public void Logout()
    {
        _rawCookies = "";
        if (File.Exists(_storagePath)) File.Delete(_storagePath);
        OnAuthStateChanged?.Invoke();
    }

    public CookieContainer GetCookieContainer()
    {
        var container = new CookieContainer();
        if (string.IsNullOrWhiteSpace(_rawCookies)) return container;

        var pairs = _rawCookies.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
        var domains = new[] { 
            new Uri("https://youtube.com"), 
            new Uri("https://music.youtube.com"),
            new Uri("https://www.youtube.com") 
        };

        foreach (var pair in pairs)
        {
            var p = pair.Trim();
            if (string.IsNullOrEmpty(p)) continue;
            
            var eqIndex = p.IndexOf('=');
            if (eqIndex > 0)
            {
                var name = p[..eqIndex].Trim();
                var value = p[(eqIndex + 1)..].Trim();
                
                foreach (var d in domains)
                {
                    try { container.Add(d, new Cookie(name, value)); } catch { }
                }
            }
        }
        return container;
    }

    // --- Локализация ---

    public string GetLanguage()
    {
        // Возвращает код языка (например, "ru", "en")
        try { return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName; }
        catch { return "en"; }
    }

    public string GetRegion()
    {
        // Возвращает код региона (например, "RU", "US")
        try { return RegionInfo.CurrentRegion.TwoLetterISORegionName; }
        catch { return "US"; }
    }
}