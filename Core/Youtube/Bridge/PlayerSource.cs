using LMP.Core.Youtube.Bridge.Common;

namespace LMP.Core.Youtube.Bridge;

/// <summary>
/// Представляет исходный код плеера YouTube.
/// </summary>
internal partial class PlayerSource(string content)
{
    /// <summary>
    /// Извлекает SignatureTimestamp (sts) из кода плеера.
    /// Переиспользует оптимизированный парсер регулярных выражений из YoutubeAstSolver.
    /// </summary>
    public CipherManifest? CipherManifest
    {
        get
        {
            try
            {
                var signatureTimestamp = YoutubeAstSolver.ExtractSts(content);
                if (signatureTimestamp == "1337")
                {
                    Log.Debug("[PlayerSource] SignatureTimestamp not found (fallback returned)");
                    return null;
                }

                return new CipherManifest(signatureTimestamp);
            }
            catch (Exception ex)
            {
                Log.Debug($"[PlayerSource] CipherManifest extraction failed: {ex.Message}");
                return null;
            }
        }
    }
}