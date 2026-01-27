using System.Text;
using System.Text.RegularExpressions;

namespace LMP.Core.Services;

public partial class CookieAuthService
{
    private readonly object _lock = new();
    private readonly Dictionary<string, string> _cookieMap = [];
    private string _cachedHeaderString = "";

    public bool IsAuthenticated 
    {
        get 
        {
            lock (_lock) return _cookieMap.ContainsKey("SAPISID");
        }
    }

    public event Action? OnAuthStateChanged;
    
    public CookieAuthService() => Load();

    private void Load()
    {
        if (File.Exists(G.File.Cookie))
        {
            var raw = File.ReadAllText(G.File.Cookie);
            ParseAndSetCookies(raw);
        }
    }

    public string GetCookieHeader()
    {
        lock (_lock) return _cachedHeaderString;
    }

    public string? GetCookieValue(string key)
    {
        lock (_lock) return _cookieMap.TryGetValue(key, out var val) ? val : null;
    }

    /// <summary>
    /// Возвращает куки для "реанимации".
    /// Логика Muzza: используем все куки, но исключаем протухшие сессионные.
    /// ВАЖНО: LOGIN_INFO и PAPISID должны остаться!
    /// </summary>
    public string GetBaseCookieHeader()
    {
        lock (_lock)
        {
            var sb = new StringBuilder();
            
            // Черный список токенов, которые вызывают 401, если они протухли
            var blackList = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "__Secure-1PSIDTS", 
                "__Secure-3PSIDTS",
                "SIDCC", 
                "__Secure-1PSIDCC", 
                "__Secure-3PSIDCC"
            };

            foreach (var kvp in _cookieMap)
            {
                // Пропускаем "плохие" токены
                if (blackList.Contains(kvp.Key)) continue;

                if (sb.Length > 0) sb.Append("; ");
                sb.Append($"{kvp.Key}={kvp.Value}");
            }
            return sb.ToString();
        }
    }

    public void SaveCookies(string cookies)
    {
        if (string.IsNullOrWhiteSpace(cookies)) return;

        var clean = cookies.Replace("\r", "").Replace("\n", "").Trim().Trim('"');
        clean = FindCookieTextRegex().Replace(clean, "");

        if (!clean.Contains("SAPISID"))
        {
            Log.Warn("[Auth] Attempt to save cookies without SAPISID. Ignoring.");
            return;
        }

        ParseAndSetCookies(clean);
        SaveToFile();
        
        Log.Info($"[Auth] Cookies saved manually. Total keys: {_cookieMap.Count}");
        OnAuthStateChanged?.Invoke();
    }

    public void RemoveSessionTokens()
    {
        lock (_lock)
        {
            var keysToRemove = new[] 
            { 
                "__Secure-1PSIDTS", "__Secure-3PSIDTS",
                "SIDCC", "__Secure-1PSIDCC", "__Secure-3PSIDCC" 
            };

            bool removedAny = false;
            foreach (var key in keysToRemove)
            {
                if (_cookieMap.Remove(key)) removedAny = true;
            }

            if (removedAny)
            {
                RebuildHeaderString();
                Log.Warn("[Auth] Session tokens (TS/CC) cleared due to auth failure.");
            }
        }
    }

    public bool UpdateCookies(IEnumerable<string> setCookieHeaders)
    {
        bool changed = false;
        lock (_lock)
        {
            foreach (var header in setCookieHeaders)
            {
                var parts = header.Split(';');
                if (parts.Length == 0) continue;

                var firstPart = parts[0];
                var equalIndex = firstPart.IndexOf('=');
                
                if (equalIndex > 0)
                {
                    var key = firstPart[..equalIndex].Trim();
                    var value = firstPart[(equalIndex + 1)..].Trim();

                    if (string.IsNullOrEmpty(value) || value.Equals("deleted", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!_cookieMap.TryGetValue(key, out var existingVal) || existingVal != value)
                    {
                        _cookieMap[key] = value;
                        changed = true;
                        
                        if (key == "__Secure-1PSIDTS")
                        {
                             _cookieMap["__Secure-3PSIDTS"] = value;
                        }
                    }
                }
            }

            if (changed) RebuildHeaderString();
        }

        if (changed) SaveToFile();
        return changed;
    }

    public void Logout()
    {
        lock (_lock)
        {
            _cookieMap.Clear();
            _cachedHeaderString = "";
        }
        if (File.Exists(G.File.Cookie)) File.Delete(G.File.Cookie);
        OnAuthStateChanged?.Invoke();
    }

    private void ParseAndSetCookies(string raw)
    {
        lock (_lock)
        {
            _cookieMap.Clear();
            var parts = raw.Split(';');
            foreach (var part in parts)
            {
                var equalIndex = part.IndexOf('=');
                if (equalIndex > 0)
                {
                    var key = part[..equalIndex].Trim();
                    var value = part[(equalIndex + 1)..].Trim();
                    if (!string.IsNullOrEmpty(key))
                        _cookieMap[key] = value;
                }
            }
            RebuildHeaderString();
        }
    }

    private void RebuildHeaderString()
    {
        var sb = new StringBuilder();
        foreach (var kvp in _cookieMap)
        {
            if (sb.Length > 0) sb.Append("; ");
            sb.Append($"{kvp.Key}={kvp.Value}");
        }
        _cachedHeaderString = sb.ToString();
    }

    private void SaveToFile()
    {
        lock (_lock)
        {
            try
            {
                if (_cookieMap.ContainsKey("SAPISID"))
                    File.WriteAllText(G.File.Cookie, _cachedHeaderString);
            }
            catch (Exception ex)
            {
                Log.Error($"[Auth] Failed to save updated cookies: {ex.Message}");
            }
        }
    }

    [GeneratedRegex(@"^Cookie:\s*", RegexOptions.IgnoreCase, "ru-RU")]
    private static partial Regex FindCookieTextRegex();
}