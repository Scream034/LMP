using System.Text.RegularExpressions;
using LMP.Core.Youtube.Bridge.Common;

namespace LMP.Core.Youtube.Bridge;

/// <summary>
/// Представляет исходный код плеера YouTube.
/// </summary>
internal partial class PlayerSource(string content)
{
    /// <summary>
    /// Извлекает SignatureTimestamp (sts) из кода плеера.
    /// Сама дешифрация подписи теперь полностью делегирована SigCipherDecryptor.
    /// </summary>
    public CipherManifest? CipherManifest
    {
        get
        {
            try
            {
                var stsMatch = SignatureTimestampRegex().Match(content);
                if (!stsMatch.Success)
                {
                    Log.Debug("[PlayerSource] SignatureTimestamp not found");
                    return null;
                }

                var signatureTimestamp = stsMatch.Groups[1].Value;
                return new CipherManifest(signatureTimestamp);
            }
            catch (Exception ex)
            {
                Log.Debug($"[PlayerSource] CipherManifest extraction failed: {ex.Message}");
                return null;
            }
        }
    }

    [GeneratedRegex(@"(?:signatureTimestamp|sts)\s*[:=]\s*(\d+)", RegexOptions.Compiled)]
    private static partial Regex SignatureTimestampRegex();

    /// <summary>
    /// Парсит исходный код плеера.
    /// </summary>
    public static PlayerSource Parse(string raw) => new(raw);
}