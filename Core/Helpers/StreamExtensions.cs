namespace LMP.Core.Helpers;

/// <summary>
/// Extension методы для работы с потоками
/// </summary>
public static class StreamExtensions
{
    /// <summary>
    /// Читает ровно указанное количество байт или выбрасывает исключение
    /// </summary>
    public static async ValueTask ReadExactlyAsync(
        this Stream stream, 
        Memory<byte> buffer, 
        CancellationToken ct = default)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[totalRead..], ct);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    $"Expected {buffer.Length} bytes, got {totalRead}");
            }
            totalRead += read;
        }
    }
    
    /// <summary>
    /// Создаёт HttpClient оптимизированный для стриминга
    /// </summary>
    public static HttpClient CreateStreamingClient(TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            // Отключаем буферизацию для стриминга
            MaxResponseHeadersLength = 64,
            // Поддержка keep-alive
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            // Автоматическая декомпрессия
            AutomaticDecompression = System.Net.DecompressionMethods.All
        };
        
        return new HttpClient(handler)
        {
            Timeout = timeout,
            DefaultRequestHeaders =
            {
                { "User-Agent", "Mozilla/5.0 (compatible; LMP/1.0)" }
            }
        };
    }
}