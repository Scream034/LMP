using System.IO;
using System.Net;

namespace LMP.Core.Services;

/// <summary>
/// Легковесный одноразовый сервер для приема куков из браузерного расширения.
/// Работает строго на 127.0.0.1 и включает поддержку CORS.
/// </summary>
public sealed class LocalAuthServer
{
    private const int Port = 40340;
    private static string Prefix => $"http://127.0.0.1:{Port}/api/auth/";

    /// <summary>
    /// Ожидает POST запрос с куками от расширения.
    /// </summary>
    public async Task<string?> WaitForCookiesAsync(CancellationToken ct)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add(Prefix);

        try
        {
            listener.Start();
            Log.Debug($"[LocalAuthServer] Listening on {Prefix}");

            using var reg = ct.Register(() => 
            {
                if (listener.IsListening) listener.Stop();
            });

            while (!ct.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync().ConfigureAwait(false);
                var request = context.Request;
                var response = context.Response;

                // CORS — обязательно для запросов из расширений браузера
                response.Headers.Add("Access-Control-Allow-Origin", "*");
                response.Headers.Add("Access-Control-Allow-Methods", "POST, OPTIONS");
                response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

                if (request.HttpMethod == "OPTIONS")
                {
                    response.StatusCode = 204;
                    response.Close();
                    continue;
                }

                if (request.HttpMethod == "POST")
                {
                    using var reader = new StreamReader(request.InputStream);
                    var cookies = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

                    response.StatusCode = 200;
                    response.Close();
                    
                    return cookies;
                }

                response.StatusCode = 405;
                response.Close();
            }
        }
        catch (HttpListenerException)
        {
            // Игнорируем ошибку при принудительной остановке листенера (отмена юзером)
        }
        catch (Exception ex)
        {
            Log.Error($"[LocalAuthServer] Error: {ex.Message}");
        }
        finally
        {
            if (listener.IsListening) listener.Stop();
        }

        return null;
    }
}