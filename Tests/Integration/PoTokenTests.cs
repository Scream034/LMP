using System.Diagnostics;
using LMP.Core.Audio.Http;
using LMP.Core.Youtube.Bridge.Common;
using LMP.Core.Youtube.Bridge.PoToken;
using LMP.Tests.Framework;

namespace LMP.Tests.Integration;

public static class PoTokenTests
{
    //  Шаг 1: WAA Challenge 

    [TestMethod(TestCategory.Integration, "PoToken: Step 1 — WAA Challenge",
     Group = "PoToken", Order = 200, RequiresNetwork = true, TimeoutSeconds = 30)]
    public static async Task TestWaaChallengeAsync()
    {
        var sw = Stopwatch.StartNew();
        var challenge = await WaaClient.FetchChallengeAsync(SharedHttpClient.Instance)
            .ConfigureAwait(false);
        sw.Stop();

        Assert(challenge is not null, "FetchChallengeAsync returned null");
        Assert(!string.IsNullOrEmpty(challenge!.Program), "Challenge.Program is empty");
        Assert(!string.IsNullOrEmpty(challenge.GlobalName), "Challenge.GlobalName is empty");
        Assert(challenge.HasScript,
            $"Challenge has neither ScriptUrl nor InlineScript. " +
            $"Program={challenge.Program.Length}ch, GlobalName='{challenge.GlobalName}'");

        Log.Info($"[PoToken] Challenge OK in {sw.ElapsedMilliseconds}ms:");
        Log.Info($"  GlobalName  = '{challenge.GlobalName}'");
        Log.Info($"  Program     = {challenge.Program.Length} chars");
        Log.Info($"  Script      = {(challenge.InlineScript != null ? $"INLINE ({challenge.InlineScript.Length / 1024}KB)" : $"URL: {challenge.ScriptUrl}")}");
        Log.Info($"  Hash        = '{challenge.InterpreterHash}'");
    }

    //  Шаг 2: bg.js Load 

    [TestMethod(TestCategory.Integration, "PoToken: Step 2 — bg.js Load",
        Group = "PoToken", Order = 201, RequiresNetwork = true, TimeoutSeconds = 30)]
    public static async Task TestBgJsDownloadAsync()
    {
        var challenge = await WaaClient.FetchChallengeAsync(SharedHttpClient.Instance)
            .ConfigureAwait(false);

        Assert(challenge is not null, "Challenge fetch failed");
        Assert(challenge!.HasScript, "Challenge has neither ScriptUrl nor InlineScript");

        var script = await ResolveBgScriptAsync(challenge);

        Assert(!string.IsNullOrEmpty(script), "bg.js script is empty");
        Assert(script.Length > 10_000, $"bg.js suspiciously small: {script.Length} bytes");

        Log.Info($"[PoToken] bg.js OK: {script.Length / 1024}KB");
    }

    //  Шаг 3: QuickJS Context + Snapshot (async) 

