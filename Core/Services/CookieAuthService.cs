using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace LMP.Core.Services;

public partial class CookieAuthService
{
    private string _rawCookies = "";

    // Используем Firefox, так как он стабильнее для эмуляции WebRemix
    private readonly string _userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:128.0) Gecko/20100101 Firefox/128.0";
    private static readonly char[] _cookieContainerSeparator = [';', ','];
    private static readonly Uri[] _cookieDomains =
        [
            new Uri("https://youtube.com"),
            new Uri("https://music.youtube.com"),
            new Uri("https://www.youtube.com")
        ];

    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(_rawCookies);
    public string UserAgent => _userAgent;

    public event Action? OnAuthStateChanged;

    // Событие, чтобы уведомить логгер или UI, что куки обновились на диске
    public event Action? OnCookiesUpdated;

    public CookieAuthService()
    {
        Load();
    }

    private void Load()
    {
        if (File.Exists(G.File.Cookie))
        {
            _rawCookies = File.ReadAllText(G.File.Cookie);
        }
    }

    // Сохранение, вызванное пользователем (вставка текста)
    public void SaveCookies(string cookies)
    {
        if (string.IsNullOrWhiteSpace(cookies)) return;

        var clean = FindCookieTextRegex().Replace(cookies, "");
        clean = clean.Replace("\r", "").Replace("\n", "");
        clean = clean.Trim().Trim('"');

        _rawCookies = clean;
        File.WriteAllText(G.File.Cookie, _rawCookies);
        OnAuthStateChanged?.Invoke();
        Log.Info($"Cookies saved. Length: {_rawCookies.Length}");
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

            Log.Info($"Syncing cookies. Music: {cookiesMusic.Count()}, Main: {cookiesMain.Count()}, Total unique: {allCookies.Count}]");
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
                File.WriteAllText(G.File.Cookie, _rawCookies);
                OnCookiesUpdated?.Invoke(); // Уведомляем систему
            }
        }
        catch (Exception ex)
        {
            // Игнорируем ошибки при фоновом сохранении, чтобы не крашить плеер
            Log.Error($"Failed to sync cookies: {ex.Message}");
        }
    }

    public void Logout()
    {
        _rawCookies = "";
        if (File.Exists(G.File.Cookie)) File.Delete(G.File.Cookie);
        OnAuthStateChanged?.Invoke();
    }

    public CookieContainer GetCookieContainer()
    {
        var container = new CookieContainer();
        if (string.IsNullOrWhiteSpace(_rawCookies)) return container;

        // Разделители: точка с запятой (стандарт) или запятая (иногда встречается при копировании)
        var pairs = _rawCookies.Split(_cookieContainerSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var pair in pairs)
        {
            var p = pair.Trim();
            if (string.IsNullOrEmpty(p)) continue;

            var eqIndex = p.IndexOf('=');
            if (eqIndex > 0)
            {
                var name = p[..eqIndex].Trim();
                var value = p[(eqIndex + 1)..].Trim();

                foreach (var d in _cookieDomains)
                {
                    try { container.Add(d, new Cookie(name, value)); } catch { }
                }
            }
        }
        return container;
    }

    [GeneratedRegex(@"^Cookie:\s*", RegexOptions.IgnoreCase, "ru-RU")]
    private static partial Regex FindCookieTextRegex();
}