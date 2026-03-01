using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LMP.Core.Models;

namespace LMP.Core.Services;

public partial class CookieAuthService
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, string> _cookieMap = new(32, StringComparer.Ordinal);
    private string _cachedHeaderString = "";

    private readonly string _authDataPath = Path.Combine(AppContext.BaseDirectory, "auth.json");

    /// <summary>
    /// Статический FrozenSet для resurrection cookies — O(1) проверка, zero alloc.
    /// </summary>
    private static readonly FrozenSet<string> ResurrectionCookieNames = new[]
    {
        "SID", "HSID", "SSID", "APISID", "SAPISID",
        "__Secure-1PSID", "__Secure-3PSID",
        "__Secure-1PAPISID", "__Secure-3PAPISID",
        "LOGIN_INFO", "PREF"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Критические куки, при обновлении которых нужно сохранить файл.
    /// </summary>
    private static readonly FrozenSet<string> CriticalCookieNames = new[]
    {
        "1PSIDTS", "SAPISID", "SIDCC"
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Ошибка загрузки профиля при старте. Не null → auth.json не удалось прочитать.
    /// </summary>
    private string? _profileLoadError;
    public bool HasProfileLoadError => _profileLoadError != null;

    public AuthState State { get; private set; } = new();

    public bool IsAuthenticated
    {
        get
        {
            lock (_lock) return _cookieMap.ContainsKey("SAPISID");
        }
    }

    public event Action? OnAuthStateChanged;

    public CookieAuthService()
    {
        LoadCookies();
        LoadAuthData();
        UpdateStateAuthStatus();
    }

    // --- Profile Management ---

    public void UpdateUserProfile(string name, string email, string avatarUrl)
    {
        State.UserName = name;
        State.UserEmail = email;
        State.AvatarUrl = avatarUrl;
        State.LastUpdated = DateTime.UtcNow;
        State.IsAuthenticated = IsAuthenticated;

        SaveAuthData();
        OnAuthStateChanged?.Invoke();
    }

    private void LoadAuthData()
    {
        if (!File.Exists(_authDataPath)) return;

        try
        {
            var json = File.ReadAllText(_authDataPath);

            if (string.IsNullOrWhiteSpace(json))
            {
                Log.Warn("[Auth] auth.json is empty");
                return;
            }

            var loadedState = JsonSerializer.Deserialize<AuthState>(json);
            if (loadedState != null)
            {
                State = loadedState;
                Log.Info($"[Auth] Profile restored from cache: {State.UserName} (updated: {State.LastUpdated:g})");
            }
        }
        catch (JsonException ex)
        {
            _profileLoadError = ex.Message;
            Log.Error($"[Auth] Corrupted auth.json: {ex.Message}");

            // Пытаемся спасти: если куки валидны, не перезаписываем файл дефолтным State
            // State остаётся Guest, но мы не вызываем SaveAuthData() чтобы не затереть файл
        }
        catch (IOException ex)
        {
            _profileLoadError = ex.Message;
            Log.Error($"[Auth] Cannot read auth.json (file locked?): {ex.Message}");
        }
        catch (Exception ex)
        {
            _profileLoadError = ex.Message;
            Log.Error($"[Auth] Failed to load auth data: {ex.Message}");
        }
    }

    private void SaveAuthData()
    {
        try
        {
            var json = JsonSerializer.Serialize(State, G.Json.Beautiful);
            File.WriteAllText(_authDataPath, json);
        }
        catch (Exception ex)
        {
            Log.Error($"[Auth] Failed to save auth data: {ex.Message}");
        }
    }

    private void UpdateStateAuthStatus()
    {
        bool currentAuth = IsAuthenticated;
        if (State.IsAuthenticated != currentAuth)
        {
            State.IsAuthenticated = currentAuth;

            // Не перезаписываем auth.json если загрузка провалилась —
            // файл может содержать валидные данные профиля, которые мы не смогли прочитать.
            if (!HasProfileLoadError)
            {
                SaveAuthData();
            }
            else
            {
                Log.Warn("[Auth] Skipping SaveAuthData — profile load error present, avoiding overwrite");
            }
        }
    }

    /// <summary>
    /// Лёгкая проверка валидности сессии через HTTP запрос к YouTube Music.
    /// Не бросает исключений — возвращает результат проверки.
    /// </summary>
    /// <returns>
    /// true  — сессия валидна (или нет сети — оптимистичный подход)
    /// false — куки точно протухли (HTTP 401/403 от Google)
    /// </returns>
    public async Task<(bool IsValid, string? Error)> ValidateSessionAsync()
    {
        if (!IsAuthenticated)
            return (false, "Not authenticated");

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            client.DefaultRequestHeaders.Add("Cookie", GetCookieHeader());
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

            // Лёгкий запрос к YouTube Music sw.js_data — возвращает visitor data
            var response = await client.GetAsync("https://music.youtube.com/sw.js_data");

            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                // Если тело содержит данные — сессия валидна
                if (body.Length > 10)
                {
                    Log.Info("[Auth] Session validated successfully");
                    return (true, null);
                }
            }

            // 401/403 — куки протухли
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized
                or System.Net.HttpStatusCode.Forbidden)
            {
                Log.Warn($"[Auth] Session invalid: HTTP {(int)response.StatusCode}");
                return (false, $"HTTP {(int)response.StatusCode}");
            }

            // Другие ошибки — оптимистично считаем что сессия ок (проблема сети?)
            Log.Warn($"[Auth] Validation ambiguous: HTTP {(int)response.StatusCode}");
            return (true, null);
        }
        catch (HttpRequestException ex)
        {
            // Нет сети — не паникуем, используем кэшированный профиль
            Log.Info($"[Auth] Network unavailable during validation: {ex.Message}");
            return (true, null); // Оптимистичный подход
        }
        catch (TaskCanceledException)
        {
            Log.Info("[Auth] Session validation timed out");
            return (true, null); // Оптимистичный подход
        }
        catch (Exception ex)
        {
            Log.Error($"[Auth] Validation error: {ex.Message}");
            return (true, null); // При сомнениях — не ломаем UX
        }
    }

    // --- Cookie Management ---

    private void LoadCookies()
    {
        if (File.Exists(G.FilePath.Cookie))
        {
            var raw = File.ReadAllText(G.FilePath.Cookie);
            ParseAndSetCookies(raw);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string GetCookieHeader()
    {
        lock (_lock) return _cachedHeaderString;
    }

    public string? GetCookieValue(string key)
    {
        lock (_lock)
            return _cookieMap.TryGetValue(key, out var val) ? val : null;
    }

    /// <summary>
    /// Собирает resurrection cookie header. Использует FrozenSet для O(1) проверки.
    /// </summary>
    public string GetResurrectionCookieHeader()
    {
        lock (_lock)
        {
            // Предварительная оценка размера
            int estimatedLen = 0;
            int matchCount = 0;
            foreach (var kvp in _cookieMap)
            {
                if (ResurrectionCookieNames.Contains(kvp.Key))
                {
                    estimatedLen += kvp.Key.Length + kvp.Value.Length + 3; // "key=value; "
                    matchCount++;
                }
            }

            if (matchCount == 0) return "";

            var sb = new StringBuilder(estimatedLen);
            foreach (var kvp in _cookieMap)
            {
                if (!ResurrectionCookieNames.Contains(kvp.Key)) continue;

                if (sb.Length > 0) sb.Append("; ");
                sb.Append(kvp.Key);
                sb.Append('=');
                sb.Append(kvp.Value);
            }
            return sb.ToString();
        }
    }

    public void SaveCookies(string cookies)
    {
        if (string.IsNullOrWhiteSpace(cookies)) return;

        // Span-based очистка
        var span = cookies.AsSpan().Trim().Trim('"');
        var clean = span.ToString().Replace("\r", "").Replace("\n", "");
        clean = FindCookieTextRegex().Replace(clean, "");

        if (!clean.Contains("SAPISID", StringComparison.Ordinal))
        {
            Log.Warn("[Auth] Attempt to save cookies without SAPISID. Ignoring.");
            return;
        }

        ParseAndSetCookies(clean);
        SaveCookiesToFile();

        UpdateStateAuthStatus();
        OnAuthStateChanged?.Invoke();

        Log.Info($"[Auth] Cookies saved manually. Total keys: {_cookieMap.Count}");
    }

    public bool UpdateCookies(IEnumerable<string> setCookieHeaders)
    {
        bool criticalCookieUpdated = false;
        lock (_lock)
        {
            foreach (var header in setCookieHeaders)
            {
                // Парсим первую пару key=value до ';'
                var headerSpan = header.AsSpan();
                int semicolonIdx = headerSpan.IndexOf(';');
                var firstPart = semicolonIdx >= 0 ? headerSpan[..semicolonIdx] : headerSpan;

                int equalIdx = firstPart.IndexOf('=');
                if (equalIdx <= 0) continue;

                var key = firstPart[..equalIdx].Trim().ToString();
                var value = firstPart[(equalIdx + 1)..].Trim().ToString();

                if (string.IsNullOrEmpty(value) ||
                    value.Equals("deleted", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!_cookieMap.TryGetValue(key, out var existingVal) || existingVal != value)
                {
                    _cookieMap[key] = value;

                    // Проверка через FrozenSet + Contains
                    if (!criticalCookieUpdated && IsCriticalCookie(key))
                        criticalCookieUpdated = true;

                    // Синхронизация PSIDTS
                    if (key == "__Secure-1PSIDTS")
                        _cookieMap["__Secure-3PSIDTS"] = value;
                }
            }

            if (criticalCookieUpdated || _cookieMap.Count > 0)
                RebuildHeaderString();
        }

        if (criticalCookieUpdated)
        {
            SaveCookiesToFile();
            UpdateStateAuthStatus();
        }

        return criticalCookieUpdated;
    }

    /// <summary>
    /// Проверяет, является ли cookie критическим (нужно сохранение).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsCriticalCookie(string key)
    {
        // Проверяем по точному совпадению и по Contains для составных имён
        foreach (var criticalName in CriticalCookieNames)
        {
            if (key.Contains(criticalName, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    public void Logout()
    {
        lock (_lock)
        {
            _cookieMap.Clear();
            _cachedHeaderString = "";
        }

        if (File.Exists(G.FilePath.Cookie)) File.Delete(G.FilePath.Cookie);

        State = new AuthState();
        if (File.Exists(_authDataPath)) File.Delete(_authDataPath);

        OnAuthStateChanged?.Invoke();
    }

    /// <summary>
    /// Парсит cookie строку без лишних аллокаций.
    /// Использует Span для поиска разделителей.
    /// </summary>
    private void ParseAndSetCookies(string raw)
    {
        lock (_lock)
        {
            _cookieMap.Clear();

            var remaining = raw.AsSpan();

            while (remaining.Length > 0)
            {
                // Ищем разделитель ';'
                int semicolonIdx = remaining.IndexOf(';');
                ReadOnlySpan<char> part;

                if (semicolonIdx >= 0)
                {
                    part = remaining[..semicolonIdx];
                    remaining = remaining[(semicolonIdx + 1)..];
                }
                else
                {
                    part = remaining;
                    remaining = [];
                }

                // Ищем '='
                int equalIdx = part.IndexOf('=');
                if (equalIdx <= 0) continue;

                var key = part[..equalIdx].Trim();
                var value = part[(equalIdx + 1)..].Trim();

                if (key.Length > 0)
                    _cookieMap[key.ToString()] = value.ToString();
            }

            RebuildHeaderString();
        }
    }

    /// <summary>
    /// Перестраивает строку cookie header с предварительным расчётом размера.
    /// </summary>
    private void RebuildHeaderString()
    {
        if (_cookieMap.Count == 0)
        {
            _cachedHeaderString = "";
            return;
        }

        // Оценка размера: средний cookie ~30 символов + разделители
        int estimatedLen = _cookieMap.Count * 40;
        var sb = new StringBuilder(estimatedLen);

        foreach (var kvp in _cookieMap)
        {
            if (sb.Length > 0) sb.Append("; ");
            sb.Append(kvp.Key);
            sb.Append('=');
            sb.Append(kvp.Value);
        }

        _cachedHeaderString = sb.ToString();
    }

    private void SaveCookiesToFile()
    {
        lock (_lock)
        {
            try
            {
                if (_cookieMap.ContainsKey("SAPISID"))
                    File.WriteAllText(G.FilePath.Cookie, _cachedHeaderString);
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