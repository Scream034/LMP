using System.Globalization;
using System.Text.RegularExpressions;
using LMP.Core.Youtube.Bridge.Cipher;
using LMP.Core.Youtube.Utils.Extensions;

namespace LMP.Core.Youtube.Bridge;

internal partial class PlayerSource(string content)
{
    public CipherManifest? CipherManifest
    {
        get
        {
            // Extract the signature timestamp
            var signatureTimestamp = MyRegex().Match(content)
                .Groups[1]
                .Value.NullIfWhiteSpace();

            if (string.IsNullOrWhiteSpace(signatureTimestamp))
                return null;

            // Find where the player calls the cipher functions
            var cipherCallsite = MyRegex1().Match(content)
                .Groups[0]
                .Value.NullIfWhiteSpace();

            if (string.IsNullOrWhiteSpace(cipherCallsite))
                return null;

            // Find the object that defines the cipher functions
            var cipherContainerName = MyRegex2().Match(cipherCallsite)
                .Groups[1]
                .Value;

            if (string.IsNullOrWhiteSpace(cipherContainerName))
                return null;

            // Find the definition of the cipher functions
            // Dynamic regex based on extracted container name - cannot use GeneratedRegex for the full pattern
            var cipherDefinition = Regex
                .Match(
                    content,
                    $$"""
                    var {{Regex.Escape(cipherContainerName)}}={.*?};
                    """,
                    RegexOptions.Singleline
                )
                .Groups[0]
                .Value.NullIfWhiteSpace();

            if (string.IsNullOrWhiteSpace(cipherDefinition))
                return null;

            // Identify the swap cipher function
            var swapFuncName = MyRegex3().Match(cipherDefinition)
                .Groups[1]
                .Value.NullIfWhiteSpace();

            // Identify the splice cipher function
            var spliceFuncName = SpliceFuncRegex().Match(cipherDefinition)
                .Groups[1]
                .Value.NullIfWhiteSpace();

            // Identify the reverse cipher function
            var reverseFuncName = ReverseFuncRegex().Match(cipherDefinition)
                .Groups[1]
                .Value.NullIfWhiteSpace();

            var operations = new List<ICipherOperation>();
            foreach (var statement in cipherCallsite.Split(';'))
            {
                var calledFuncName = CalledFuncRegex().Match(statement)
                    .Groups[1]
                    .Value;

                if (string.IsNullOrWhiteSpace(calledFuncName))
                    continue;

                if (string.Equals(calledFuncName, swapFuncName, StringComparison.Ordinal))
                {
                    var index = IndexRegex().Match(statement)
                        .Groups[1]
                        .Value.Pipe(s => int.Parse(s, CultureInfo.InvariantCulture));

                    operations.Add(new SwapCipherOperation(index));
                }
                else if (string.Equals(calledFuncName, spliceFuncName, StringComparison.Ordinal))
                {
                    var index = IndexRegex().Match(statement)
                        .Groups[1]
                        .Value.Pipe(s => int.Parse(s, CultureInfo.InvariantCulture));

                    operations.Add(new SpliceCipherOperation(index));
                }
                else if (string.Equals(calledFuncName, reverseFuncName, StringComparison.Ordinal))
                {
                    operations.Add(new ReverseCipherOperation());
                }
            }

            return new CipherManifest(signatureTimestamp, operations);
        }
    }

    [GeneratedRegex(@"(?:signatureTimestamp|sts):(\d{5})")]
    private static partial Regex MyRegex();

    [GeneratedRegex("""
                    [$_\w]+=function\([$_\w]+\){([$_\w]+)=\1\.split\(['"]{2}\);.*?return \1\.join\(['"]{2}\)}
                    """, RegexOptions.Singleline
    )]
    private static partial Regex MyRegex1();

    [GeneratedRegex(@"([$_\w]+)\.[$_\w]+\([$_\w]+,\d+\);")]
    private static partial Regex MyRegex2();

    [GeneratedRegex(@"([$_\w]+):function\([$_\w]+,[$_\w]+\){+[^}]*?%[^}]*?}", RegexOptions.Singleline
    )]
    private static partial Regex MyRegex3();

    // New generated regexes replacing static Regex.Match

    [GeneratedRegex(@"([$_\w]+):function\([$_\w]+,[$_\w]+\){+[^}]*?splice[^}]*?}", RegexOptions.Singleline)]
    private static partial Regex SpliceFuncRegex();

    [GeneratedRegex(@"([$_\w]+):function\([$_\w]+\){+[^}]*?reverse[^}]*?}", RegexOptions.Singleline)]
    private static partial Regex ReverseFuncRegex();

    [GeneratedRegex(@"[$_\w]+\.([$_\w]+)\([$_\w]+,\d+\)")]
    private static partial Regex CalledFuncRegex();

    [GeneratedRegex(@"\([$_\w]+,(\d+)\)")]
    private static partial Regex IndexRegex();
}

internal partial class PlayerSource
{
    public static PlayerSource Parse(string raw) => new(raw);
}