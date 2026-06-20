using System.Net;

namespace LMP.Core.Services;

/// <summary>
/// Долгоживущий HTTP-сервер (синглтон) для приёма куков из браузерного расширения.
/// Listener запускается лениво при первом вызове <see cref="WaitForCookiesAsync"/>
/// и живёт до <see cref="Dispose"/>.
/// Множественные вызовы <see cref="WaitForCookiesAsync"/> безопасно заменяют друг друга.
/// </summary>
public sealed class LocalAuthServer : IDisposable
{
    private const int Port = 40340;
    private static readonly byte[] PongResponse = "pong"u8.ToArray();
    private static string Prefix => $"http://127.0.0.1:{Port}/api/";

    private static LocalAuthServer? _createdInstance;

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _serverCts = new();
    private readonly Lock _lock = new();

    /// <summary>Ожидающий результата вызов WaitForCookiesAsync.</summary>
    private TaskCompletionSource<string?>? _pendingTcs;

    private bool _isStarted;
    private bool _isDisposed;

    /// <summary>
    /// Инициализирует объект сервера без запуска listener.
    /// Фактический старт происходит лениво при первом ожидании куков.
    /// </summary>
    public LocalAuthServer()
    {
        _listener.Prefixes.Add(Prefix);
        _createdInstance = this;
    }

    /// <summary>
    /// Освобождает ранее созданный экземпляр сервера, если он уже был инициализирован.
    /// Не инициирует создание сервиса через DI.
    /// </summary>
    public static void DisposeIfCreated()
    {
        _createdInstance?.Dispose();
    }

    /// <summary>
    /// Ожидает куки от расширения. При повторном вызове предыдущее ожидание снимается.
    /// Отмена <paramref name="ct"/> снимает ожидание без остановки сервера.
    /// </summary>
    /// <param name="ct">Токен отмены ожидания.</param>
    /// <returns>Строка куков или <c>null</c>, если ожидание было снято.</returns>
    public Task<string?> WaitForCookiesAsync(CancellationToken ct)
    {
        if (!EnsureStarted())
            return Task.FromResult<string?>(null);

        TaskCompletionSource<string?> tcs;

        lock (_lock)
        {
            if (_isDisposed)
                return Task.FromResult<string?>(null);

            _pendingTcs?.TrySetResult(null);

            tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingTcs = tcs;
        }

        return WaitForCookiesCoreAsync(tcs, ct);
    }

    /// <summary>
    /// Лениво запускает listener и фоновый цикл при первом реальном использовании.
    /// </summary>
    /// <returns><c>true</c>, если сервер запущен или уже был запущен.</returns>
    private bool EnsureStarted()
    {
        lock (_lock)
        {
            if (_isDisposed)
                return false;

            if (_isStarted)
                return true;

            try
            {
                _listener.Start();
                _isStarted = true;
                Log.Debug($"[LocalAuthServer] Listening on {Prefix}");
            }
            catch (HttpListenerException ex)
            {
                Log.Error($"[LocalAuthServer] Failed to start (port busy?): {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                Log.Error($"[LocalAuthServer] Unexpected start error: {ex.Message}");
                return false;
            }
        }

        _ = RunLoopAsync(_serverCts.Token);
        return true;
    }

    /// <summary>
    /// Привязывает пользовательский токен отмены к конкретному ожиданию куков.
    /// </summary>
    private async Task<string?> WaitForCookiesCoreAsync(TaskCompletionSource<string?> tcs, CancellationToken ct)
    {
        using var registration = ct.Register(static state =>
        {
            var waiter = (CookieWaiterState)state!;
            waiter.Server.CancelPendingWait(waiter.Tcs);
        }, new CookieWaiterState(this, tcs));

        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Снимает ожидание куков для указанного waiter, если он всё ещё актуален.
    /// </summary>
    private void CancelPendingWait(TaskCompletionSource<string?> tcs)
    {
        lock (_lock)
        {
            if (_pendingTcs == tcs)
            {
                _pendingTcs = null;
                tcs.TrySetResult(null);
            }
        }
    }

    /// <summary>
    /// Основной цикл обработки HTTP-запросов. Работает до отмены <see cref="_serverCts"/>.
    /// </summary>
    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (HttpListenerException ex)
            {
                Log.Warn($"[LocalAuthServer] GetContext error: {ex.Message}");
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            _ = HandleContextAsync(context, ct);
        }

        Log.Debug("[LocalAuthServer] Loop stopped.");
    }

    /// <summary>
    /// Обрабатывает один HTTP-запрос.
    /// </summary>
    private async Task HandleContextAsync(HttpListenerContext context, CancellationToken ct)
    {
        var request = context.Request;
        var response = context.Response;

        try
        {
            // CORS
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, X-Extension-Version");

            var path = request.Url?.AbsolutePath ?? "";

            if (request.HttpMethod == "OPTIONS")
            {
                response.StatusCode = 204;
                return;
            }

            if (request.HttpMethod == "GET" &&
                path.Equals("/api/ping", StringComparison.OrdinalIgnoreCase))
            {
                response.StatusCode = 200;
                response.ContentType = "text/plain";
                response.ContentLength64 = PongResponse.Length;
                await response.OutputStream.WriteAsync(PongResponse, ct).ConfigureAwait(false);
                return;
            }

            if (request.HttpMethod == "POST" &&
                path.StartsWith("/api/auth", StringComparison.OrdinalIgnoreCase))
            {
                using var reader = new StreamReader(request.InputStream);
                var cookies = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

                response.StatusCode = 200;

                lock (_lock)
                {
                    if (_pendingTcs != null)
                    {
                        _pendingTcs.TrySetResult(cookies);
                        _pendingTcs = null;
                    }
                    else
                    {
                        Log.Warn("[LocalAuthServer] Received cookies but no one is waiting.");
                    }
                }

                return;
            }

            response.StatusCode = 405;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"[LocalAuthServer] HandleContext error: {ex.Message}");
            try { response.StatusCode = 500; } catch { }
        }
        finally
        {
            try { response.Close(); } catch { }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
        }

        _serverCts.Cancel();

        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }

        lock (_lock)
        {
            _pendingTcs?.TrySetResult(null);
            _pendingTcs = null;
        }

        _serverCts.Dispose();
        Log.Debug("[LocalAuthServer] Disposed.");
    }

    /// <summary>
    /// Структура без аллокаций для передачи состояния отмены.
    /// </summary>
    private readonly record struct CookieWaiterState(
        LocalAuthServer Server,
        TaskCompletionSource<string?> Tcs);
}