    [TestMethod(TestCategory.Integration, "PoToken: Step 3 — QuickJS Snapshot",
     Group = "PoToken", Order = 202, RequiresNetwork = true, TimeoutSeconds = 60)]
    public static async Task TestQuickJsSnapshotAsync()
    {
        var challenge = await WaaClient.FetchChallengeAsync(SharedHttpClient.Instance)
            .ConfigureAwait(false);
        Assert(challenge is not null, "Challenge fetch failed");

        var bgScript = await ResolveBgScriptAsync(challenge!);
        Assert(!string.IsNullOrEmpty(bgScript), "bg.js empty");

        //  Сохраняем до запуска — если context упадёт, файлы уже есть 
        await SaveDiagnosticsAsync(bgScript, challenge!, phase: "pre-run")
            .ConfigureAwait(false);

        var swCtx = Stopwatch.StartNew();
        // 1. Создаём пустой контекст
        var handle = QuickJsNative.CreateContext("void 0;");
        swCtx.Stop();

        Assert(handle != IntPtr.Zero,
            $"QuickJsNative.CreateContext returned null in {swCtx.ElapsedMilliseconds}ms");

        Log.Info($"[PoToken] QuickJS context created in {swCtx.ElapsedMilliseconds}ms");

        try
        {
            // 2. Оцениваем bg.js нативно
            var bgEvalRes = QuickJsNative.Eval(handle, bgScript);
            Assert(bgEvalRes?.StartsWith("__err:") != true, $"bg.js load failed: {bgEvalRes}");

            // 3. Запускаем нативный бутстрап
            var bootstrapOk = QuickJsNative.InitBotGuardBootstrap(handle, challenge!.GlobalName, challenge!.Program);
            Assert(bootstrapOk, "Native InitBotGuardBootstrap failed");

            //  Сохраняем console output после создания context 
            var consoleAfterCreate = QuickJsNative.GetConsoleOutput(handle);
            await SaveDiagnosticsAsync(bgScript, challenge!, consoleOutput: consoleAfterCreate,
                phase: "post-create").ConfigureAwait(false);
            QuickJsNative.ClearConsoleOutput(handle);

            var snapshot = await RunAsyncJsFunctionAsync(handle, "_bg_doSnapshot()", "snapshot")
                .ConfigureAwait(false);

            //  Сохраняем console output после snapshot 
            var consoleAfterSnapshot = QuickJsNative.GetConsoleOutput(handle);
            await SaveDiagnosticsAsync(bgScript, challenge!, consoleOutput: consoleAfterSnapshot,
                phase: "post-snapshot").ConfigureAwait(false);
            QuickJsNative.ClearConsoleOutput(handle);

            if (snapshot is null)
            {
                var lastErr = QuickJsNative.GetLastError(handle);
                Assert(false, $"snapshot() returned null. LastError: {lastErr}");
            }

            Log.Info($"[PoToken] ✓ Snapshot SUCCESS! Length={snapshot!.Length}");

            //  Сохраняем сам snapshot 
            var dir = Path.Combine(G.Folder.Logs, "BotGuard");
            var ts = DateTime.Now.ToString("HH-mm-ss");
            await File.WriteAllTextAsync(
                Path.Combine(dir, $"{ts}_snapshot.txt"), snapshot)
                .ConfigureAwait(false);
        }
        finally
        {
            //  Финальный дамп перед уничтожением 
            var consoleFinal = QuickJsNative.GetConsoleOutput(handle);
            if (!string.IsNullOrEmpty(consoleFinal))
            {
                await SaveDiagnosticsAsync(bgScript, challenge!, consoleOutput: consoleFinal,
                    phase: "final").ConfigureAwait(false);
            }
            QuickJsNative.DestroyContext(handle);
        }
    }

    //  Шаг 4: IntegrityToken 

    [TestMethod(TestCategory.Integration, "PoToken: Step 4 — IntegrityToken",
         Group = "PoToken", Order = 203, RequiresNetwork = true, TimeoutSeconds = 60)]
    public static async Task TestIntegrityTokenAsync()
    {
        var challenge = await WaaClient.FetchChallengeAsync(SharedHttpClient.Instance)
            .ConfigureAwait(false);
        Assert(challenge is not null, "Challenge fetch failed");

        var bgScript = await ResolveBgScriptAsync(challenge!);
        Assert(!string.IsNullOrEmpty(bgScript), "bg.js empty");

        var handle = QuickJsNative.CreateContext("void 0;");
        Assert(handle != IntPtr.Zero, "QuickJS context creation failed");

        try
        {
            var bgEvalRes = QuickJsNative.Eval(handle, bgScript);
            Assert(bgEvalRes?.StartsWith("__err:") != true, "bg.js evaluation failed");

            var bootstrapOk = QuickJsNative.InitBotGuardBootstrap(handle, challenge!.GlobalName, challenge!.Program);
            Assert(bootstrapOk, "Native InitBotGuardBootstrap failed");

            // ✅ Async snapshot
            var snapshot = await RunAsyncJsFunctionAsync(handle, "_bg_doSnapshot()", "snapshot")
                .ConfigureAwait(false);

            Assert(!string.IsNullOrEmpty(snapshot),
                $"Snapshot failed. LastError: {QuickJsNative.GetLastError(handle)}");

            Log.Info($"[PoToken] Snapshot: {Truncate(snapshot, 40)}...");

            var sw = Stopwatch.StartNew();
            var integrityData = await WaaClient.GenerateIntegrityTokenAsync(
                SharedHttpClient.Instance, snapshot!).ConfigureAwait(false);
            sw.Stop();

            Assert(integrityData is not null,
                "GenerateIntegrityTokenAsync returned null — WAA/GenerateIT failed");
            Assert(!string.IsNullOrEmpty(integrityData!.IntegrityToken),
                "IntegrityToken is empty");

            Log.Info($"[PoToken] IntegrityToken in {sw.ElapsedMilliseconds}ms:");
            Log.Info($"  Token  = '{Truncate(integrityData.IntegrityToken, 30)}...'");
            Log.Info($"  TTL    = {integrityData.EstimatedTtlSecs}s");
        }
        finally
        {
            QuickJsNative.DestroyContext(handle);
        }
    }

    //  Шаг 5: Full Pipeline 

