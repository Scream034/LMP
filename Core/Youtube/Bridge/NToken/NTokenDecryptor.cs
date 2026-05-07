using System.Text.RegularExpressions;
using Jint;
using Jint.Native;
using LMP.Core.Youtube.Bridge.Common;

namespace LMP.Core.Youtube.Bridge.NToken;

/// <summary>
/// N-Token дешифратор YouTube.
/// <para>
/// Стратегии (в порядке приоритета):
/// 1. Wrapper Sieve Fuzzing — исполнение реальных обёрток (включая L2) из base.js
/// 2. Direct Magic Fuzzing — перебор реальных числовых аргументов, найденных в base.js
/// 3. Algebraic Fuzzing — перебор вычисленных через AST констант
/// 4. Прямой вызов ядра без аргументов (fallback)
/// </para>
/// </summary>
public sealed partial class NTokenDecryptor(PlayerContextManager playerManager)
    : JsDecryptorBase<NTokenDecryptor>(playerManager, G.FilePath.NTokenCache, 2000, 500)
{
    /// <summary>
    /// Длинный тестовый токен, гарантирующий корректную математику N-Token ядра.
    /// Включает разнообразные символы для точной валидации перемешивания.
    /// </summary>
    private const string TestToken = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmn";

    private const int MaxWrapperBodyLength = 1500;
    private const int MaxWrapperParams = 4;
    private const int MaxFuzzCandidates = 50;
    private const int MaxNTokenLength = 60;

    /// <summary>
    /// Минимальная разница в символах для принятия результата.
    /// </summary>
    private const int MinDiffCount = 12;

    private static readonly string[] ArrayMethodMarkers =
        ["'pop'", "'push'", "'reverse'", "'splice'", "'shift'", "'unshift'"];

    /// <summary>Расшифровывает N-токен.</summary>
    public async ValueTask<string> DecryptAsync(string nToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(nToken)) return nToken;
        if (Cache.TryGet(nToken, out var cached)) return cached;

        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        var result = TryInvokeJs(nToken, "NToken");
        return result ?? nToken;
    }

    protected override void InitializeCore(PlayerContext context)
    {
        Log.Info("[NToken] Initializing...");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var coreName = FindCoreFunctionName(context.BaseJs);
        if (coreName is null)
        {
            Log.Error($"[NToken] Core function not found in base.js (version={context.Version})");
            return;
        }
        Log.Debug($"[NToken] Core function: {coreName}");

        // Pre-analyze: rP(1, 3206, this) → magic numbers для hybrid strategy
        var methodCalls = FindMethodCallPatterns(context.BaseJs, coreName);

        // Strategy 1: Wrapper Sieve Fuzzing
        var wrappers = FindAllShortWrappers(context.BaseJs, coreName);
        bool success = TryWrapperSieveFuzzing(context, coreName, wrappers);

        // Strategy 2: Method-based invocation (g.hY.mH pattern)
        if (!success)
            success = TryMethodBasedInvocation(context, coreName, methodCalls);

        // Strategy 3: Hybrid — callable wrappers × method-call magic numbers
        if (!success && methodCalls.Count > 0 && wrappers.Count > 0)
            success = TryHybridWrapperStrategy(context, wrappers, methodCalls);

        // Strategy 4: Magic Numbers + Fallback
        if (!success)
        {
            var candidates = MagicNumbersExtractor.ExtractCandidates(context.BaseJs, coreName);
            if (candidates.Count == 0 || candidates.TrueForAll(c => c.HasArgs))
                candidates.Add(MagicNumbers.None);

            Log.Info($"[NToken] Testing {candidates.Count} candidate(s): " +
                     $"{string.Join(" | ", candidates.Take(10))}");

            success = TryInitWithCandidates(
                context, coreName, candidates,
                BuildCoreWrapperScript, TestToken,
                (baseJs, fn) => BuildDefaultBundle(baseJs, fn),
                ValidateNTokenResult);
        }

        sw.Stop();
        if (success)
            Log.Info($"[NToken] Ready in {sw.ElapsedMilliseconds}ms (core={coreName})");
        else
            Log.Error($"[NToken] All strategies failed after {sw.ElapsedMilliseconds}ms (core={coreName})");
    }

    /// <summary>
    /// Strategy 2: Method-based invocation — new ClassName(n).method().
    /// </summary>
    private bool TryMethodBasedInvocation(PlayerContext context, string coreName,
        List<MethodCallInfo> calls)
    {
        if (calls.Count == 0) return false;

        Log.Info($"[NToken] Method strategy: found {calls.Count} class method call(s)");

        Engine? engine = null;
        try
        {
            engine = CreateFullEngine();
            engine.Execute(BrowserStubs);
            engine.Execute(context.BaseJs);

            try { engine.Execute($"try{{ window['{coreName}']={coreName}; }}catch(_){{}}"); }
            catch { /* core may be inside closure */ }

            foreach (var call in calls)
            {
                Log.Debug($"[NToken] Method strategy: trying {call}");

                if (TryMethodApproach(engine, call, "standard", BuildStandardApproach(call))
                 || TryMethodApproach(engine, call, "proto-call", BuildProtoCallApproach(call))
                 || TryMethodApproach(engine, call, "direct-core", BuildDirectCoreApproach(call, coreName))
                 || TryMethodApproach(engine, call, "url-core", BuildUrlCoreApproach(call, coreName)))
                {
                    FullEngine = engine;
                    FullFuncName = "__decryptorTransform";
                    engine = null;
                    Log.Info($"[NToken] Method strategy ready: {call}");
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"[NToken] Method strategy: full JS init failed: " +
                      $"{Truncate(FormatJsException(ex), 120)}");
        }

        engine?.Dispose();
        return false;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Strategy 3: Hybrid — callable wrappers × magic numbers
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Hybrid strategy: вызывает обёртки (x4 и др.) с magic numbers из method-call
    /// анализа и proxy-this объектом, который возвращает n-token для любого свойства.
    /// <para>
    /// Ядро rP(Z, k, N) ожидает объект N с полями. Обёртка x4 экспортирована в window
    /// и внутри вызывает rP. Мы передаём (magic1, magic2, proxyThis) — proxy возвращает
    /// n-token для любого string-ключа, позволяя ядру прочитать токен из «правильного» поля.
    /// </para>
    /// </summary>
    private bool TryHybridWrapperStrategy(PlayerContext context,
        List<WrapperCandidate> wrappers, List<MethodCallInfo> methodCalls)
    {
        Log.Info($"[NToken] Hybrid strategy: {wrappers.Count} wrapper(s) × " +
                 $"{methodCalls.Count} magic pair(s)");

        var modifiedJs = InjectWrapperExports(context.BaseJs, wrappers);

        Engine? engine = null;
        try
        {
            engine = CreateFullEngine();
            engine.Execute(BrowserStubs);
            engine.Execute(modifiedJs);

            foreach (var wrapper in wrappers)
            {
                if (!IsCallableInEngine(engine, wrapper.Name)) continue;

                foreach (var call in methodCalls)
                {
                    Log.Debug($"[NToken] Hybrid: {wrapper.Name}" +
                              $"({call.MagicArg1}, {call.MagicArg2}, ...)");

                    var result = TryHybridMagicCall(
                        engine, wrapper, call.MagicArg1, call.MagicArg2, TestToken);

                    if (result is not null)
                    {
                        FullEngine = engine;
                        Cache.Set(TestToken, result);
                        engine.Execute(BuildHybridRuntimeWrapper(
                            wrapper.Name, wrapper.ParamCount,
                            call.MagicArg1, call.MagicArg2));
                        FullFuncName = "__decryptorTransform";
                        engine = null;
                        Log.Info($"[NToken] Hybrid ready: {wrapper.Name}" +
                                 $"({call.MagicArg1}, {call.MagicArg2}, ...)");
                        return true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"[NToken] Hybrid failed: {Truncate(FormatJsException(ex), 120)}");
        }
        finally
        {
            engine?.Dispose();
        }
        return false;
    }

    /// <summary>
    /// Вставляет window-экспорты обёрток внутрь base.js (в те же scope где они определены).
    /// </summary>
    private static string InjectWrapperExports(string baseJs, List<WrapperCandidate> wrappers)
    {
        var sorted = wrappers.OrderByDescending(w => w.Position).ToList();
        var js = baseJs;
        foreach (var w in sorted)
        {
            int insertPos = FindEndOfDefinition(js, w.Position);
            if (insertPos < 0) continue;
            js = js.Insert(insertPos,
                $"\ntry{{window['{w.Name}']={w.Name};}}catch(_eX){{}}\n");
        }
        return js;
    }

    /// <summary>
    /// Пробует вызвать обёртку с magic args + token в разных форматах.
    /// Возвращает расшифрованный токен или null.
    /// </summary>
    private static string? TryHybridMagicCall(Engine engine, WrapperCandidate wrapper,
        int magic1, int magic2, string testToken)
    {
        try
        {
            engine.Execute(BuildHybridTestScript(
                wrapper.Name, wrapper.ParamCount, magic1, magic2));
            var result = engine.Invoke("__hybridTest", testToken).AsString();

            if (!string.IsNullOrEmpty(result) && result != testToken)
            {
                if (ValidateNTokenResult(result, testToken))
                {
                    Log.Debug($"[NToken] Hybrid hit: {wrapper.Name}" +
                              $"({magic1}, {magic2}) → '{Truncate(result, 20)}'");
                    return result;
                }
                Log.Debug($"[NToken] Hybrid: {wrapper.Name} → " +
                          $"'{Truncate(result, 30)}' (rejected)");
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"[NToken] Hybrid error for {wrapper.Name}: " +
                      $"{Truncate(ex.Message, 80)}");
        }
        return null;
    }

    /// <summary>
    /// JS-функция для тестирования hybrid-вызова.
    /// Пробует 4 варианта 3-го аргумента: string, array, proxy-this, url-proxy.
    /// </summary>
    private static string BuildHybridTestScript(string funcName, int paramCount,
        int magic1, int magic2) => $$"""
        window.__hybridTest = function(n) {
            var fn = window['{{funcName}}'];
            if (typeof fn !== 'function') return '';
            var pc = {{paramCount}};
            function pad(a) { while (a.length < pc) a.push(undefined); return a; }
            
            try {
                var r = fn.apply(null, pad([{{magic1}}, {{magic2}}, n]));
                if (typeof r === 'string' && r.length > 0 && r !== n) return r;
                if (Array.isArray(r)) { var s = r.join(''); if (s.length > 0 && s !== n) return s; }
            } catch(e) {}
            
            try {
                var a = n.split('');
                var r2 = fn.apply(null, pad([{{magic1}}, {{magic2}}, a]));
                if (typeof r2 === 'string' && r2.length > 0 && r2 !== n) return r2;
                if (Array.isArray(r2)) { var s = r2.join(''); if (s.length > 0 && s !== n) return s; }
                var m = a.join(''); if (m.length > 0 && m !== n) return m;
            } catch(e) {}
            
            try {
                var proxy = new Proxy({}, {
                    get: function(t, p) { if (p in t) return t[p]; return typeof p === 'string' ? n : void 0; },
                    set: function(t, p, v) { t[p] = v; return true; },
                    has: function() { return true; }
                });
                var r3 = fn.apply(null, pad([{{magic1}}, {{magic2}}, proxy]));
                if (typeof r3 === 'string' && r3.length > 0 && r3 !== n) return r3;
                if (Array.isArray(r3)) { var s = r3.join(''); if (s.length > 0 && s !== n) return s; }
                var keys = Object.keys(proxy);
                for (var i = 0; i < keys.length; i++) {
                    var v = proxy[keys[i]];
                    if (typeof v === 'string' && v.length > 4 && v.length < 60 && v !== n) return v;
                }
            } catch(e) {}
            
            try {
                var url = 'https://rr.googlevideo.com/videoplayback?n=' + n + '&itag=251';
                var p2 = new Proxy({url: url, j: url}, {
                    get: function(t, p) { if (p in t) return t[p]; return typeof p === 'string' ? url : void 0; },
                    set: function(t, p, v) { t[p] = v; return true; },
                    has: function() { return true; }
                });
                var r4 = fn.apply(null, pad([{{magic1}}, {{magic2}}, p2]));
                if (typeof r4 === 'string' && r4.length > 4 && r4 !== n && r4 !== url) {
                    var nm = r4.match(/[?&]n=([^&]+)/);
                    if (nm) return nm[1];
                    if (r4.length < 60) return r4;
                }
                var k2 = Object.keys(p2);
                for (var i = 0; i < k2.length; i++) {
                    var v = p2[k2[i]];
                    if (typeof v !== 'string' || v === n || v === url) continue;
                    var nm2 = v.match(/[?&]n=([^&]+)/);
                    if (nm2 && nm2[1] !== n) return nm2[1];
                    if (v.length > 4 && v.length < 60) return v;
                }
            } catch(e) {}
            
            return '';
        };
        """;

    /// <summary>
    /// Runtime-обёртка для hybrid: вызывает wrapper(magic1, magic2, proxyThis)
    /// при каждой дешифровке n-token.
    /// </summary>
    private static string BuildHybridRuntimeWrapper(string funcName, int paramCount,
        int magic1, int magic2) => $$"""
        var __lastError = '';
        window.__decryptorTransform = function(n) {
            __lastError = '';
            try {
                var fn = window['{{funcName}}'];
                if (typeof fn !== 'function') { __lastError = 'not a function'; return n; }
                var pc = {{paramCount}};
                function pad(a) { while (a.length < pc) a.push(undefined); return a; }
                
                try {
                    var proxy = new Proxy({}, {
                        get: function(t, p) { if (p in t) return t[p]; return typeof p === 'string' ? n : void 0; },
                        set: function(t, p, v) { t[p] = v; return true; },
                        has: function() { return true; }
                    });
                    var r = fn.apply(null, pad([{{magic1}}, {{magic2}}, proxy]));
                    if (typeof r === 'string' && r.length > 0 && r !== n) return r;
                    if (Array.isArray(r)) { var s = r.join(''); if (s.length > 0 && s !== n) return s; }
                    var keys = Object.keys(proxy);
                    for (var i = 0; i < keys.length; i++) {
                        var v = proxy[keys[i]];
                        if (typeof v === 'string' && v.length > 4 && v.length < 60 && v !== n) return v;
                    }
                } catch(e1) {}
                
                try {
                    var r2 = fn.apply(null, pad([{{magic1}}, {{magic2}}, n]));
                    if (typeof r2 === 'string' && r2.length > 0 && r2 !== n) return r2;
                } catch(e2) {}
                
                try {
                    var a = n.split('');
                    var r3 = fn.apply(null, pad([{{magic1}}, {{magic2}}, a]));
                    if (typeof r3 === 'string' && r3.length > 0 && r3 !== n) return r3;
                    if (Array.isArray(r3)) { var s = r3.join(''); if (s.length > 0 && s !== n) return s; }
                    var m = a.join(''); if (m.length > 0 && m !== n) return m;
                } catch(e3) {}
                
                __lastError = 'no valid result';
                return n;
            } catch(e) { __lastError = String(e); return n; }
        };
        """;

    /// <summary>
    /// Выполняет один подход: загружает обёртку, вызывает с тестовым токеном, валидирует.
    /// </summary>
    private bool TryMethodApproach(Engine engine, MethodCallInfo call,
    string name, string wrapperJs)
    {
        try
        {
            engine.Execute(wrapperJs);
            var result = engine.Invoke("__decryptorTransform", TestToken).AsString();
            if (ValidateNTokenResult(result, TestToken))
            {
                Cache.Set(TestToken, result);
                Log.Debug($"[NToken] Method approach '{name}' succeeded for {call}");
                return true;
            }
            var jsError = ReadJsError(engine);
            if (jsError is not null)
                Log.Debug($"[NToken] Method {name} error for {call}: {jsError}");
        }
        catch (Exception ex)
        {
            Log.Debug($"[NToken] Method {name} crashed for {call}: " +
                      $"{Truncate(FormatJsException(ex), 120)}");
        }
        return false;
    }

    private static string BuildStandardApproach(MethodCallInfo call) => $$"""
        var __lastError = '';
        window.__decryptorTransform = function(n) {
            __lastError = '';
            try {
                var cls = {{call.ClassName}};
                if (!cls || typeof cls !== 'function') { __lastError = 'standard: class not found'; return n; }
                var obj = new cls(n);
                if (typeof obj.{{call.MethodName}} !== 'function') {
                    __lastError = 'standard: {{call.MethodName}} not a function';
                    return n;
                }
                var r = obj.{{call.MethodName}}();
                if (typeof r === 'string' && r.length > 0 && r !== n) return r;
                var keys = Object.keys(obj);
                for (var i = 0; i < keys.length; i++) {
                    var v = obj[keys[i]];
                    if (typeof v === 'string' && v.length > 4 && v.length < 60 && v !== n) return v;
                }
                __lastError = 'standard: returned ' + typeof r;
                return n;
            } catch(e) { __lastError = 'standard: ' + String(e); return n; }
        };
        """;

    private static string BuildProtoCallApproach(MethodCallInfo call) => $$"""
        var __lastError = '';
        window.__decryptorTransform = function(n) {
            __lastError = '';
            try {
                var cls = {{call.ClassName}};
                if (!cls) { __lastError = 'proto: class not found'; return n; }
                var proto = cls.prototype;
                if (!proto) { __lastError = 'proto: no prototype'; return n; }
                var obj = Object.create(proto);
                try { cls.call(obj, n); } catch(e1) {
                    obj.n = n; obj.url = n; obj.j = n; obj.B = n;
                }
                var m = obj.{{call.MethodName}} || proto.{{call.MethodName}};
                if (typeof m !== 'function') { __lastError = 'proto: method not found'; return n; }
                var r = m.call(obj);
                if (typeof r === 'string' && r.length > 0 && r !== n) return r;
                var keys = Object.keys(obj);
                for (var i = 0; i < keys.length; i++) {
                    var v = obj[keys[i]];
                    if (typeof v === 'string' && v.length > 4 && v.length < 60 && v !== n) return v;
                }
                __lastError = 'proto: no valid result';
                return n;
            } catch(e) { __lastError = 'proto: ' + String(e); return n; }
        };
        """;

    private static string BuildDirectCoreApproach(MethodCallInfo call, string coreName) => $$"""
        var __lastError = '';
        window.__decryptorTransform = function(n) {
            __lastError = '';
            try {
                var fn = window['{{coreName}}'];
                if (typeof fn !== 'function') { __lastError = 'direct: core not callable'; return n; }
                var _mut = {};
                var fakeThis = new Proxy({}, {
                    get: function(t, p) { if (p in t) return t[p]; return typeof p === 'string' ? n : void 0; },
                    set: function(t, p, v) { t[p] = v; _mut[p] = v; return true; },
                    has: function() { return true; }
                });
                var r = fn({{call.MagicArg1}}, {{call.MagicArg2}}, fakeThis);
                if (typeof r === 'string' && r.length > 0 && r !== n) return r;
                if (Array.isArray(r)) { var s = r.join(''); if (s.length > 0 && s !== n) return s; }
                var mk = Object.keys(_mut);
                for (var i = 0; i < mk.length; i++) {
                    var v = _mut[mk[i]];
                    if (typeof v === 'string' && v.length > 4 && v.length < 60 && v !== n) return v;
                }
                __lastError = 'direct: returned ' + typeof r;
                return n;
            } catch(e) { __lastError = 'direct: ' + String(e); return n; }
        };
        """;

    private static string BuildUrlCoreApproach(MethodCallInfo call, string coreName) => $$"""
        var __lastError = '';
        window.__decryptorTransform = function(n) {
            __lastError = '';
            try {
                var fn = window['{{coreName}}'];
                if (typeof fn !== 'function') { __lastError = 'url: not callable'; return n; }
                var url = 'https://rr5---sn-test.googlevideo.com/videoplayback?n=' + n + '&itag=251';
                var fakeThis = new Proxy({url: url, j: url}, {
                    get: function(t, p) { if (p in t) return t[p]; return typeof p === 'string' ? url : void 0; },
                    set: function(t, p, v) { t[p] = v; return true; },
                    has: function() { return true; }
                });
                var r = fn({{call.MagicArg1}}, {{call.MagicArg2}}, fakeThis);
                if (typeof r === 'string' && r !== n && r !== url) {
                    var m = r.match && r.match(/[?&]n=([^&]+)/);
                    if (m && m[1] !== n && m[1].length > 4) return m[1];
                    if (r.length > 4 && r.length < 60) return r;
                }
                var keys = Object.keys(fakeThis);
                for (var i = 0; i < keys.length; i++) {
                    var v = fakeThis[keys[i]];
                    if (typeof v !== 'string' || v === n || v === url) continue;
                    var m2 = v.match && v.match(/[?&]n=([^&]+)/);
                    if (m2 && m2[1] !== n && m2[1].length > 4) return m2[1];
                    if (v.length > 4 && v.length < 60) return v;
                }
                __lastError = 'url: no result';
                return n;
            } catch(e) { __lastError = 'url: ' + String(e); return n; }
        };
        """;

    /// <summary>Чтение __lastError из JS-движка.</summary>
    private static string? ReadJsError(Engine engine)
    {
        try
        {
            var errorVal = engine.GetValue("__lastError");
            if (errorVal.IsString())
                return string.IsNullOrWhiteSpace(errorVal.AsString()) ? null : errorVal.AsString();
        }
        catch { /* ignore */ }
        return null;
    }

    /// <summary>
    /// Описание найденного вызова ядра из метода класса.
    /// </summary>
    private readonly record struct MethodCallInfo(
        string ClassName,
        string MethodName,
        int MagicArg1,
        int MagicArg2,
        string ConstructorFirstParamRole);

    /// <summary>
    /// Ищет вызовы ядра вида coreName(num, num, this) внутри методов классов.
    /// Также находит конструктор класса для понимания как создать объект.
    /// </summary>
    private static List<MethodCallInfo> FindMethodCallPatterns(string baseJs, string coreName)
    {
        var results = new List<MethodCallInfo>();
        var span = baseJs.AsSpan();

        // ═══ Паттерн: coreName(число, число, this) или coreName(число, число, переменная) ═══
        var pattern = $"{coreName}(";
        int searchFrom = 0;

        while (searchFrom < span.Length)
        {
            int callPos = span[searchFrom..].IndexOf(pattern.AsSpan(), StringComparison.Ordinal);
            if (callPos < 0) break;
            callPos += searchFrom;
            searchFrom = callPos + pattern.Length;

            // Проверяем что перед coreName нет буквы (не часть другого идентификатора)
            if (callPos > 0 && JsFunctionExtractor.IsIdentChar(span[callPos - 1])) continue;

            // Парсим аргументы
            int parenStart = callPos + coreName.Length;
            int parenEnd = JsFunctionExtractor.FindMatchingParen(baseJs, parenStart);
            if (parenEnd < 0) continue;

            var argsSpan = span[(parenStart + 1)..parenEnd];
            var argParts = SplitTopLevelCommas(argsSpan);

            // Ищем паттерн (число, число, this) или (число, число, переменная)
            if (argParts.Count < 3) continue;

            if (!int.TryParse(argParts[0].Trim().ToString(), out int magic1)) continue;
            if (!int.TryParse(argParts[1].Trim().ToString(), out int magic2)) continue;

            var thirdArg = argParts[2].Trim().ToString();
            if (thirdArg != "this") continue; // Только this-based вызовы

            // ═══ Ищем контекст: в каком классе/объекте этот вызов ═══
            // Ищем ближайший "class" или конструктор перед вызовом
            var classInfo = FindEnclosingClass(baseJs, callPos);
            if (classInfo is null) continue;

            Log.Debug($"[NToken] Found method call: {coreName}({magic1}, {magic2}, this) " +
                      $"in class '{classInfo.Value.ClassName}', method '{classInfo.Value.MethodName}'");

            results.Add(new MethodCallInfo(
                classInfo.Value.ClassName,
                classInfo.Value.MethodName,
                magic1,
                magic2,
                "ntoken"));
        }

        return results;
    }

    private readonly record struct ClassContext(string ClassName, string MethodName);

    /// <summary>
    /// Находит имя класса и метода, содержащего позицию в коде.
    /// Ищет паттерны: g.ClassName=class{...} или ClassName.prototype.method=function
    /// </summary>
    private static ClassContext? FindEnclosingClass(string baseJs, int pos)
    {
        var span = baseJs.AsSpan();

        // ═══ Ищем "class" перед позицией ═══
        int searchFrom = Math.Max(0, pos - 50000);
        int bestClassPos = -1;
        string? bestClassName = null;

        // Паттерн: g.XXX=class{ или XXX=class{
        int i = searchFrom;
        while (i < pos)
        {
            int classIdx = span[i..pos].IndexOf("=class", StringComparison.Ordinal);
            if (classIdx < 0) break;
            classIdx += i;

            // Извлекаем имя перед =class
            int nameEnd = classIdx;
            int nameStart = nameEnd - 1;
            while (nameStart >= 0 && (char.IsLetterOrDigit(span[nameStart]) || span[nameStart] is '_' or '$' or '.'))
                nameStart--;
            nameStart++;

            if (nameStart < nameEnd)
            {
                bestClassPos = classIdx;
                bestClassName = span[nameStart..nameEnd].ToString();
            }
            i = classIdx + 6;
        }

        if (bestClassName is null) return null;

        // ═══ Ищем имя метода — ближайший идентификатор() { перед позицией ═══
        // В классах YouTube: methodName() { ... coreName(1, 3206, this) ... }
        int methodSearchStart = Math.Max(bestClassPos, pos - 5000);
        string? methodName = null;

        for (int j = pos - 1; j >= methodSearchStart; j--)
        {
            if (span[j] == '{')
            {
                // Ищем )  перед {
                int k = j - 1;
                while (k >= methodSearchStart && span[k] is ' ' or '\t' or '\n' or '\r') k--;
                if (k < methodSearchStart || span[k] != ')') continue;
                int parenStart = -1;
                int depth = 1;
                k--;
                while (k >= methodSearchStart && depth > 0)
                {
                    if (span[k] == ')') depth++;
                    else if (span[k] == '(') { depth--; if (depth == 0) parenStart = k; }
                    k--;
                }
                if (parenStart < 0) continue;

                // Извлекаем имя метода перед (
                int mEnd = parenStart;
                int mStart = mEnd - 1;
                while (mStart >= methodSearchStart &&
                       (char.IsLetterOrDigit(span[mStart]) || span[mStart] is '_' or '$'))
                    mStart--;
                mStart++;

                if (mStart < mEnd)
                {
                    methodName = span[mStart..mEnd].ToString();
                    break;
                }
            }
        }

        return new ClassContext(bestClassName, methodName ?? "unknown");
    }

    /// <summary>
    /// Разбивает аргументы на top-level части по запятым.
    /// </summary>
    private static List<ReadOnlyMemory<char>> SplitTopLevelCommas(ReadOnlySpan<char> span)
    {
        var result = new List<ReadOnlyMemory<char>>(4);
        var str = span.ToString();
        int depth = 0, start = 0;

        for (int i = 0; i < str.Length; i++)
        {
            char c = str[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == ',' && depth == 0)
            {
                result.Add(str.AsMemory(start, i - start));
                start = i + 1;
            }
        }
        result.Add(str.AsMemory(start));
        return result;
    }

    private static bool ValidateNTokenResult(string? result, string input)
    {
        if (string.IsNullOrEmpty(result) || result == input) return false;
        if (result.Length < 5 || result.Length > MaxNTokenLength) return false;
        if (result is "undefined" or "null" or "NaN" or "true" or "false") return false;

        foreach (char c in result)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))
                return false;
        }

        if (input.Contains(result, StringComparison.Ordinal) || result.Contains(input, StringComparison.Ordinal))
            return false;

        // Защита от фальшивых обёрток: если результат содержит блок из 8+ 
        // нетронутых символов оригинала — это тривиальная обрезка, а не дешифровка.
        if (HasLongSharedSubstring(input.AsSpan(), result.AsSpan(), 8))
        {
            Log.Debug($"[NToken] Rejected trivial mutation: shares contiguous block with input. Result='{Truncate(result, 20)}'");
            return false;
        }

        // Substitution check - БОЛЕЕ СТРОГАЯ ВЕРСИЯ
        Span<int> freq = stackalloc int[128];
        foreach (char c in input) if (c < 128) freq[c]++;
        foreach (char c in result) if (c < 128) freq[c]--;

        int diffCount = 0;
        foreach (int f in freq) diffCount += Math.Abs(f);

        // Для длинных токенов (TestToken=36 символов) требуем больше различий
        int minDiffCount = input.Length >= 20 ? 20 : 12;
        if (diffCount < minDiffCount)
        {
            Log.Debug($"[NToken] Rejected weak mutation: diffCount={diffCount} < {minDiffCount}, result='{Truncate(result, 20)}'");
            return false;
        }

        // Новое: Проверка позиционных изменений
        // Если более 50% символов остались на своих местах - отклоняем
        int samePositionCount = 0;
        int minLength = Math.Min(input.Length, result.Length);
        for (int i = 0; i < minLength; i++)
        {
            if (input[i] == result[i]) samePositionCount++;
        }

        if (samePositionCount > minLength / 2)
        {
            Log.Debug($"[NToken] Rejected positional mutation: {samePositionCount}/{minLength} symbols unchanged");
            return false;
        }

        return true;
    }

    private static bool HasLongSharedSubstring(ReadOnlySpan<char> a, ReadOnlySpan<char> b, int minLength)
    {
        if (a.Length < minLength || b.Length < minLength) return false;
        for (int i = 0; i <= a.Length - minLength; i++)
        {
            var sub = a.Slice(i, minLength);
            if (b.IndexOf(sub) >= 0) return true;
        }
        return false;
    }

    private bool TryWrapperSieveFuzzing(PlayerContext context, string coreName, List<WrapperCandidate> wrappers)
    {
        if (wrappers.Count == 0) return false;
        Log.Info($"[NToken] Sieve: found {wrappers.Count} wrapper candidate(s)");
        return FuzzWrappersInFullJs(context, wrappers);
    }

    private readonly record struct FuzzResult(string DecryptedToken, int UsedArgCount, JsValue? SecondArg, bool UsesArrayInput);

    private bool FuzzWrappersInFullJs(PlayerContext context, List<WrapperCandidate> wrappers)
    {
        var sortedByPos = wrappers.OrderByDescending(w => w.Position).ToList();
        var modifiedJs = context.BaseJs;
        int exportedCount = 0;

        foreach (var w in sortedByPos)
        {
            int insertPos = FindEndOfDefinition(modifiedJs, w.Position);
            if (insertPos < 0)
            {
                Log.Debug($"[NToken] Sieve: could not find end of definition for wrapper '{w.Name}'");
                continue;
            }
            modifiedJs = modifiedJs.Insert(insertPos, $"\ntry{{window['{w.Name}']={w.Name};}}catch(_eX){{}}\n");
            exportedCount++;
        }

        if (exportedCount == 0) return false;

        Engine? engine = null;
        try
        {
            engine = CreateSieveEngine();
            engine.Execute(BrowserStubs);
            engine.Execute(modifiedJs);

            Log.Debug($"[NToken] Full JS executed, testing wrappers...");

            var arrayFallbackCandidates = new List<WrapperCandidate>(4);

            foreach (var wrapper in wrappers)
            {
                if (!IsCallableInEngine(engine, wrapper.Name))
                {
                    Log.Debug($"[NToken] Sieve: wrapper '{wrapper.Name}' (params={wrapper.ParamCount}) " +
                              $"not callable after full JS execution");
                    continue;
                }

                // Быстрая проверка 0-param оберток
                if (wrapper.ParamCount == 0)
                {
                    if (IsStaticResultWrapper(engine, wrapper.Name))
                    {
                        Log.Debug($"[NToken] Sieve: skipping '{wrapper.Name}' — " +
                                  $"returns static result (not N-token wrapper)");
                        continue;
                    }
                }

                Log.Debug($"[NToken] Sieve: testing wrapper '{wrapper.Name}' " +
                          $"(params={wrapper.ParamCount}, bodyLen={wrapper.Body.Length})");

                // Специальная обработка для больших оберток (как x4)
                if (wrapper.Body.Length > 500)
                {
                    Log.Info($"[NToken] Extended testing for large wrapper '{wrapper.Name}'");

                    // Пробуем сначала с числами, которые часто встречаются в YouTube
                    var specialNumbers = new[] { 2, 5, 50, 1, 0, 8, 9, 16, 17, 24, 25, 32, 33, 40, 41, 48, 49, 56, 57, 64, 65 };
                    foreach (var num in specialNumbers)
                    {
                        var tokenArg = MakeTokenArg(engine, TestToken, false);
                        var args = BuildArgsArray(wrapper.ParamCount, tokenArg, new JsNumber(num));

                        bool sawErr = false;
                        string? lastErrorKey = null;
                        int repeatCount = 0;

                        var result2 = TryCallAndValidate(engine, wrapper.Name, args, TestToken,
                            tokenArg, ref sawErr, ref lastErrorKey, ref repeatCount);

                        if (result2 is not null)
                        {
                            Log.Info($"[NToken] Found working combination for '{wrapper.Name}' with number {num}");
                            return AcceptSieveWinner(ref engine, wrapper, new FuzzResult(result2, wrapper.ParamCount, new JsNumber(num), false));
                        }
                    }
                }

                bool sawArrayError = false;
                var result = FuzzSingleWrapper(engine, wrapper, TestToken, false, ref sawArrayError);

                if (result is not null)
                    return AcceptSieveWinner(ref engine, wrapper, result.Value);

                if (sawArrayError)
                {
                    Log.Debug($"[NToken] Sieve: wrapper '{wrapper.Name}' saw array method error, " +
                              $"will retry with array input");
                    arrayFallbackCandidates.Add(wrapper);
                }

                if (wrapper.Name == "x4")
                {
                    Log.Info($"[NToken] Special focus on x4 wrapper - likely the real N-token decryptor");
                    // Можно добавить дополнительную диагностику
                    try
                    {
                        var funcCode = engine.Evaluate($"String(window['{wrapper.Name}'])").ToString();
                        Log.Debug($"[NToken] x4 function code preview: {Truncate(funcCode, 200)}");
                    }
                    catch (Exception ex)
                    {
                        Log.Debug($"[NToken] Could not get x4 function code: {ex.Message}");
                    }
                }
            }

            foreach (var wrapper in arrayFallbackCandidates)
            {
                Log.Debug($"[NToken] Sieve: retrying '{wrapper.Name}' with array input");
                bool sawArrayError = false;
                var result = FuzzSingleWrapper(engine, wrapper, TestToken, true, ref sawArrayError);

                if (result is not null)
                    return AcceptSieveWinner(ref engine, wrapper, result.Value);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[NToken] Sieve: full JS execution failed: {Truncate(FormatJsException(ex), 200)}");
        }
        finally
        {
            engine?.Dispose();
        }
        return false;
    }

    /// <summary>
    /// Проверяет, является ли обертка статической utility-функцией.
    /// </summary>
    private static bool IsStaticResultWrapper(Engine engine, string funcName)
    {
        try
        {
            var result = engine.Invoke(funcName);
            if (result.IsUndefined() || result.IsNull()) return false;

            // Возвращает что-то - значит это utility-функция
            if (result.IsArray() || result.IsString())
                return true;

            return false;
        }
        catch
        {
            return false; // Если крашится - оставляем для fuzzing
        }
    }

    private bool AcceptSieveWinner(ref Engine? engine, WrapperCandidate wrapper, FuzzResult r)
    {
        FullEngine = engine;
        Cache.Set(TestToken, r.DecryptedToken);
        engine!.Execute(BuildSieveRuntimeWrapper(wrapper.Name, wrapper.ParamCount, r));
        FullFuncName = "__decryptorTransform";
        engine = null; // Prevent disposal
        return true;
    }

    private static bool IsCallableInEngine(Engine engine, string name)
    {
        try
        {
            return engine.Evaluate($"typeof window['{name}']").ToString() == "function";
        }
        catch { return false; }
    }

    private static FuzzResult? FuzzSingleWrapper(Engine engine, WrapperCandidate wrapper,
     string testToken, bool useArrayInput, ref bool sawArrayMethodError)
    {
        string? lastErrorKey = null;
        int repeatCount = 0;
        const int maxRepeat = 3;

        var tokenArg = MakeTokenArg(engine, testToken, useArrayInput);
        bool sawErr = false;

        // Single-arg call
        var r = TryCallAndValidate(engine, wrapper.Name, [tokenArg], testToken,
            useArrayInput ? tokenArg : null, ref sawErr, ref lastErrorKey, ref repeatCount);
        sawArrayMethodError |= sawErr;
        if (r is not null) return new FuzzResult(r, 1, null, useArrayInput);

        if (wrapper.ParamCount >= 2)
        {
            // Для больших оберток (> 500 символов) - расширенный набор тестов
            bool isLargeWrapper = wrapper.Body.Length > 500;

            // Числовые значения второго аргумента
            int tokenLen = testToken.Length;
            var secondValues = new List<double>
        {
            double.NaN, 0, 1, 2, 3, 4, 5, 7, 8, 10, 16, 40, 64, 128, 255, 256,
            tokenLen, tokenLen - 1, tokenLen + 1, tokenLen / 2
        };

            // Добавляем больше значений для больших оберток
            if (isLargeWrapper)
            {
                secondValues.AddRange(new double[] {
                6, 9, 12, 15, 18, 21, 24, 27, 30, 33, 36, 39, 42, 45, 48, 51, 54, 57, 60, 63
            });
            }

            foreach (var val in secondValues)
            {
                var second = double.IsNaN(val) ? JsValue.Undefined : new JsNumber(val);
                var args = BuildArgsArray(wrapper.ParamCount, tokenArg, second);
                sawErr = false;
                r = TryCallAndValidate(engine, wrapper.Name, args, testToken,
                    useArrayInput ? tokenArg : null, ref sawErr, ref lastErrorKey, ref repeatCount);
                sawArrayMethodError |= sawErr;
                if (r is not null) return new FuzzResult(r, wrapper.ParamCount, second, useArrayInput);
                if (repeatCount >= maxRepeat && !isLargeWrapper) break; // для больших оберток продолжаем
            }

            // Пробуем array как второй аргумент
            var secondArr = MakeTokenArg(engine, testToken, true);
            var argsWithArray = BuildArgsArray(wrapper.ParamCount, tokenArg, secondArr);
            sawErr = false;
            r = TryCallAndValidate(engine, wrapper.Name, argsWithArray, testToken,
                tokenArg, ref sawErr, ref lastErrorKey, ref repeatCount);
            sawArrayMethodError |= sawErr;
            if (r is not null) return new FuzzResult(r, wrapper.ParamCount, secondArr, useArrayInput);

            // Пробуем строку как второй аргумент
            var argsWithString = BuildArgsArray(wrapper.ParamCount, tokenArg, new JsString(testToken));
            sawErr = false;
            r = TryCallAndValidate(engine, wrapper.Name, argsWithString, testToken,
                useArrayInput ? tokenArg : null, ref sawErr, ref lastErrorKey, ref repeatCount);
            sawArrayMethodError |= sawErr;
            if (r is not null) return new FuzzResult(r, wrapper.ParamCount, new JsString(testToken), useArrayInput);
        }

        // Специальная обработка для 4-параметровых оберток (как x4)
        if (wrapper.ParamCount >= 4)
        {
            Log.Debug($"[NToken] Testing extended strategies for 4+ param wrapper '{wrapper.Name}'");

            // Стратегия 1: [token, token.length, undefined, undefined]
            {
                var args = BuildArgsArray(wrapper.ParamCount, tokenArg, new JsNumber(testToken.Length));
                sawErr = false;
                r = TryCallAndValidate(engine, wrapper.Name, args, testToken,
                    useArrayInput ? tokenArg : null, ref sawErr, ref lastErrorKey, ref repeatCount);
                sawArrayMethodError |= sawErr;
                if (r is not null) return new FuzzResult(r, wrapper.ParamCount, new JsNumber(testToken.Length), useArrayInput);
            }

            // Стратегия 2: [token, token, token, token] - все аргументы = токен
            {
                var firstArg = tokenArg;
                var secondArg = MakeTokenArg(engine, testToken, useArrayInput);
                var thirdArg = MakeTokenArg(engine, testToken, useArrayInput);
                var fourthArg = MakeTokenArg(engine, testToken, useArrayInput);

                var args = new JsValue[wrapper.ParamCount];
                args[0] = firstArg;
                args[1] = secondArg;
                args[2] = thirdArg;
                args[3] = fourthArg;
                for (int i = 4; i < wrapper.ParamCount; i++) args[i] = JsValue.Undefined;

                sawErr = false;
                r = TryCallAndValidate(engine, wrapper.Name, args, testToken,
                    useArrayInput ? tokenArg : null, ref sawErr, ref lastErrorKey, ref repeatCount);
                sawArrayMethodError |= sawErr;
                if (r is not null) return new FuzzResult(r, wrapper.ParamCount, secondArg, useArrayInput);
            }

            // Стратегия 3: [token, length, 0, 1] - числовые аргументы
            {
                var args = new JsValue[wrapper.ParamCount];
                args[0] = tokenArg;
                args[1] = new JsNumber(testToken.Length);
                args[2] = new JsNumber(0);
                args[3] = new JsNumber(1);
                for (int i = 4; i < wrapper.ParamCount; i++) args[i] = JsValue.Undefined;

                sawErr = false;
                r = TryCallAndValidate(engine, wrapper.Name, args, testToken,
                    useArrayInput ? tokenArg : null, ref sawErr, ref lastErrorKey, ref repeatCount);
                sawArrayMethodError |= sawErr;
                if (r is not null) return new FuzzResult(r, wrapper.ParamCount, new JsNumber(testToken.Length), useArrayInput);
            }
        }

        if (wrapper.ParamCount == 0)
        {
            sawErr = false;
            r = TryCallAndValidate(engine, wrapper.Name, [], testToken, null,
                ref sawErr, ref lastErrorKey, ref repeatCount);
            sawArrayMethodError |= sawErr;
            if (r is not null) return new FuzzResult(r, 0, null, useArrayInput);
        }

        return null;
    }

    private static JsValue MakeTokenArg(Engine engine, string token, bool asArray)
    {
        if (!asArray) return token;
        engine.SetValue("__tmpToken", token);
        var arr = engine.Evaluate("__tmpToken.split('')");
        engine.SetValue("__tmpToken", JsValue.Undefined);
        return arr;
    }

    private static JsValue[] BuildArgsArray(int count, JsValue firstArg, JsValue secondArg)
    {
        var args = new JsValue[count];
        args[0] = firstArg;
        if (count > 1) args[1] = secondArg;
        for (int i = 2; i < count; i++) args[i] = JsValue.Undefined;
        return args;
    }

    private static string? TryCallAndValidate(Engine engine, string funcName, JsValue[] args,
     string testToken, JsValue? mutableArrayArg, ref bool sawArrayMethodError,
     ref string? lastErrorKey, ref int repeatCount)
    {
        try
        {
            var jsResult = engine.Invoke(funcName, args);
            lastErrorKey = null;
            repeatCount = 0;

            // Логируем undefined/null для основных вызовов
            if ((jsResult.IsUndefined() || jsResult.IsNull()) && args.Length <= 2)
            {
                Log.Debug($"[NToken] Sieve '{funcName}' returned " +
                          $"{(jsResult.IsUndefined() ? "undefined" : "null")} (args={args.Length})");
            }

            if (!jsResult.IsUndefined() && !jsResult.IsNull())
            {
                if (jsResult.IsArray())
                {
                    engine.SetValue("__tmpArr", jsResult);
                    var joined = engine.Evaluate("__tmpArr.join('')").AsString();
                    if (ValidateNTokenResult(joined, testToken)) return joined;
                    if (!string.IsNullOrEmpty(joined) && joined != testToken)
                        Log.Debug($"[NToken] Sieve '{funcName}' returned array→'{Truncate(joined, 30)}' " +
                                  $"(rejected by validation, args={args.Length})");
                }
                else if (!jsResult.IsObject())
                {
                    var str = jsResult.ToString();
                    if (ValidateNTokenResult(str, testToken)) return str;
                    if (!string.IsNullOrEmpty(str) && str != testToken)
                        Log.Debug($"[NToken] Sieve '{funcName}' returned '{Truncate(str, 30)}' " +
                                  $"(rejected by validation, args={args.Length})");
                }
            }

            // Проверяем мутацию первого массива
            if (mutableArrayArg is not null && mutableArrayArg.IsArray())
            {
                engine.SetValue("__tmpArr", mutableArrayArg);
                var mutated = engine.Evaluate("__tmpArr.join('')").AsString();
                if (ValidateNTokenResult(mutated, testToken)) return mutated;
                if (!string.IsNullOrEmpty(mutated) && mutated != testToken)
                    Log.Debug($"[NToken] Sieve '{funcName}' mutated array→'{Truncate(mutated, 30)}' " +
                              $"(rejected by validation, args={args.Length})");
            }

            // Новое: Проверяем мутацию ВТОРОГО аргумента, если он массив
            if (args.Length >= 2 && args[1].IsArray())
            {
                engine.SetValue("__tmpArr2", args[1]);
                var mutated2 = engine.Evaluate("__tmpArr2.join('')").AsString();
                if (ValidateNTokenResult(mutated2, testToken)) return mutated2;
                if (!string.IsNullOrEmpty(mutated2) && mutated2 != testToken)
                    Log.Debug($"[NToken] Sieve '{funcName}' mutated 2nd array→'{Truncate(mutated2, 30)}' " +
                              $"(rejected by validation, args={args.Length})");
            }
        }
        catch (Exception ex)
        {
            foreach (var marker in ArrayMethodMarkers)
            {
                if (ex.Message.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    sawArrayMethodError = true;
            }

            var errorKey = ex.Message.Length > 60 ? ex.Message[..60] : ex.Message;
            if (errorKey == lastErrorKey)
            {
                repeatCount++;
            }
            else
            {
                lastErrorKey = errorKey;
                repeatCount = 1;
                Log.Debug($"[NToken] Sieve call error for '{funcName}' " +
                          $"(args={args.Length}): {Truncate(ex.Message, 120)}");
            }
        }
        return null;
    }

    private readonly record struct WrapperCandidate(string Name, string Body, int ParamCount, int Position);

    private static List<WrapperCandidate> FindAllShortWrappers(string baseJs, string coreName)
    {
        var candidates = new List<WrapperCandidate>(16);
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var span = baseJs.AsSpan();
        var coreSpan = coreName.AsSpan();

        // Level 1: Direct wrappers
        int searchFrom = 0;
        while (searchFrom < span.Length)
        {
            int corePos = span[searchFrom..].IndexOf(coreSpan, StringComparison.Ordinal);
            if (corePos < 0) break;
            corePos += searchFrom;
            searchFrom = corePos + coreSpan.Length;

            if (corePos > 0 && JsFunctionExtractor.IsIdentChar(span[corePos - 1])) continue;
            int afterCore = corePos + coreName.Length;
            if (afterCore < span.Length && JsFunctionExtractor.IsIdentChar(span[afterCore]) && span[afterCore] is not ('(' or '.' or '[')) continue;

            var wrapper = TryExtractEnclosingWrapper(baseJs, corePos, coreName);
            if (wrapper is null) continue;
            if (!seenNames.Add(wrapper.Value.Name)) continue;
            if (wrapper.Value.Body.Length > MaxWrapperBodyLength || wrapper.Value.ParamCount > MaxWrapperParams) continue;

            candidates.Add(wrapper.Value);
            if (candidates.Count >= MaxFuzzCandidates) break;
        }

        // Level 2: Nested wrappers (wrappers of wrappers)
        if (candidates.Count > 0 && candidates.Count < MaxFuzzCandidates)
        {
            var level2 = new List<WrapperCandidate>(8);
            foreach (var c in candidates)
            {
                foreach (var w in FindAllShortWrappersOfName(baseJs, c.Name, coreName))
                {
                    if (!seenNames.Add(w.Name)) continue;
                    if (w.ParamCount > 2) continue;
                    level2.Add(w);
                }
                if (candidates.Count + level2.Count >= MaxFuzzCandidates) break;
            }
            level2.AddRange(candidates);
            candidates = level2;
        }

        candidates.Sort((a, b) =>
        {
            int ap = a.ParamCount == 0 ? 99 : a.ParamCount;
            int bp = b.ParamCount == 0 ? 99 : b.ParamCount;
            int cmp = ap.CompareTo(bp);
            return cmp != 0 ? cmp : a.Position.CompareTo(b.Position);
        });

        return candidates;
    }

    private static List<WrapperCandidate> FindAllShortWrappersOfName(string baseJs, string targetName, string coreName)
    {
        var results = new List<WrapperCandidate>(4);
        var span = baseJs.AsSpan();
        var targetSpan = targetName.AsSpan();
        int searchFrom = 0;

        while (searchFrom < span.Length)
        {
            int pos = span[searchFrom..].IndexOf(targetSpan, StringComparison.Ordinal);
            if (pos < 0) break;
            pos += searchFrom;
            searchFrom = pos + targetSpan.Length;

            if (pos > 0 && JsFunctionExtractor.IsIdentChar(span[pos - 1])) continue;
            int after = pos + targetName.Length;
            if (after < span.Length && JsFunctionExtractor.IsIdentChar(span[after]) && span[after] is not ('(' or '.' or '[')) continue;

            var wrapper = TryExtractEnclosingWrapper(baseJs, pos, coreName);
            if (wrapper is null) continue;
            if (wrapper.Value.Name == targetName || wrapper.Value.Name == coreName) continue;
            if (wrapper.Value.Body.Length > 500 || wrapper.Value.ParamCount > 2) continue;

            results.Add(wrapper.Value);
            if (results.Count >= 8) break;
        }
        return results;
    }

    private static WrapperCandidate? TryExtractEnclosingWrapper(string baseJs, int callPos, string coreName)
    {
        var span = baseJs.AsSpan();
        int scanLimit = Math.Max(0, callPos - MaxWrapperBodyLength - 100);
        int funcKeywordPos = -1;

        for (int i = callPos - 1; i >= scanLimit; i--)
        {
            if (span[i] != 'f') continue;
            if (i + 8 > span.Length || !span.Slice(i, 8).SequenceEqual("function")) continue;
            if (i > 0 && JsFunctionExtractor.IsIdentChar(span[i - 1])) continue;
            funcKeywordPos = i;
            break;
        }
        if (funcKeywordPos < 0) return null;

        int parenStart = -1;
        for (int i = funcKeywordPos + 8; i < span.Length && i < funcKeywordPos + 50; i++)
        {
            if (span[i] == '(') { parenStart = i; break; }
            if (span[i] is not (' ' or '\t' or '*')) break;
        }
        if (parenStart < 0) return null;

        int parenEnd = JsFunctionExtractor.FindMatchingParen(baseJs, parenStart);
        if (parenEnd < 0) return null;

        int paramCount = 1;
        var paramSpan = span[(parenStart + 1)..parenEnd].Trim();
        if (paramSpan.IsEmpty) paramCount = 0;
        else foreach (char c in paramSpan) if (c == ',') paramCount++;

        int bodyBrace = -1;
        for (int i = parenEnd + 1; i < span.Length && i < parenEnd + 20; i++)
        {
            if (span[i] == '{') { bodyBrace = i; break; }
            if (span[i] is not (' ' or '\t' or '\n' or '\r')) break;
        }
        if (bodyBrace < 0) return null;

        int braceEnd = JsFunctionExtractor.FindMatchingBrace(baseJs, bodyBrace);
        if (braceEnd < 0 || braceEnd - funcKeywordPos + 1 > MaxWrapperBodyLength + 200) return null;

        var bodySpan = span[bodyBrace..(braceEnd + 1)];
        if (bodySpan.IndexOf("return", StringComparison.Ordinal) < 0) return null;

        int j = funcKeywordPos - 1;
        while (j >= 0 && span[j] is ' ' or '\t') j--;
        if (j < 0 || span[j] != '=') return null;
        j--;
        while (j >= 0 && span[j] is ' ' or '\t') j--;
        int nameEnd = j + 1;
        while (j >= 0 && (char.IsLetterOrDigit(span[j]) || span[j] is '_' or '$')) j--;
        int nameStart = j + 1;
        if (nameStart >= nameEnd) return null;

        string funcName = span[nameStart..nameEnd].ToString();
        if (funcName == coreName || funcName.Length > 20) return null;

        return new WrapperCandidate(funcName, span[funcKeywordPos..(braceEnd + 1)].ToString(), paramCount, funcKeywordPos);
    }

    private static int FindEndOfDefinition(string js, int funcKeywordPos)
    {
        var span = js.AsSpan();
        int parenStart = -1;
        for (int i = funcKeywordPos; i < span.Length && i < funcKeywordPos + 50; i++)
            if (span[i] == '(') { parenStart = i; break; }
        if (parenStart < 0) return -1;

        int parenEnd = JsFunctionExtractor.FindMatchingParen(js, parenStart);
        if (parenEnd < 0) return -1;

        int bodyBrace = -1;
        for (int i = parenEnd + 1; i < span.Length && i < parenEnd + 20; i++)
        {
            if (span[i] == '{') { bodyBrace = i; break; }
            if (span[i] is not (' ' or '\t' or '\n' or '\r')) break;
        }
        if (bodyBrace < 0) return -1;

        int braceEnd = JsFunctionExtractor.FindMatchingBrace(js, bodyBrace);
        if (braceEnd < 0) return -1;

        int end = braceEnd + 1;
        if (end < span.Length && span[end] == ';') end++;
        return end;
    }

    private static string BuildSieveRuntimeWrapper(string funcName, int paramCount, FuzzResult fr)
    {
        bool secondArgIsArray = fr.SecondArg is not null && fr.SecondArg.IsArray();
        bool secondArgIsString = fr.SecondArg is not null && fr.SecondArg.IsString();
        bool needsFirstArr = fr.UsesArrayInput;

        // Определяем JS-выражение для второго аргумента
        string secondArgExpr;
        if (secondArgIsArray) secondArgExpr = "arr2";
        else if (secondArgIsString) secondArgExpr = "n";
        else if (fr.UsedArgCount > 1 && fr.SecondArg is not null && !fr.SecondArg.IsUndefined())
            secondArgExpr = MapSecondArgToJs(fr.SecondArg);
        else secondArgExpr = "undefined";

        // Формируем список аргументов
        string firstArgExpr = needsFirstArr ? "arr" : "n";
        var argParts = new List<string>(paramCount);
        if (fr.UsedArgCount >= 1) argParts.Add(firstArgExpr);
        if (fr.UsedArgCount >= 2) argParts.Add(secondArgExpr);
        while (argParts.Count < paramCount) argParts.Add("undefined");
        var callStr = string.Join(", ", argParts);

        return $$"""
    var __lastError = '';
    window.__decryptorTransform = function(n) {
        __lastError = '';
        try {
            var f = window['{{funcName}}'];
            if (typeof f !== 'function') { __lastError = 'not a function'; return n; }
            {{(needsFirstArr ? "var arr = n.split('');" : "")}}
            {{(secondArgIsArray ? "var arr2 = n.split('');" : "")}}
            var r = f({{callStr}});
            if (typeof r === 'string' && r.length > 0 && r !== n) return r;
            if (Array.isArray(r)) { var s = r.join(''); if (s.length > 0 && s !== n) return s; }
            {{(needsFirstArr ? "var m = arr.join(''); if (m.length > 0 && m !== n) return m;" : "")}}
            {{(secondArgIsArray ? "var m2 = arr2.join(''); if (m2.length > 0 && m2 !== n) return m2;" : "")}}
            __lastError = 'no valid result';
            return n;
        } catch(e) { __lastError = 'wrapper: ' + String(e); return n; }
    };
    """;
    }

    private static string MapSecondArgToJs(JsValue secondArg)
    {
        if (!secondArg.IsNumber()) return "undefined";
        var num = (int)secondArg.AsNumber();
        int testLen = TestToken.Length;
        if (num == testLen) return "n.length";
        if (num == testLen - 1) return "n.length-1";
        if (num == testLen / 2) return "Math.floor(n.length/2)";
        if (num == testLen + 1) return "n.length+1";
        return num.ToString();
    }

    /// <summary>
    /// Оптимизированный скрипт вызова ядра.
    /// <para>
    /// КЛЮЧЕВОЕ ИЗМЕНЕНИЕ: magic args формируются как чистый массив без trailing comma.
    /// Для ядер с >3 параметрами (например rP(Z,k,N,a,T,K,y,Q,q,r)) 
    /// n-token пробуется на позициях 0 (без magic) или сразу после magic args.
    /// Array-режим передаёт split('') результат, а мутация проверяется отдельно.
    /// </para>
    /// </summary>
    private static string BuildCoreWrapperScript(string funcName, MagicNumbers magic) => $$"""
    var __lastError = '';
    window.__decryptorTransform = function(n) {
        __lastError = '';
        try {
            var f = window['{{funcName}}'];
            if (typeof f !== 'function') return n;
            
            var magicArgs = [{{magic.ToJsArgPrefix()}}];
            // Remove undefined sentinel for no-args case
            if (magicArgs.length === 1 && magicArgs[0] === undefined) magicArgs = [];
            
            // Strategy 1: string input after magic args
            try {
                var args1 = magicArgs.concat([n]);
                var r = f.apply(null, args1);
                if (typeof r === 'string' && r.length > 0 && r !== n) return r;
                if (Array.isArray(r)) { var s = r.join(''); if (s.length > 0 && s !== n) return s; }
            } catch(e1) { __lastError = 'str: ' + String(e1); }
            
            // Strategy 2: array input after magic args (for functions expecting array)
            try {
                var a = n.split('');
                var args2 = magicArgs.concat([a]);
                var r2 = f.apply(null, args2);
                if (typeof r2 === 'string' && r2.length > 0 && r2 !== n) return r2;
                if (Array.isArray(r2)) { var s2 = r2.join(''); if (s2.length > 0 && s2 !== n) return s2; }
                var mutated = a.join('');
                if (mutated.length > 0 && mutated !== n) return mutated;
            } catch(e2) { __lastError += ' | arr: ' + String(e2); }
            
            // Strategy 3: for multi-param cores, try passing token multiple times
            // (some cores expect: core(magic1, magic2, token, token, ...))
            if (magicArgs.length > 0) {
                try {
                    var args3 = magicArgs.concat([n, n]);
                    var r3 = f.apply(null, args3);
                    if (typeof r3 === 'string' && r3.length > 0 && r3 !== n) return r3;
                    if (Array.isArray(r3)) { var s3 = r3.join(''); if (s3.length > 0 && s3 !== n) return s3; }
                } catch(e3) { /* silent - just a heuristic */ }
                
                try {
                    var a4 = n.split('');
                    var args4 = magicArgs.concat([a4, a4]);
                    var r4 = f.apply(null, args4);
                    if (typeof r4 === 'string' && r4.length > 0 && r4 !== n) return r4;
                    if (Array.isArray(r4)) { var s4 = r4.join(''); if (s4.length > 0 && s4 !== n) return s4; }
                    var m4 = a4.join('');
                    if (m4.length > 0 && m4 !== n) return m4;
                } catch(e4) { /* silent */ }
            }
            
            return n;
        } catch(e) { return n; }
    };
    """;

    private static string? FindCoreFunctionName(string baseJs)
    {
        var match = TripleSelfReferenceRegex().Match(baseJs);
        if (!match.Success) return null;
        var searchStart = Math.Max(0, match.Index - 10_000);
        string? lastName = null;
        foreach (Match m in FunctionDefinitionRegex().Matches(baseJs, searchStart))
        {
            if (m.Index >= match.Index) break;
            lastName = m.Groups[1].Value;
        }
        return lastName;
    }

    [GeneratedRegex(@"(\w+)\s*\[\s*[^\]]+\s*\]\s*=\s*\1\s*;\s*\1\s*\[\s*[^\]]+\s*\]\s*=\s*\1\s*;\s*\1\s*\[\s*[^\]]+\s*\]\s*=\s*\1", RegexOptions.None)]
    private static partial Regex TripleSelfReferenceRegex();

    [GeneratedRegex(@"(?:^|[;\n}])([a-zA-Z_$][\w$]*)\s*=\s*function\s*\(", RegexOptions.Compiled)]
    private static partial Regex FunctionDefinitionRegex();
}