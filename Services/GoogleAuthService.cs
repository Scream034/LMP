using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MyLiteMusicPlayer.Models;

namespace MyLiteMusicPlayer.Services;

public class GoogleAuthService : IDisposable
{
    private readonly string _clientId;
    private readonly string _clientSecret;

    private const string RedirectUri = "http://127.0.0.1:8765/";
    private const string Scope = "https://www.googleapis.com/auth/youtube.readonly email profile openid";

    private readonly string _tokenPath;
    private readonly HttpClient _http;
    private HttpListener? _listener;

    public AuthState State { get; private set; } = new();
    public bool IsAuthenticated => State.IsAuthenticated && !string.IsNullOrEmpty(State.AccessToken);

    public event Action? OnAuthStateChanged;

    public GoogleAuthService()
    {
        _http = new HttpClient();

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string appFolder = Path.Combine(appData, "LiteMusicPlayer");
        Directory.CreateDirectory(appFolder);
        _tokenPath = Path.Combine(appFolder, "auth.json");

        var secrets = SecretsLoader.Load();
        _clientId = secrets.Google.ClientId;
        _clientSecret = secrets.Google.ClientSecret;

        LoadTokens();
    }

    private void LoadTokens()
    {
        try
        {
            if (File.Exists(_tokenPath))
            {
                string json = File.ReadAllText(_tokenPath);
                var state = JsonSerializer.Deserialize<AuthState>(json);
                if (state != null)
                {
                    State = state;
                    if (State.IsAuthenticated && State.TokenExpiry <= DateTime.UtcNow.AddMinutes(5))
                    {
                        _ = RefreshTokenAsync();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Error loading tokens: {ex.Message}");
            State = new AuthState();
        }
    }

    private void SaveTokens()
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(State, options);
            File.WriteAllText(_tokenPath, json);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to save auth tokens: {ex.Message}");
        }
    }

    public async Task<bool> StartLoginAsync()
    {
        try
        {
            string state = GenerateRandomString(32);
            string codeVerifier = GenerateRandomString(64);
            string codeChallenge = GenerateCodeChallenge(codeVerifier);

            string authUrl = $"https://accounts.google.com/o/oauth2/v2/auth?" +
                $"client_id={Uri.EscapeDataString(_clientId)}&" +
                $"redirect_uri={Uri.EscapeDataString(RedirectUri)}&" +
                $"response_type=code&" +
                $"scope={Uri.EscapeDataString(Scope)}&" +
                $"state={state}&" +
                $"code_challenge={codeChallenge}&" +
                $"code_challenge_method=S256&" +
                $"access_type=offline&" +
                $"prompt=consent";

            _listener = new HttpListener();
            _listener.Prefixes.Add(RedirectUri);
            _listener.Start();

            Log.Info($"[Auth] Listening on {RedirectUri}...");

            Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

            var context = await _listener.GetContextAsync();
            var request = context.Request;
            var response = context.Response;

            string? code = request.QueryString["code"];
            string? returnedState = request.QueryString["state"];
            string? error = request.QueryString["error"];

            string responseHtml = "<html><body style='background:#121212;color:white;font-family:sans-serif;text-align:center;padding-top:50px;'>" +
                                  (code != null
                                      ? "<h1 style='color:#1DB954;'>Login Successful!</h1><p>You can close this tab and return to the app.</p>"
                                      : $"<h1 style='color:#ff5555;'>Login Failed</h1><p>{error}</p>") +
                                  "<script>setTimeout(function(){window.close()}, 2000);</script></body></html>";

            byte[] buffer = Encoding.UTF8.GetBytes(responseHtml);
            response.ContentType = "text/html; charset=utf-8";
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer);
            response.Close();

            _listener.Stop();

            if (string.IsNullOrEmpty(code) || returnedState != state)
            {
                Log.Error($"[Auth] OAuth error: {error} or State mismatch");
                return false;
            }

            return await ExchangeCodeAsync(code, codeVerifier);
        }
        catch (Exception ex)
        {
            Log.Error($"[Auth] Login critical error: {ex.Message}");
            _listener?.Stop();
            return false;
        }
    }

    private async Task<bool> ExchangeCodeAsync(string code, string codeVerifier)
    {
        try
        {
            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _clientId,
                ["client_secret"] = _clientSecret,
                ["code"] = code,
                ["code_verifier"] = codeVerifier,
                ["grant_type"] = "authorization_code",
                ["redirect_uri"] = RedirectUri
            });

