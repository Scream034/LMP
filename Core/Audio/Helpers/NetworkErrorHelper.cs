using System.Security.Authentication;
using LMP.Core.Exceptions;

namespace LMP.Core.Helpers;

/// <summary>
/// Helper for analyzing and classifying network and cancellation exceptions.
/// Shared between audio pipelines, HTTP handlers, and UI error orchestrators.
/// </summary>
public static class NetworkErrorHelper
{
    /// <summary>
    /// Checks if the exception is a transient user cancellation or a genuine network drop.
    /// </summary>
    /// <param name="exception">Exception to evaluate.</param>
    /// <returns><c>true</c> if it is a pure cancellation; otherwise <c>false</c> if it is a network error.</returns>
    public static bool IsCancellationLike(Exception? exception)
    {
        if (exception is ChunkDownloadFatalException) return false;

        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is TimeoutException or 
                IOException or 
                System.Net.Sockets.SocketException or 
                System.Net.WebException or
                HttpRequestException)
            {
                return false;
            }
        }

        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is OperationCanceledException or TaskCanceledException)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Checks if the exception is caused by an SSL/TLS handshake failure or a connection reset (common in DPI/firewall blocking).
    /// </summary>
    /// <param name="exception">Exception to evaluate.</param>
    /// <returns><c>true</c> if DPI/handshake/timeout block is detected; otherwise <c>false</c>.</returns>
    public static bool IsSslOrTlsHandshakeFailure(Exception? exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is AuthenticationException or TimeoutException)
                return true;

            if (current is IOException ioEx)
            {
                var msg = ioEx.Message;
                if (msg.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase) ||
                    msg.Contains("handshake failed", StringComparison.OrdinalIgnoreCase) ||
                    msg.Contains("closed the transport stream", StringComparison.OrdinalIgnoreCase) ||
                    msg.Contains("unexpected packet format", StringComparison.OrdinalIgnoreCase) ||
                    msg.Contains("decryption operation failed", StringComparison.OrdinalIgnoreCase) ||
                    msg.Contains("Connection reset", StringComparison.OrdinalIgnoreCase) ||
                    msg.Contains("Connection timed out", StringComparison.OrdinalIgnoreCase) ||
                    msg.Contains("Response ended prematurely", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            if (current is System.Net.Sockets.SocketException sockEx)
            {
                if (sockEx.SocketErrorCode is 
                    System.Net.Sockets.SocketError.ConnectionReset or 
                    System.Net.Sockets.SocketError.TimedOut or 
                    System.Net.Sockets.SocketError.ConnectionAborted or 
                    System.Net.Sockets.SocketError.ConnectionRefused)
                {
                    return true;
                }
            }

            if (current is System.Net.WebException webEx &&
                webEx.Status == System.Net.WebExceptionStatus.SecureChannelFailure)
            {
                return true;
            }
        }
        return false;
    }
}