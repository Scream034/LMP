using System.Diagnostics.CodeAnalysis;
using LMP.Core.Youtube.Utils.Extensions;

namespace LMP.Core.Youtube.Bridge.Cipher;

internal class ReverseCipherOperation : ICipherOperation
{
    public string Decipher(string input) => input.Reverse();

    [ExcludeFromCodeCoverage]
    public override string ToString() => "Reverse";
}
