using System.Text;
using Jint;

namespace LMP.Core.Youtube.Bridge.Common;

public abstract class JsDecryptorBase<T>(
    PlayerContextManager playerManager,
    string cacheFilePath,
    int maxMemory,
    int maxDisk) : IYoutubeDecryptor, IDisposable
{
    protected readonly PlayerContextManager PlayerManager = playerManager;
    protected readonly DecryptorCache<string, string> Cache = new(cacheFilePath, maxMemory, maxDisk);

    private readonly SemaphoreSlim _initSemaphore = new(1, 1);
    private readonly object _engineLock = new();

    protected Engine? FullEngine;
    protected string? FullFuncName;

    protected string? CurrentPlayerVersion;
    internal volatile bool IsInitialized;

    protected string DecryptorName => typeof(T).Name;
    protected string DiagFolder => Cache.CacheFolder;

     public async ValueTask EnsureInitializedAsync(CancellationToken ct)
    {
        if (IsInitialized) return;

        await _initSemaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsInitialized) return;

            var context = await PlayerManager.GetOrLoadAsync(ct).ConfigureAwait(false);
            CurrentPlayerVersion = context.Version;
            await Cache.LoadAsync(context.Version).ConfigureAwait(false);

            try
            {
                InitializeCore(context);
                IsInitialized = true;
            }
            catch (Jint.Runtime.JavaScriptException ex) // ПЕРЕХВАТЫВАЕМ JS ИСКЛЮЧЕНИЯ JINT С РАСШИРЕННЫМ ДАМПОМ ОШИБКИ
            {
                Log.Error($"[{DecryptorName}] Critical Jint JS Exception inside base.js: {ex.Message}");
                Log.Error($"  └─ File Location: Line {ex.Location.Start.Line}, Col {ex.Location.Start.Column}");
                Log.Error($"  └─ Call Stack:\n{ex.JavaScriptStackTrace}");
                throw;
            }
            catch (Exception ex)
            {
                Log.Error($"[{DecryptorName}] Initialization failed: {ex.Message}");
                throw;
            }
        }
        finally
        {
            _initSemaphore.Release();
        }
    }

    protected abstract void InitializeCore(PlayerContext context);

    protected static Engine CreateEngine(TimeSpan timeout, int maxStatements) =>
        new(opt => opt.TimeoutInterval(timeout).LimitRecursion(200).MaxStatements(maxStatements));

    protected static Engine CreateFullEngine() => CreateEngine(TimeSpan.FromSeconds(30), 10_000_000);

    protected void ForceDisposeEngines()
    {
        lock (_engineLock)
        {
            FullEngine?.Dispose();
            FullEngine = null;
            FullFuncName = null;
        }
    }

    protected static string FormatJsException(Exception ex)
    {
        if (ex is not Jint.Runtime.JavaScriptException jsEx) return ex.Message;
        var sb = new StringBuilder(256).Append(jsEx.Message);
        try { var errorObj = jsEx.Error; if (errorObj is not null && !errorObj.IsUndefined() && !errorObj.IsNull()) sb.Append($" [JSError: {Truncate(errorObj.ToString(), 100)}]"); } catch { /* ignore */ }
        try { var jsStack = jsEx.JavaScriptStackTrace; if (!string.IsNullOrEmpty(jsStack)) sb.Append($" [JSStack: {Truncate(jsStack, 150)}]"); } catch { /* ignore */ }
        try { var loc = jsEx.Location; if (loc.Start.Line > 0) sb.Append($" [Line:{loc.Start.Line}:{loc.Start.Column}]"); } catch { /* ignore */ }
        return sb.ToString();
    }

    protected string? TryInvokeJs(string input, string logPrefix)
    {
        if (FullEngine is not null && FullFuncName is not null)
        {
            lock (_engineLock)
            {
                try
                {
                    var result = FullEngine.Invoke(FullFuncName, input).AsString();
                    if (!string.IsNullOrEmpty(result) && result != input)
                    {
                        Cache.Set(input, result);
                        Log.Debug($"[{DecryptorName}] {logPrefix} Full: {Truncate(input)} -> {Truncate(result)}");
                        return result;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"[{DecryptorName}] {logPrefix} Full JS failed: {FormatJsException(ex)}");
                }
            }
        }
        return null;
    }

    protected static string Truncate(string s, int len = 20) => s.Length <= len ? s : string.Concat(s.AsSpan(0, len), "...");

    public void FlushCache() => Cache.SaveAsync().GetAwaiter().GetResult();

    public void InvalidateCache()
    {
        Cache.Clear();
        ForceDisposeEngines();
        IsInitialized = false;
        Log.Info($"[{DecryptorName}] Cache invalidated");
    }

    public virtual void Dispose()
    {
        FlushCache();
        ForceDisposeEngines();
        _initSemaphore.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Полный набор заглушек браузерного API с полифилом класса URL.
    /// Исключает любые сбои песочницы из-за проверок типов.
    /// </summary>
    public const string BrowserStubs = """
        if (typeof globalThis.XMLHttpRequest === "undefined") {
            globalThis.XMLHttpRequest = { prototype: {} };
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
            defineGlobal('document', Object.create(null));
            defineGlobal('navigator', Object.create(null));
        })();

        if (typeof BigInt === 'undefined') { 
            var BigInt = function(v) { return Number(v); }; 
            BigInt.asIntN = function(bits, n) { return Number(n); }; 
            BigInt.asUintN = function(bits, n) { return Number(n); }; 
            globalThis.BigInt = BigInt; 
        }
        """;
}