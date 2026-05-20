using System.Text;
using Acornima;
using Acornima.Ast;
using LMP.Core.Logger;

namespace LMP.Core.Youtube.Bridge.SigCipher;

/// <summary>
/// Обеспечивает высокопроизводительный разбор и модификацию плеера на основе AST Acornima.
/// </summary>
public static class YoutubeAstSolver
{
    private static readonly MatchTemplate IdentifierTemplate = new()
    {
        Or = [
            new()
            {
                NodeType = typeof(ExpressionStatement),
                Expression = new()
                {
                    NodeType = typeof(AssignmentExpression),
                    Operator = "=",
                    Left = new()
                    {
                        Or = [
                            new() { NodeType = typeof(Identifier) },
                            new() { NodeType = typeof(MemberExpression) }
                        ]
                    },
                    Right = new()
                    {
                        NodeType = typeof(FunctionExpression),
                        Async = false
                    }
                }
            },
            new()
            {
                NodeType = typeof(FunctionDeclaration),
                Async = false,
                Id = new() { NodeType = typeof(Identifier) }
            },
            new()
            {
                NodeType = typeof(VariableDeclaration),
                AnyKey = [
                    new()
                    {
                        NodeType = typeof(VariableDeclarator),
                        Init = new()
                        {
                            NodeType = typeof(FunctionExpression),
                            Async = false
                        }
                    }
                ]
            }
        ]
    };

    private static readonly MatchTemplate AsdasdTemplate = new()
    {
        NodeType = typeof(ExpressionStatement),
        Expression = new()
        {
            NodeType = typeof(CallExpression),
            Callee = new()
            {
                NodeType = typeof(MemberExpression),
                Object = new() { NodeType = typeof(Identifier) },
                Optional = false
            },
            Arguments = [
                new() { NodeType = typeof(Literal), Value = "alr" },
                new() { NodeType = typeof(Literal), Value = "yes" }
            ],
            Optional = false
        }
    };

   private const string SetupScript = """
    if (typeof globalThis.__log === "undefined") {
        globalThis.__log = function() {};
    }
    globalThis._result = globalThis._result || {};

    // 1. Сначала инициализируем глобальный _yt_player правильными свойствами Signature Timestamp.
    // Именно этот объект передается в качестве локального 'g' в IIFE плеера!
    globalThis._yt_player = globalThis._yt_player || {};
    (function() {
        var stsVal = typeof globalThis.__sts !== "undefined" ? globalThis.__sts : 20590;
        globalThis._yt_player.sts = stsVal;
        globalThis._yt_player.qj = stsVal;
        globalThis._yt_player.Mo = stsVal;
        globalThis._yt_player.signatureTimestamp = stsVal;
    })();

    // 2. Связываем Proxy для g напрямую с _yt_player для полной синхронизации
    if (typeof globalThis.g === "undefined") {
        try {
            var _gFallback = function() {}; 
            _gFallback.prototype = {};
            Object.defineProperty(globalThis, 'g', {
                value: new Proxy(globalThis._yt_player, {
                    get: function(t, p) {
                        if (p === 'then') return void 0;
                        if (typeof p === 'symbol') return void 0;
                        if (p in t) return t[p];
                        return _gFallback;
                    },
                    set: function(t, p, v) { t[p] = v; return true; },
                    has: function() { return true; }
                }),
                writable: true,
                configurable: true,
                enumerable: true
            });
        } catch (e) {
            globalThis.g = globalThis._yt_player;
        }
    }

    (function() {
        var URLPolyfill = function(url, base) {
            var absoluteUrl = url;
            if (base) {
                if (url.startsWith("/")) {
                    var originMatch = base.match(/^https?:\/\/[^\/]+/);
                    absoluteUrl = (originMatch ? originMatch[0] : "") + url;
                } else if (!url.startsWith("http")) {
                    absoluteUrl = base.replace(/\/?[^\/]*$/, "/") + url;
                }
            }
            
            var match = absoluteUrl.match(/^(https?:)\/\/([^\/?#]+)([^?#]*)(?:\?([^#]*))?(?:#(.*))?$/);
            if (!match) throw new Error("Invalid URL: " + url);
            
            this.protocol = match[1];
            this.host = match[2];
            this.hostname = match[2].split(":")[0];
            this.pathname = match[3] || "/";
            this.search = match[4] ? "?" + match[4] : "";
            this.hash = match[5] ? "#" + match[5] : "";
            this.href = absoluteUrl;
            this.origin = this.protocol + "//" + this.host;
        };
        URLPolyfill.prototype.toString = function() { return this.href; };

        function defineGlobal(name, val) {
            try {
                Object.defineProperty(globalThis, name, {
                    value: val,
                    writable: true,
                    configurable: true,
                    enumerable: true
                });
            } catch(e) {
                globalThis[name] = val;
            }
        }

        defineGlobal('URL', URLPolyfill);
        defineGlobal('location', new URLPolyfill("https://www.youtube.com/watch?v=bMD38TVNUF8"));
        defineGlobal('self', globalThis);
        defineGlobal('window', globalThis);
        defineGlobal('top', globalThis);
        defineGlobal('parent', globalThis);
        defineGlobal('document', Object.create(null));
        defineGlobal('navigator', Object.create(null));
    })();

    if (typeof globalThis.XMLHttpRequest === "undefined") {
        globalThis.XMLHttpRequest = { prototype: {} };
    }

    globalThis.console = {
        log: function() {
            var msg = Array.prototype.slice.call(arguments).join(' ');
            globalThis.__log("[JS console.log] " + msg);
        },
        error: function() {
            var msg = Array.prototype.slice.call(arguments).join(' ');
            globalThis.__log("[JS console.error] " + msg);
        }
    };
    """;

