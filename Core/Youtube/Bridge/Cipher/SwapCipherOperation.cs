using System.Diagnostics.CodeAnalysis;
using LMP.Core.Youtube.Utils.Extensions;

namespace LMP.Core.Youtube.Bridge.Cipher;

internal class SwapCipherOperation(int index) : ICipherOperation
{
    public string Decipher(string input) => input.SwapChars(0, index);

    [ExcludeFromCodeCoverage]
    public override string ToString() => $"Swap ({index})";
}
