using System.Net;
using System.Text.RegularExpressions;

namespace MyLiteMusicPlayer.Core.Services;

public class CookieAuthService
{
    private readonly string _storagePath;
    private readonly string _uaPath; // Путь к файлу User-Agent
    private string _rawCookies = "";
    // Дефолтный UA (Chrome 122), на случай если пользователь ничего не настроил
    private string _userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";

    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(_rawCookies);
    public string UserAgent => _userAgent; // Публичное свойство

    public event Action? OnAuthStateChanged;

    public CookieAuthService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var folder = Path.Combine(appData, "LiteMusicPlayer");
        Directory.CreateDirectory(folder);
        _storagePath = Path.Combine(folder, "auth_cookies.txt");
        _uaPath = Path.Combine(folder, "auth_ua.txt"); // Файл для UA

        Load();
    }

    private void Load()
    {
        if (File.Exists(_storagePath))
        {
            _rawCookies = File.ReadAllText(_storagePath);
        }
        
        if (File.Exists(_uaPath))
        {
            var storedUa = File.ReadAllText(_uaPath).Trim();
            if (!string.IsNullOrWhiteSpace(storedUa))
            {
                _userAgent = storedUa;
            }
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

    // Новый метод для сохранения UA
    public void SaveUserAgent(string userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent)) return;
        
        _userAgent = userAgent.Trim();
        File.WriteAllText(_uaPath, _userAgent);
        // Вызываем событие, чтобы клиент перезагрузился с новым UA
        OnAuthStateChanged?.Invoke();
    }

    public void Logout()
    {
        _rawCookies = "";
        if (File.Exists(_storagePath)) File.Delete(_storagePath);
        OnAuthStateChanged?.Invoke();
    }

    public List<Cookie> GetCookies()
    {
        var list = new List<Cookie>();
        if (string.IsNullOrWhiteSpace(_rawCookies)) return list;

        var pairs = _rawCookies.Split(';');
        foreach (var pair in pairs)
        {
            var p = pair.Trim();
            if (string.IsNullOrEmpty(p)) continue;
            var eqIndex = p.IndexOf('=');
            if (eqIndex > 0)
            {
                var name = p[..eqIndex].Trim();
                var value = p[(eqIndex + 1)..].Trim();
                var cookie = new Cookie(name, value, "/", ".youtube.com");
                list.Add(cookie);
            }
        }
        return list;
    }
}