            var response = await _http.PostAsync("https://oauth2.googleapis.com/token", content);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                Log.Error($"[Auth] Token exchange failed: {error}");
                return false;
            }

            string json = await response.Content.ReadAsStringAsync();
            var tokenData = JsonSerializer.Deserialize<JsonElement>(json);

            State.AccessToken = tokenData.GetProperty("access_token").GetString();

            if (tokenData.TryGetProperty("refresh_token", out var rt))
            {
                State.RefreshToken = rt.GetString();
            }

            int expiresIn = tokenData.GetProperty("expires_in").GetInt32();
            State.TokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn - 60);
            State.IsAuthenticated = true;

            // Загружаем профиль и YouTube канал параллельно
            await Task.WhenAll(
                FetchUserProfileAsync(),
                FetchYouTubeChannelAsync()
            );

            SaveTokens();
            OnAuthStateChanged?.Invoke();

            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"[Auth] ExchangeCodeAsync error: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> RefreshTokenAsync()
    {
        if (string.IsNullOrEmpty(State.RefreshToken))
        {
            Log.Warn("[Auth] No refresh token available.");
            return false;
        }

        try
        {
            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _clientId,
                ["client_secret"] = _clientSecret,
                ["refresh_token"] = State.RefreshToken,
                ["grant_type"] = "refresh_token"
            });

            var response = await _http.PostAsync("https://oauth2.googleapis.com/token", content);

            if (!response.IsSuccessStatusCode)
            {
                Log.Warn("[Auth] Failed to refresh token. Might be revoked.");
                Logout();
                return false;
            }

            string json = await response.Content.ReadAsStringAsync();
            var tokenData = JsonSerializer.Deserialize<JsonElement>(json);

            State.AccessToken = tokenData.GetProperty("access_token").GetString();
            int expiresIn = tokenData.GetProperty("expires_in").GetInt32();
            State.TokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn - 60);
            State.IsAuthenticated = true;

            SaveTokens();
            OnAuthStateChanged?.Invoke();

            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"[Auth] RefreshTokenAsync error: {ex.Message}");
            Logout();
            return false;
        }
    }

    /// <summary>
    /// Загружает базовый профиль Google (email, имя)
    /// </summary>
    private async Task FetchUserProfileAsync()
    {
        try
        {
            if (string.IsNullOrEmpty(State.AccessToken)) return;

            var request = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/oauth2/v2/userinfo");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", State.AccessToken);

            var response = await _http.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                string json = await response.Content.ReadAsStringAsync();
                var userInfo = JsonSerializer.Deserialize<JsonElement>(json);

                if (userInfo.TryGetProperty("email", out var email))
                    State.UserEmail = email.GetString();
                if (userInfo.TryGetProperty("name", out var name))
                    State.UserName = name.GetString();

                Log.Info($"[Auth] Google profile loaded: {State.UserName}");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[Auth] Failed to fetch user profile: {ex.Message}");
        }
    }

    /// <summary>
    /// Загружает YouTube канал пользователя (ID, название, аватар)
    /// </summary>
    private async Task FetchYouTubeChannelAsync()
    {
        try
        {
            if (string.IsNullOrEmpty(State.AccessToken)) return;

            var request = new HttpRequestMessage(HttpMethod.Get,
                "https://www.googleapis.com/youtube/v3/channels?part=snippet&mine=true");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", State.AccessToken);

            var response = await _http.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                Log.Warn("[Auth] Failed to fetch YouTube channel");
                return;
            }

            string json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("items", out var items) &&
                items.GetArrayLength() > 0)
            {
                var channel = items[0];

                if (channel.TryGetProperty("id", out var id))
                    State.YouTubeChannelId = id.GetString();

                if (channel.TryGetProperty("snippet", out var snippet))
                {
                    if (snippet.TryGetProperty("title", out var title))
                        State.YouTubeChannelName = title.GetString();

                    // Берём лучший аватар
                    if (snippet.TryGetProperty("thumbnails", out var thumbs))
                    {
                        State.YouTubeAvatarUrl = GetBestThumbnail(thumbs);
                    }
                }

                Log.Info($"[Auth] YouTube channel loaded: {State.YouTubeChannelName}");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[Auth] Failed to fetch YouTube channel: {ex.Message}");
        }
    }

    private static string? GetBestThumbnail(JsonElement thumbnails)
    {
        // Приоритет: high -> medium -> default
        if (thumbnails.TryGetProperty("high", out var high) &&
            high.TryGetProperty("url", out var highUrl))
            return highUrl.GetString();

        if (thumbnails.TryGetProperty("medium", out var medium) &&
            medium.TryGetProperty("url", out var medUrl))
            return medUrl.GetString();

        if (thumbnails.TryGetProperty("default", out var def) &&
            def.TryGetProperty("url", out var defUrl))
            return defUrl.GetString();

        return null;
    }

    public void Logout()
    {
        State = new AuthState();
        if (File.Exists(_tokenPath))
        {
            File.Delete(_tokenPath);
        }
        OnAuthStateChanged?.Invoke();
    }

    public async Task<string?> GetValidAccessTokenAsync()
    {
        if (!State.IsAuthenticated) return null;

        if (State.TokenExpiry <= DateTime.UtcNow)
        {
            bool refreshed = await RefreshTokenAsync();
            if (!refreshed) return null;
        }

        return State.AccessToken;
    }

    private static string GenerateRandomString(int length)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";
        var random = RandomNumberGenerator.Create();
        var bytes = new byte[length];
        random.GetBytes(bytes);

        var result = new char[length];
        for (int i = 0; i < length; i++)
        {
            result[i] = chars[bytes[i] % chars.Length];
        }

        return new string(result);
    }

    private static string GenerateCodeChallenge(string codeVerifier)
    {
        using var sha256 = SHA256.Create();
        var bytes = sha256.ComputeHash(Encoding.ASCII.GetBytes(codeVerifier));
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public void Dispose()
    {
        _listener?.Close();
        _http.Dispose();
        GC.SuppressFinalize(this);
    }
}