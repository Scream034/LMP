using System.Diagnostics.CodeAnalysis;

namespace LMP.Core.Youtube.Bridge.Cipher;

internal class SpliceCipherOperation(int index) : ICipherOperation
{
    public string Decipher(string input) => input[index..];

    [ExcludeFromCodeCoverage]
    public override string ToString() => $"Splice ({index})";
}