    public static string PreprocessPlayer(string baseJs)
    {
        Log.Debug($"[AstSolver] Preprocessing player script. Length: {baseJs.Length / 1024} KB");

        var stsMatch = System.Text.RegularExpressions.Regex.Match(
            baseJs,
            @"(?:signatureTimestamp|sts)\s*[:=]\s*(\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        string sts = stsMatch.Success ? stsMatch.Groups[1].Value : "20522";
        Log.Debug($"[AstSolver] Extracted Signature Timestamp (STS): {sts}");

        var parser = new Parser();
        var program = parser.ParseScript(baseJs);

        var body = program.Body;
        Node? iifeNode = null;
        FunctionExpression? iifeFunc = null;

        if (body.Count == 1)
        {
            var func = body[0];
            if (func is ExpressionStatement es && es.Expression is CallExpression ce)
            {
                var callee = ce.Callee;
                while (callee is ParenthesizedExpression pe)
                {
                    callee = pe.Expression;
                }
                if (callee is FunctionExpression fe && fe.Body is not null)
                {
                    iifeNode = func;
                    iifeFunc = fe;
                }
            }
        }
        else if (body.Count == 2)
        {
            var func = body[1];
            if (func is ExpressionStatement es && es.Expression is CallExpression ce)
            {
                var callee = ce.Callee;
                while (callee is ParenthesizedExpression pe)
                {
                    callee = pe.Expression;
                }
                if (callee is FunctionExpression fe && fe.Body is not null)
                {
                    iifeNode = func;
                    iifeFunc = fe;
                }
            }
        }

        if (iifeNode is null || iifeFunc is null)
        {
            throw new InvalidOperationException("Unexpected player structure: Failed to locate main IIFE wrapper.");
        }

        var targetStatements = iifeFunc.Body.Body;
        var plainStatements = new List<Statement>(targetStatements.Count);

        for (int i = 0; i < targetStatements.Count; i++)
        {
            var node = targetStatements[i];

            if (i == 0 && node is VariableDeclaration vd && vd.Declarations.Count == 1 &&
                vd.Declarations[0].Id is Identifier id && id.Name == "window")
            {
                continue;
            }

            if (node is ExpressionStatement exprEs)
            {
                if (exprEs.Expression is AssignmentExpression or Literal)
                {
                    plainStatements.Add(node);
                }
            }
            else
            {
                plainStatements.Add(node);
            }
        }

        var (nSolvers, sigSolvers) = GetSolutions(plainStatements, baseJs);

        var sb = new StringBuilder(baseJs.Length / 4 + 4096);

        sb.AppendLine($"globalThis.__sts = {sts};");
        sb.AppendLine(SetupScript);

        if (body.Count == 2)
        {
            var stmt0 = body[0];
            sb.Append(baseJs.AsSpan(stmt0.Start, stmt0.End - stmt0.Start)).AppendLine(";");
        }

        int headerStart = iifeNode.Start;
        int headerEnd = iifeFunc.Body.Start + 1;
        sb.Append(baseJs.AsSpan(headerStart, headerEnd - headerStart)).AppendLine();

        for (int i = 0; i < plainStatements.Count; i++)
        {
            var stmt = plainStatements[i];
            int start = stmt.Start;
            int end = stmt.End;
            sb.Append(baseJs.AsSpan(start, end - start));
            sb.AppendLine(";");
        }

        AppendSolverAssignments(sb, "n", nSolvers);
        AppendSolverAssignments(sb, "sig", sigSolvers);

        int footerStart = iifeFunc.Body.End - 1;
        int footerEnd = iifeNode.End;
        sb.Append(baseJs.AsSpan(footerStart, footerEnd - footerStart)).AppendLine();

        var result = sb.ToString();

        // ВЫДАЕМ ПОЛНЫЙ ПРЕПРОЦЕССИРОВАННЫЙ JS-ФАЙЛ НА ДИСК ДЛЯ АНАЛИЗА
        try
        {
            var tempPath = Path.Combine(Path.GetTempPath(), $"LMP_player_debug_{sts}.js");
            File.WriteAllText(tempPath, result);
            Log.Info($"[AstSolver] Preprocessed raw JS written to: {tempPath}");
        }
        catch (Exception ex)
        {
            Log.Warn($"[AstSolver] Failed to write raw JS debug file: {ex.Message}");
        }

        Log.Debug($"[AstSolver] Preprocessing complete. Final size: {result.Length / 1024} KB. Solvers - N: {nSolvers.Count}, Sig: {sigSolvers.Count}");
        return result;
    }

    private static (List<string> N, List<string> Sig) GetSolutions(List<Statement> statements, string baseJs)
    {
        var nList = new List<string>(2);
        var sigList = new List<string>(2);

        Log.Debug($"[AstSolver] Scanning {statements.Count} plain statements for solver extraction...");

        for (int i = 0; i < statements.Count; i++)
        {
            var solver = Extract(statements[i], baseJs);
            if (solver is not null)
            {
                nList.Add(MakeSolver(solver, "n"));
                sigList.Add(MakeSolver(solver, "sig"));
                Log.Info($"[AstSolver] Successfully extracted solver at statement index {i}");
            }
        }

        return (nList, sigList);
    }

    private static string? Extract(Node node, string baseJs)
    {
        var matchedId = AstMatcher.Matches(node, IdentifierTemplate);
        if (!matchedId) return null;

        Node? targetName = null;
        IReadOnlyList<Statement>? bodyStatements = null;

        if (node is FunctionDeclaration fd)
        {
            if (fd.Id is Identifier && fd.Body is not null)
            {
                targetName = fd.Id;
                bodyStatements = fd.Body.Body;
            }
        }
        else if (node is ExpressionStatement es)
        {
            if (es.Expression is AssignmentExpression ae && ae.Right is FunctionExpression fe && fe.Body is not null)
            {
                if (ae.Left is Identifier || ae.Left is MemberExpression)
                {
                    targetName = ae.Left;
                    bodyStatements = fe.Body.Body;
                }
            }
        }
        else if (node is VariableDeclaration vd)
        {
            for (int i = 0; i < vd.Declarations.Count; i++)
            {
                var decl = vd.Declarations[i];
                if (decl.Id is Identifier && decl.Init is FunctionExpression fe && fe.Body is not null)
                {
                    targetName = decl.Id;
                    bodyStatements = fe.Body.Body;
                    break;
                }
            }
        }

        if (targetName is not null && bodyStatements is not null)
        {
            bool hasAsdasd = false;
            for (int i = 0; i < bodyStatements.Count; i++)
            {
                if (AstMatcher.Matches(bodyStatements[i], AsdasdTemplate))
                {
                    hasAsdasd = true;
                    break;
                }
            }

            if (hasAsdasd)
            {
                int start = targetName.Start;
                int end = targetName.End;
                var expressionCode = baseJs.Substring(start, end - start);
                return CreateSolver(expressionCode);
            }
        }

        return null;
    }

    private static string CreateSolver(string expressionCode)
    {
        return $$"""
        ({sig, n}) => {
          const safeStringify = (obj) => {
            var cache = [];
            var res = JSON.stringify(obj, function(key, value) {
              if (typeof value === 'object' && value !== null) {
                if (cache.indexOf(value) !== -1) return '[Circular]';
                cache.push(value);
              }
              return value;
            }, 2);
            cache = null;
            return res;
          };

          console.log('[AstSolver JS] ================== DEBUG RAW VALUES ==================');
          console.log('[AstSolver JS] globalThis.__sts (STS):', globalThis.__sts);
          console.log('[AstSolver JS] globalThis.g Proxy settings:', safeStringify(globalThis.g));
          console.log('[AstSolver JS] Input sig:', JSON.stringify(sig));
          console.log('[AstSolver JS] Input n:', JSON.stringify(n));
          
          // Безопасно разэкранируем входную сигнатуру во избежание мутаций над символами %3D
          const decodedSig = sig ? (sig.indexOf('%') >= 0 ? decodeURIComponent(sig) : sig) : undefined;
          console.log('[AstSolver JS] Decoded sig value for initialization:', JSON.stringify(decodedSig));
          
          const url = ({{expressionCode}})("https://www.youtube.com/watch?v=bMD38TVNUF8", "s", decodedSig);
          if (n) {
            url.set("n", n);
          }
          
          // РЕКУРСИВНЫЙ КРОУЛЕР: Собираем абсолютно все методы с инстанса url и всей цепочки прототипов
          var keys = [];
          var curr = url;
          while (curr && curr !== Object.prototype) {
              keys = keys.concat(Object.getOwnPropertyNames(curr));
              curr = Object.getPrototypeOf(curr);
          }
          keys = [...new Set(keys)]; // Удаляем дубликаты
          
          console.log('[AstSolver JS] Prototype & Instance keys detected on url:', JSON.stringify(keys));
          console.log('[AstSolver JS] URL object dump right after construction:', safeStringify(url));
          
          const sBefore = url.get("s");
          const nBefore = url.get("n");
          console.log('[AstSolver JS] url.get("s") right after init:', JSON.stringify(sBefore));
          console.log('[AstSolver JS] url.get("n") right after init:', JSON.stringify(nBefore));
          
          var calledKey = null;
          var decrypted = false;
          
          // Проверяем, выполнилась ли дешифрация автоматически в конструкторе
          if (decodedSig !== undefined && sBefore !== decodedSig) {
              decrypted = true;
              console.log('[AstSolver JS] Constructor auto-decrypted s! Before:', JSON.stringify(decodedSig), 'After:', JSON.stringify(sBefore));
          }
          if (n !== undefined && nBefore !== n) {
              decrypted = true;
              console.log('[AstSolver JS] Constructor auto-decrypted n! Before:', JSON.stringify(n), 'After:', JSON.stringify(nBefore));
          }
          
          // Если в конструкторе ничего не расшифровалось, ищем метод дешифрации динамически!
          if (!decrypted) {
              for (const key of keys) {
                if (["constructor", "set", "get", "clone", "toString", "toJSON"].includes(key)) {
                  continue;
                }
                if (typeof url[key] === "function") {
                  try {
                      console.log('[AstSolver JS] Attempting to invoke method:', key);
                      url[key]();
                      
                      const sAfter = url.get("s");
                      const nAfter = url.get("n");
                      console.log('[AstSolver JS] Method ' + key + ' completed. Intermediate s:', JSON.stringify(sAfter), 'n:', JSON.stringify(nAfter));
                      
                      if ((decodedSig !== undefined && sAfter !== decodedSig) || 
                          (n !== undefined && nAfter !== n)) {
                          calledKey = key;
                          decrypted = true;
                          console.log('[AstSolver JS] Decryption success via method:', key);
                          break;
                      }
                  } catch (e) {
                      console.log('[AstSolver JS] Method ' + key + ' failed: ' + String(e));
                  }
                }
              }
          }
          
          const s = url.get("s");
          const decodedN = url.get("n");
          console.log('[AstSolver JS] Decryption loop completed. Called method:', calledKey, 'Result n:', JSON.stringify(decodedN), 'Result s:', JSON.stringify(s));
          console.log('[AstSolver JS] ======================================================');
          
          return {
            sig: s ? (s.indexOf('%') >= 0 ? decodeURIComponent(s) : s) : null,
            n: decodedN ?? null,
          };
        }
        """;
    }

    private static string MakeSolver(string resultArrowFunc, string identName)
    {
        return $$"""
        ({{identName}}) => {
          return ({{resultArrowFunc}})({
            {{identName}}: {{identName}}
          }).{{identName}};
        }
        """;
    }

    private static void AppendSolverAssignments(StringBuilder sb, string resultKey, List<string> solvers)
    {
        if (solvers.Count == 0)
        {
            Log.Warn($"[AstSolver] Warning: No solvers extracted for key '{resultKey}'");
            return;
        }

        sb.Append("globalThis._result.").Append(resultKey).AppendLine(" = (_input) => {");
        sb.AppendLine("  const _results = new Set();");
        sb.AppendLine("  const errors = [];");
        sb.AppendLine("  const _generators = [");
        for (int i = 0; i < solvers.Count; i++)
        {
            sb.Append(solvers[i]);
            if (i < solvers.Count - 1) sb.Append(',');
            sb.AppendLine();
        }
        sb.AppendLine("  ];");
        sb.AppendLine("  for (var i = 0; i < _generators.length; i++) {");
        sb.AppendLine("    try {");
        sb.AppendLine("      var res = _generators[i](_input);");
        sb.AppendLine("      _results.add(res);");
        sb.AppendLine("    } catch (e) {");
        sb.AppendLine("      errors.push(e);");
        sb.AppendLine("    }");
        sb.AppendLine("  }");
        sb.AppendLine("  if (!_results.size) {");
        sb.AppendLine("    throw `no solutions: ${errors.join(', ')}`;");
        sb.AppendLine("  }");
        sb.AppendLine("  if (_results.size !== 1) {");
        sb.AppendLine("    throw `invalid solutions: ${[..._results].map(x => JSON.stringify(x)).join(', ')}`;");
        sb.AppendLine("  }");
        sb.AppendLine("  return _results.values().next().value;");
        sb.AppendLine("};");
    }
}