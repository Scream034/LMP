using System.Diagnostics;
using Acornima;
using Acornima.Ast;

namespace LMP.Core.Youtube.Bridge.SigCipher;

/// <summary>
/// Обеспечивает высокопроизводительное статическое извлечение манифеста SigCipher на основе рекурсивного AST-анализа Acornima.
/// Полностью исключает необходимость выполнения JavaScript в песочнице.
/// </summary>
internal static partial class SigCipherExtractor
{
    /// <summary>
    /// Извлекает манифест операций SigCipher без выполнения JavaScript.
    /// </summary>
    public static SigCipherManifest? ExtractManifest(string baseJs, string playerVersion)
    {
        try
        {
            var sw = Stopwatch.StartNew();

            var parser = new Parser();
            var program = parser.ParseScript(baseJs);

            var (decipherFunc, cipherObj) = FindDecipherStructures(program, baseJs);
            if (decipherFunc is null || cipherObj is null)
            {
                Log.Debug("[SigExtractor] Decipher structures not found in AST.");
                return null;
            }

            var methodMap = ParseCipherObjectMethods(cipherObj, baseJs);
            if (methodMap.Count == 0)
            {
                Log.Debug("[SigExtractor] No valid operations found on cipher object.");
                return null;
            }

            var operations = ExtractOperationsFromDecipherBody(decipherFunc, methodMap);
            if (operations.Count < 3)
            {
                Log.Debug($"[SigExtractor] Extracted too few operations: {operations.Count}");
                return null;
            }

            sw.Stop();
            var manifest = new SigCipherManifest(playerVersion, operations, "extracted_ast");
            Log.Info($"[SigCipherExtractor] Successfully extracted AST manifest in {sw.ElapsedMilliseconds}ms: {manifest}");
            return manifest;
        }
        catch (Exception ex)
        {
            Log.Error($"[SigCipherExtractor] AST extraction failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Находит имя функции расшифровки в base.js.
    /// </summary>
    public static string? FindDecipherFunctionName(string baseJs)
    {
        try
        {
            var parser = new Parser();
            var program = parser.ParseScript(baseJs);
            var (decipherFunc, _) = FindDecipherStructures(program, baseJs);

            if (decipherFunc is FunctionDeclaration fd)
                return fd.Id?.Name;

            if (decipherFunc is FunctionExpression)
            {
                foreach (var node in program.Body)
                {
                    if (node is ExpressionStatement es && es.Expression is AssignmentExpression ae 
                        && ae.Right == decipherFunc && ae.Left is Identifier id)
                    {
                        return id.Name;
                    }
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static (Node? DecipherFunc, ObjectExpression? CipherObj) FindDecipherStructures(Script program, string baseJs)
    {
        var targetFunc = FindDecipherFunctionInNode(program, baseJs);
        if (targetFunc is null) return (null, null);

        BlockStatement? body = null;
        if (targetFunc is FunctionDeclaration fd) body = fd.Body;
        else if (targetFunc is FunctionExpression fe) body = fe.Body;

        var cipherObjName = ExtractCipherObjectName(body);
        if (cipherObjName is null) return (targetFunc, null);

        var targetObj = FindCipherObjectInNode(program, cipherObjName);
        return (targetFunc, targetObj);
    }

    private static Node? FindDecipherFunctionInNode(Node? node, string baseJs)
    {
        if (node is null) return null;

        if (node is FunctionDeclaration fd && IsDecipherFunction(fd.Body, baseJs))
            return fd;

        if (node is FunctionExpression fe && IsDecipherFunction(fe.Body, baseJs))
            return fe;

        foreach (var child in node.ChildNodes)
        {
            var result = FindDecipherFunctionInNode(child, baseJs);
            if (result is not null) return result;
        }

        return null;
    }

    private static ObjectExpression? FindCipherObjectInNode(Node? node, string name)
    {
        if (node is null) return null;

        if (node is VariableDeclarator vd && vd.Id is Identifier id && id.Name == name && vd.Init is ObjectExpression oe)
            return oe;

        if (node is AssignmentExpression ae && ae.Left is Identifier id2 && id2.Name == name && ae.Right is ObjectExpression oe2)
            return oe2;

        foreach (var child in node.ChildNodes)
        {
            var result = FindCipherObjectInNode(child, name);
            if (result is not null) return result;
        }

        return null;
    }

    private static bool IsDecipherFunction(BlockStatement? body, string baseJs)
    {
        if (body is null || body.Body.Count < 3) return false;

        var bodyStr = baseJs.Substring(body.Start, body.End - body.Start);
        
        bool hasSplit = bodyStr.Contains(".split(") || bodyStr.Contains("[\"split\"]");
        bool hasJoin = bodyStr.Contains(".join(") || bodyStr.Contains("[\"join\"]");
        
        bool hasReverse = bodyStr.Contains(".reverse") || bodyStr.Contains("[\"reverse\"]") || bodyStr.Contains(".reverse(");
        bool hasSplice = bodyStr.Contains(".splice") || bodyStr.Contains("[\"splice\"]") || bodyStr.Contains(".splice(");
        
        return hasSplit && hasJoin && (hasReverse || hasSplice);
    }

    private static string? ExtractCipherObjectName(BlockStatement? body)
    {
        if (body is null) return null;
        return FindFirstCipherObjectNameInNode(body);
    }

    private static string? FindFirstCipherObjectNameInNode(Node? node)
    {
        if (node is null) return null;

        if (node is CallExpression ce)
        {
            var callee = ce.Callee;
            while (callee is ParenthesizedExpression pe)
            {
                callee = pe.Expression;
            }
            if (callee is MemberExpression me && me.Object is Identifier id && id.Name != "a" && id.Name != "window" && id.Name != "self" && id.Name != "globalThis")
            {
                return id.Name;
            }
        }

        foreach (var child in node.ChildNodes)
        {
            var result = FindFirstCipherObjectNameInNode(child);
            if (result is not null) return result;
        }

        return null;
    }

    private static Dictionary<string, SigCipherOpType> ParseCipherObjectMethods(ObjectExpression oe, string baseJs)
    {
        var methodMap = new Dictionary<string, SigCipherOpType>(StringComparer.Ordinal);

        foreach (var prop in oe.Properties)
        {
            if (prop is Property p && p.Key is Identifier id && p.Value is FunctionExpression fe && fe.Body is not null)
            {
                var bodyStr = baseJs.Substring(fe.Body.Start, fe.Body.End - fe.Body.Start);
                SigCipherOpType? opType = null;

                if (bodyStr.Contains(".reverse") || bodyStr.Contains("[\"reverse\"]"))
                {
                    opType = SigCipherOpType.Reverse;
                }
                else if (bodyStr.Contains(".splice") || bodyStr.Contains("[\"splice\"]"))
                {
                    opType = SigCipherOpType.Splice;
                }
                else if (bodyStr.Contains("[0]") && bodyStr.Contains("%"))
                {
                    opType = SigCipherOpType.Swap;
                }

                if (opType.HasValue)
                {
                    methodMap[id.Name] = opType.Value;
                }
            }
        }

        return methodMap;
    }

    private static List<SigCipherOperation> ExtractOperationsFromDecipherBody(
        Node funcNode, Dictionary<string, SigCipherOpType> methodMap)
    {
        var ops = new List<SigCipherOperation>();
        BlockStatement? body = null;

        if (funcNode is FunctionDeclaration fd) body = fd.Body;
        else if (funcNode is FunctionExpression fe) body = fe.Body;

        if (body is null) return ops;

        CollectOperationsInNode(body, methodMap, ops);
        return ops;
    }

    private static void CollectOperationsInNode(
        Node? node, Dictionary<string, SigCipherOpType> methodMap, List<SigCipherOperation> ops)
    {
        if (node is null) return;

        if (node is CallExpression ce)
        {
            var callee = ce.Callee;
            while (callee is ParenthesizedExpression pe)
            {
                callee = pe.Expression;
            }
            if (callee is MemberExpression me && me.Property is Identifier methodId)
            {
                if (methodMap.TryGetValue(methodId.Name, out var opType))
                {
                    int param = 0;
                    if (ce.Arguments.Count > 1 && ce.Arguments[1] is Literal lit && lit.Value is double d)
                    {
                        param = (int)d;
                    }
                    else if (ce.Arguments.Count > 1 && ce.Arguments[1] is Literal lit2 && lit2.Value is int i)
                    {
                        param = i;
                    }

                    ops.Add(new SigCipherOperation(opType, param));
                }
            }
        }

        foreach (var child in node.ChildNodes)
        {
            CollectOperationsInNode(child, methodMap, ops);
        }
    }
}