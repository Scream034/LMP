using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace LMP.Core.Services;

public class CookieAuthService
{
    private readonly string _storagePath;
    private string _rawCookies = "";
    
    // Используем Firefox, так как он стабильнее для эмуляции WebRemix
    private readonly string _userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:128.0) Gecko/20100101 Firefox/128.0";

    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(_rawCookies);
    public string UserAgent => _userAgent;

    public event Action? OnAuthStateChanged;
    
    // Событие, чтобы уведомить логгер или UI, что куки обновились на диске
    public event Action? OnCookiesUpdated; 

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

    // Сохранение, вызванное пользователем (вставка текста)
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

    // АВТОМАТИЧЕСКОЕ СОХРАНЕНИЕ
    // Вызывается таймером из YoutubeProvider
    public void SyncCookiesFromContainer(CookieContainer container)
    {
        try 
        {
            // Собираем куки с двух основных доменов, так как YouTube размазывает их
            var musicUri = new Uri("https://music.youtube.com");
            var mainUri = new Uri("https://youtube.com");
            
            var cookiesMusic = container.GetCookies(musicUri).Cast<Cookie>();
            var cookiesMain = container.GetCookies(mainUri).Cast<Cookie>();

            // Объединяем и убираем дубликаты по имени
            var allCookies = cookiesMusic.Concat(cookiesMain)
                .GroupBy(c => c.Name)
                .Select(g => g.First()) // Берем первую (обычно самую свежую)
                .ToList();

            if (allCookies.Count == 0) return;

            var sb = new StringBuilder();
            foreach (var cookie in allCookies)
            {
                if (sb.Length > 0) sb.Append("; ");
                sb.Append($"{cookie.Name}={cookie.Value}");
            }

            var newCookiesString = sb.ToString();

            // Пишем на диск, только если строка реально изменилась
            // Это предотвращает износ SSD постоянной записью одного и того же
            if (!string.Equals(_rawCookies, newCookiesString, StringComparison.Ordinal))
            {
                _rawCookies = newCookiesString;
                File.WriteAllText(_storagePath, _rawCookies);
                OnCookiesUpdated?.Invoke(); // Уведомляем систему
            }
        }
        catch (Exception)
        {
            // Игнорируем ошибки при фоновом сохранении, чтобы не крашить плеер
        }
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

        // Разделители: точка с запятой (стандарт) или запятая (иногда встречается при копировании)
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
}