    [TestMethod(TestCategory.Integration, "PoToken: Full Pipeline",
  Group = "PoToken", Order = 204, RequiresNetwork = true, TimeoutSeconds = 90)]
    public static async Task TestFullPipelineAsync()
    {
        //  Сначала получаем challenge и сохраняем скрипты 
        var challengeForDiag = await WaaClient.FetchChallengeAsync(SharedHttpClient.Instance)
            .ConfigureAwait(false);

        if (challengeForDiag is not null)
        {
            var bgForDiag = await ResolveBgScriptAsync(challengeForDiag).ConfigureAwait(false);
            await SaveDiagnosticsAsync(bgForDiag, challengeForDiag, phase: "pipeline-pre")
                .ConfigureAwait(false);
        }

        //  Запускаем pipeline 
        using var service = new BotGuardService(SharedHttpClient.Instance);
        var visitorId = LMP.Core.Youtube.Utils.YoutubeClientUtils.VisitorData;

        Log.Info($"[PoToken] Testing full pipeline with identifier: {Truncate(visitorId, 30)}...");

        var sw = Stopwatch.StartNew();
        var token = await service.GenerateAsync(visitorId).ConfigureAwait(false);
        sw.Stop();

        if (token is null)
        {
            Log.Error("[PoToken] GenerateAsync returned null — pipeline failed");
            Log.Error("[PoToken] Check previous step tests for specific failure point");
            Assert(false, "Full pipeline returned null PoToken");
            return;
        }

        Assert(token.Length >= 100, $"PoToken suspiciously short: {token.Length} chars");
        Assert(!token.Contains('+') && !token.Contains('/') && !token.Contains('='),
            "PoToken is not Base64Url encoded");

        Log.Info($"[PoToken] ✓ FULL PIPELINE SUCCESS in {sw.ElapsedMilliseconds}ms!");
        Log.Info($"[PoToken] Token: {Truncate(token, 40)}...");
    }

    /// <summary>
    /// Проверяет что PoToken mint выдаёт токен корректной длины (110-128 bytes → ~150-172 base64url chars).
    /// Выявляет регрессию когда wpo[0] вызывается без integrityTokenBytes.
    /// </summary>
    [TestMethod(TestCategory.Integration, "PoToken: Mint Token Length Validation",
        Group = "PoToken", Order = 205, RequiresNetwork = true, TimeoutSeconds = 90)]
    public static async Task TestMintTokenLengthAsync()
    {
        using var service = new BotGuardService(SharedHttpClient.Instance);
        var videoId = "dQw4w9WgXcQ";

        Log.Info($"[PoToken] Minting content-bound token for videoId={videoId}");

        var sw = Stopwatch.StartNew();
        var token = await service.MintForVideoAsync(videoId).ConfigureAwait(false);
        sw.Stop();

        Assert(token is not null, "GenerateAsync returned null");

        Log.Info($"[PoToken] Token length: {token!.Length} chars ({sw.ElapsedMilliseconds}ms)");
        Log.Info($"[PoToken] Token prefix: {Truncate(token, 40)}...");

        // 110-128 bytes → 147-172 base64url chars (no padding)
        // Minimum safe threshold: 80 chars (~60 bytes)
        Assert(token!.Length >= 80,
            $"PoToken too short: {token.Length} chars. " +
            "Expected ≥80 (110-128 bytes base64url). " +
            "Likely cause: wpo[0] called without integrityTokenBytes.");

        Assert(token.Length <= 300,
            $"PoToken too long: {token.Length} chars. " +
            "Expected ≤300. Possibly double-encoded or wrong data.");

        // Base64url validation
        Assert(!token.Contains('+') && !token.Contains('/'),
            "Token contains standard base64 chars (+/) instead of base64url (-_)");

        Log.Info($"[PoToken] ✓ Token length OK: {token.Length} chars");
    }

    //  Shared helpers 

    /// <summary>
    /// Получает bg.js скрипт: inline (если есть) или скачивает по URL.
    /// Тесты должны использовать этот метод — НЕ обращаться к ScriptUrl напрямую.
    /// </summary>
    private static async Task<string> ResolveBgScriptAsync(BotGuardChallenge challenge)
    {
        if (!string.IsNullOrEmpty(challenge.InlineScript))
        {
            Log.Info($"[PoToken] Using INLINE bg.js ({challenge.InlineScript.Length / 1024}KB)");
            return challenge.InlineScript;
        }

        if (string.IsNullOrEmpty(challenge.ScriptUrl))
            throw new InvalidOperationException("Challenge has neither InlineScript nor ScriptUrl");

        Log.Info($"[PoToken] Downloading bg.js from {Truncate(challenge.ScriptUrl, 50)}");
        var sw = Stopwatch.StartNew();
        var script = await SharedHttpClient.Instance
            .GetStringAsync(challenge.ScriptUrl).ConfigureAwait(false);
        sw.Stop();
        Log.Info($"[PoToken] bg.js downloaded: {script.Length / 1024}KB in {sw.ElapsedMilliseconds}ms");
        return script;
    }

