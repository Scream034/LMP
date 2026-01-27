using System.Text.RegularExpressions;

namespace LMP.Core.Services;

public partial class CookieAuthService
{
    private string _rawCookies = "";

    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(_rawCookies);

    public event Action? OnAuthStateChanged;
    
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

    /// <summary>
    /// Метод для получения сырой строки (используется провайдером)
    /// </summary>
    /// <returns></returns>
    public string GetRawCookies()
    {
        return _rawCookies;
    }

    /// <summary>
    /// Сохранение, вызванное ТОЛЬКО пользователем (логин/вставка)
    /// </summary>
    /// <param name="cookies"></param>
    public void SaveCookies(string cookies)
    {
        if (string.IsNullOrWhiteSpace(cookies)) return;

        // Чистим ввод от заголовка "Cookie: " если он есть
        var clean = FindCookieTextRegex().Replace(cookies, "");
        clean = clean.Replace("\r", "").Replace("\n", "");
        clean = clean.Trim().Trim('"');

        // Валидация: проверяем наличие критических полей перед сохранением
        if (!clean.Contains("SAPISID"))
        {
            Log.Warn("[Auth] Attempt to save cookies without SAPISID. Ignoring.");
            return;
        }

        _rawCookies = clean;
        // Перезаписываем файл
        File.WriteAllText(G.File.Cookie, _rawCookies);
        
        Log.Info($"[Auth] Cookies saved manually. Length: {_rawCookies.Length}");
        OnAuthStateChanged?.Invoke();
    }

    public void Logout()
    {
        _rawCookies = "";
        if (File.Exists(G.File.Cookie)) File.Delete(G.File.Cookie);
        OnAuthStateChanged?.Invoke();
    }

    [GeneratedRegex(@"^Cookie:\s*", RegexOptions.IgnoreCase, "ru-RU")]
    private static partial Regex FindCookieTextRegex();
}