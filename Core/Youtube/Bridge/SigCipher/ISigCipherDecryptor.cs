namespace LMP.Core.Youtube.Bridge.SigCipher;

public interface ISigCipherDecryptor
{
    /// <summary>
    /// Расшифровывает подпись YouTube.
    /// </summary>
    ValueTask<string> DecipherAsync(string encryptedSignature, CancellationToken ct = default);
}