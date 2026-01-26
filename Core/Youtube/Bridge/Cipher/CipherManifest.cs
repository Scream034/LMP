namespace LMP.Core.Youtube.Bridge.Cipher;

internal class CipherManifest(string signatureTimestamp, IReadOnlyList<ICipherOperation> operations)
{
    public string SignatureTimestamp { get; } = signatureTimestamp;

    public IReadOnlyList<ICipherOperation> Operations { get; } = operations;

    public string Decipher(string input) =>
        Operations.Aggregate(input, static (acc, op) => op.Decipher(acc));
}
