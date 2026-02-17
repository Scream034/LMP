namespace LMP.Core.Helpers;

/// <summary>
/// HTTP handler для логирования всех запросов в DEBUG.
/// </summary>
public sealed class LoggingHandler(HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
#if DEBUG && HTTP_LOG
        var sw = Stopwatch.StartNew();
        
        Log.Debug($"[HTTP] → {request.Method} {request.RequestUri}");
        
        if (request.Headers.Range != null)
            Log.Debug($"[HTTP]   Range: {request.Headers.Range}");
#endif

        HttpResponseMessage response;
#if DEBUG && HTTP_LOG
        try
        {
            response = await base.SendAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            Log.Warn($"[HTTP] ✗ {request.Method} {request.RequestUri} - {ex.Message}");
            throw;
        }
#else
        try
        {
            response = await base.SendAsync(request, cancellationToken);
        }
        catch (Exception)
        {
            throw;
        }
#endif

#if DEBUG && HTTP_LOG
        sw.Stop();
        
        var status = (int)response.StatusCode;
        var size = response.Content.Headers.ContentLength;
        var sizeStr = size.HasValue ? $"{size.Value / 1024}KB" : "?";
        
        var logLevel = status >= 400 ? "Warn" : "Trace";
        var symbol = status >= 400 ? "✗" : "✓";
        
        Log.Debug($"[HTTP] {symbol} {status} {request.RequestUri?.AbsolutePath} ({sizeStr}, {sw.ElapsedMilliseconds}ms)");
        
        // Для m3u8 логируем полный URL
        if (request.RequestUri?.AbsolutePath.Contains("m3u8") == true)
        {
            Log.Debug($"[HTTP] M3U8 Full URL: {request.RequestUri}");
        }
#endif

        return response;
    }
}