    /// <summary>
    /// Запускает JS async функцию через паттерн: Eval → PumpEventLoop → read result.
    /// Идентично RunAsyncJsAsync в BotGuardService, но для изолированных тестов.
    /// </summary>
    private static async Task<string?> RunAsyncJsFunctionAsync(
        IntPtr handle, string jsExpression, string operationName,
        int timeoutMs = 10_000)
    {
        // 1. Запускаем Promise
        var evalCode =
            "globalThis._test_result = null; " +
            "globalThis._test_error  = null; " +
            "globalThis._test_done   = false; " +
            $"Promise.resolve({jsExpression}).then(" +
            "  function(r) {{ globalThis._test_result = (r == null) ? '' : String(r); globalThis._test_done = true; }}," +
            "  function(e) {{ globalThis._test_error  = (e && e.message) ? e.message : String(e); globalThis._test_done = true; }}" +
            ");";

        var startResult = QuickJsNative.Eval(handle, evalCode);
        if (startResult is not null && startResult.StartsWith("__err:"))
        {
            Log.Error($"[PoToken] {operationName} eval error: {startResult[5..]}");
            return null;
        }

        // 2. Pump event loop
        var pumped = await Task.Run(() => QuickJsNative.PumpEventLoop(handle, timeoutMs))
            .ConfigureAwait(false);

        if (pumped < 0)
        {
            Log.Error($"[PoToken] {operationName} pump failed. LastError: {QuickJsNative.GetLastError(handle)}");
            return null;
        }

        // 3. Проверяем done
        var done = QuickJsNative.Eval(handle, "globalThis._test_done ? 'yes' : 'no'");
        if (done != "yes")
        {
            Log.Error($"[PoToken] {operationName} timed out after {timeoutMs}ms (pumped={pumped})");
            return null;
        }

        // 4. Читаем ошибку
        var error = QuickJsNative.Eval(handle, "globalThis._test_error");
        if (!string.IsNullOrEmpty(error))
        {
            Log.Error($"[PoToken] {operationName} JS error: {error}");
            return null;
        }

        // 5. Читаем результат
        var result = QuickJsNative.Eval(handle, "globalThis._test_result");
        if (string.IsNullOrEmpty(result))
        {
            Log.Warn($"[PoToken] {operationName} result is empty (pumped={pumped})");
        }

        return result;
    }

    /// <summary>
    /// Сохраняет файлы диагностики BotGuard в папку логов.
    /// Вызывается из тестов — не из production кода.
    /// </summary>
    private static async Task SaveDiagnosticsAsync(
          string bgScript,
          BotGuardChallenge challenge,
          string? consoleOutput = null,
          string? phase = null)
    {
        try
        {
            var dir = Path.Combine(G.Folder.Logs, "BotGuard");
            Directory.CreateDirectory(dir);

            var ts = DateTime.Now.ToString("HH-mm-ss");
            var tag = phase is not null ? $"_{phase}" : "";

            var bgPath = Path.Combine(dir, $"{ts}_bg_raw.js");
            await File.WriteAllTextAsync(bgPath, bgScript).ConfigureAwait(false);
            Log.Info($"[PoToken] Saved bg.js → {bgPath}");

            var meta = $"""
GlobalName : {challenge.GlobalName}
Program    : {challenge.Program.Length} chars
Hash       : {challenge.InterpreterHash}
ScriptUrl  : {challenge.ScriptUrl}
InlineLen  : {challenge.InlineScript?.Length ?? 0}
Program    :
{challenge.Program}
""";
            var metaPath = Path.Combine(dir, $"{ts}_challenge.txt");
            await File.WriteAllTextAsync(metaPath, meta).ConfigureAwait(false);

            if (consoleOutput is not null)
            {
                var conPath = Path.Combine(dir, $"{ts}{tag}_console.txt");
                await File.WriteAllTextAsync(conPath, consoleOutput).ConfigureAwait(false);
                Log.Info($"[PoToken] Saved console output → {conPath}");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[PoToken] SaveDiagnostics failed: {ex.Message}");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static string Truncate(string? s, int len = 20) =>
        s is null ? "null" : s.Length <= len ? s : string.Concat(s.AsSpan(0, len), "...");
}