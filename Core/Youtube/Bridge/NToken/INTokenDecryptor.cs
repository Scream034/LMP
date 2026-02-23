namespace LMP.Core.Youtube.Bridge.NToken;

public interface INTokenDecryptor
{
    /// <summary>
    /// Расшифровывает n-parameter.
    /// </summary>
    ValueTask<string> DecryptAsync(string nToken, CancellationToken ct = default);
}