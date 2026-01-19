using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MyLiteMusicPlayer.Models;

namespace MyLiteMusicPlayer.Services;

public class GoogleAuthService : IDisposable
{
    private const string ClientId = "YOUR_CLIENT_ID.apps.googleusercontent.com";
    private const string ClientSecret = "YOUR_CLIENT_SECRET";
    private const string RedirectUri = "http://localhost:8765/callback";
    private const string Scope = "https://www.googleapis.com/auth/youtube.readonly email profile";
    
    private readonly string _tokenPath;
    private readonly HttpClient _http;
    private HttpListener? _listener;
    private CancellationTokenSource? _listenerCts;
    
    public AuthState State { get; private set; } = new();
    public bool IsAuthenticated => State.IsAuthenticated && State.TokenExpiry > DateTime.UtcNow;
    
    public event Action? OnAuthStateChanged;

    public GoogleAuthService()
    {
        _http = new HttpClient();
        
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string appFolder = Path.Combine(appData, "LiteMusicPlayer");
        Directory.CreateDirectory(appFolder);
        _tokenPath = Path.Combine(appFolder, "auth.json");
        
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
        catch
        {
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
            Console.WriteLine($"Failed to save auth tokens: {ex.Message}\n{ex.StackTrace}");
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
                $"client_id={Uri.EscapeDataString(ClientId)}&" +
                $"redirect_uri={Uri.EscapeDataString(RedirectUri)}&" +
                $"response_type=code&" +
                $"scope={Uri.EscapeDataString(Scope)}&" +
                $"state={state}&" +
                $"code_challenge={codeChallenge}&" +
                $"code_challenge_method=S256&" +
                $"access_type=offline&" +
                $"prompt=consent";
            
            _listenerCts = new CancellationTokenSource();
            _listener = new HttpListener();
            _listener.Prefixes.Add("http://localhost:8765/");
            _listener.Start();
            
            Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });
            
            var context = await _listener.GetContextAsync();
            var request = context.Request;
            var response = context.Response;
            
            string? code = request.QueryString["code"];
            string? returnedState = request.QueryString["state"];
            
            string responseHtml = code != null 
                ? "<html><body><h1>Authorization successful</h1></body></html>"
                : "<html><body><h1>Authorization failed</h1></body></html>";
            
            byte[] buffer = Encoding.UTF8.GetBytes(responseHtml);
            response.ContentType = "text/html; charset=utf-8";
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer);
            response.Close();
            
            _listener.Stop();
            _listener = null;
            
            if (code == null || returnedState != state)
            {
                return false;
            }
            
            return await ExchangeCodeAsync(code, codeVerifier);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Login error: {ex.Message}\n{ex.StackTrace}");
            return false;
        }
    }

    private async Task<bool> ExchangeCodeAsync(string code, string codeVerifier)
    {
        try
        {
            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["client_secret"] = ClientSecret,
                ["code"] = code,
                ["code_verifier"] = codeVerifier,
                ["grant_type"] = "authorization_code",
                ["redirect_uri"] = RedirectUri
            });
            
            var response = await _http.PostAsync("https://oauth2.googleapis.com/token", content);
            
            if (!response.IsSuccessStatusCode)
                return false;
            
            string json = await response.Content.ReadAsStringAsync();
            var tokenData = JsonSerializer.Deserialize<JsonElement>(json);
            
            State.AccessToken = tokenData.GetProperty("access_token").GetString();
            State.RefreshToken = tokenData.TryGetProperty("refresh_token", out var rt) 
                ? rt.GetString() 
                : State.RefreshToken;
            
            int expiresIn = tokenData.GetProperty("expires_in").GetInt32();
            State.TokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn);
            State.IsAuthenticated = true;
            
            await FetchUserInfoAsync();
            
            SaveTokens();
            OnAuthStateChanged?.Invoke();
            
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Token exchange error: {ex.Message}\n{ex.StackTrace}");
            return false;
        }
    }

    public async Task<bool> RefreshTokenAsync()
    {
        if (string.IsNullOrEmpty(State.RefreshToken))
            return false;
        
        try
        {
            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["client_secret"] = ClientSecret,
                ["refresh_token"] = State.RefreshToken,
                ["grant_type"] = "refresh_token"
            });
            
            var response = await _http.PostAsync("https://oauth2.googleapis.com/token", content);
            
            if (!response.IsSuccessStatusCode)
            {
                Logout();
                return false;
            }
            
            string json = await response.Content.ReadAsStringAsync();
            var tokenData = JsonSerializer.Deserialize<JsonElement>(json);
            
            State.AccessToken = tokenData.GetProperty("access_token").GetString();
            int expiresIn = tokenData.GetProperty("expires_in").GetInt32();
            State.TokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn);
            
            SaveTokens();
            OnAuthStateChanged?.Invoke();
            
            return true;
        }
        catch
        {
            Logout();
            return false;
        }
    }

    private async Task FetchUserInfoAsync()
    {
        try
        {
            _http.DefaultRequestHeaders.Authorization = 
                new AuthenticationHeaderValue("Bearer", State.AccessToken);
            
            var response = await _http.GetAsync("https://www.googleapis.com/oauth2/v2/userinfo");
            
            if (response.IsSuccessStatusCode)
            {
                string json = await response.Content.ReadAsStringAsync();
                var userInfo = JsonSerializer.Deserialize<JsonElement>(json);
                
                State.UserEmail = userInfo.TryGetProperty("email", out var email) 
                    ? email.GetString() : null;
                State.UserName = userInfo.TryGetProperty("name", out var name) 
                    ? name.GetString() : null;
                State.UserAvatarUrl = userInfo.TryGetProperty("picture", out var pic) 
                    ? pic.GetString() : null;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to fetch user info: {ex.Message}\n{ex.StackTrace}");
        }
    }

    public void Logout()
    {
        State = new AuthState();
        SaveTokens();
        OnAuthStateChanged?.Invoke();
    }

    public async Task<string?> GetValidAccessTokenAsync()
    {
        if (!State.IsAuthenticated)
            return null;
        
        if (State.TokenExpiry <= DateTime.UtcNow.AddMinutes(5))
        {
            if (!await RefreshTokenAsync())
                return null;
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
        _listenerCts?.Cancel();
        _listener?.Close();
        _http.Dispose();
        GC.SuppressFinalize(this);
    }
}