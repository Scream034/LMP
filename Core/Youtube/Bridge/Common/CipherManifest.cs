namespace LMP.Core.Youtube.Bridge.Common;

/// <summary>
/// Облегченный манифест плеера, содержащий временную метку подписи (STS).
/// </summary>
internal sealed class CipherManifest(string signatureTimestamp)
{
    /// <summary>Временная метка подписи (sts/signatureTimestamp), необходимая для обхода блокировок.</summary>
    public string SignatureTimestamp { get; } = signatureTimestamp;
}