using System.Text;
using System.Text.RegularExpressions;

namespace LMP.Core.Services;

public partial class CookieAuthService
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, string> _cookieMap = [];
    private string _cachedHeaderString = "";

    // Список критически важных кук для сессии.
    private static readonly HashSet<string> CriticalCookieKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "__Secure-1PSIDTS", "__Secure-3PSIDTS", "SIDCC", "SAPISID"
    };

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

    public string GetResurrectionCookieHeader()
    {
        lock (_lock)
        {
            var sb = new StringBuilder();

            var allowList = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "SID", "HSID", "SSID", "APISID", "SAPISID",
                "__Secure-1PSID", "__Secure-3PSID",
                "__Secure-1PAPISID", "__Secure-3PAPISID",
                "LOGIN_INFO", "PREF"
            };

            foreach (var kvp in _cookieMap)
            {
                if (allowList.Contains(kvp.Key))
                {
                    if (sb.Length > 0) sb.Append("; ");
                    sb.Append($"{kvp.Key}={kvp.Value}");
                }
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

    public bool UpdateCookies(IEnumerable<string> setCookieHeaders)
    {
        bool criticalCookieUpdated = false;
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

                        if (key.Contains("1PSIDTS") || key.Contains("SAPISID") || key.Contains("SIDCC"))
                        {
                            criticalCookieUpdated = true;
                        }

                        if (key == "__Secure-1PSIDTS")
                        {
                            _cookieMap["__Secure-3PSIDTS"] = value;
                        }
                    }
                }
            }

            if (criticalCookieUpdated || _cookieMap.Count > 0) RebuildHeaderString();
        }

        if (criticalCookieUpdated) SaveToFile();

        return criticalCookieUpdated;
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