#if AUDIO_TESTS

using System.Diagnostics;
using LMP.Core.Audio.Decoders;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Backends;
using LMP.Core.Audio.Sources;
using LMP.Core.Helpers;

namespace LMP.Core.Audio.Tests;

public static class AudioTests
{
    private static int _passed;
    private static int _failed;
    private static readonly List<string> _errors = [];
    
    // Путь к тестовому файлу - измените на свой
    private const string TestWebMPath = @"D:\Music\test_audio.webm";
    private const string TestM4APath = @"D:\Music\test_audio.m4a";
    
    public static async Task RunAllAsync()
    {
        _passed = 0;
        _failed = 0;
        _errors.Clear();
        
        Log.Info("╔══════════════════════════════════════════╗");
        Log.Info("║       AUDIO LIBRARY TESTS                ║");
        Log.Info("╚══════════════════════════════════════════╝");
        
        var sw = Stopwatch.StartNew();
        
        // Unit tests
        RunTest("CircularBuffer_BasicOperations", Test_CircularBuffer_BasicOperations);
        RunTest("CircularBuffer_WrapAround", Test_CircularBuffer_WrapAround);
        RunTest("OpusDecoder_Create", Test_OpusDecoder_Create);
        RunTest("OpusDecoder_DecodeEmpty_PLC", Test_OpusDecoder_DecodeEmpty_PLC);
        RunTest("AacDecoder_Create", Test_AacDecoder_Create);
        RunTest("NullBackend_BasicFlow", Test_NullBackend_BasicFlow);
        
#if AUDIO_TESTS_OFFLINE
        // Offline file tests
        await RunTestAsync("FileSource_OpenWebM", Test_FileSource_OpenWebM);
        await RunTestAsync("FileSource_ReadFrames", Test_FileSource_ReadFrames);
        await RunTestAsync("FileSource_DecodeFrames", Test_FileSource_DecodeFrames);
        await RunTestAsync("Integration_PlayFile_NullBackend", Test_Integration_PlayFile_NullBackend);
        await RunTestAsync("Integration_PlayFile_RealAudio", Test_Integration_PlayFile_RealAudio);
#endif
        
        sw.Stop();
        
        Log.Info("══════════════════════════════════════════");
        Log.Info($"Results: {_passed} passed, {_failed} failed ({sw.ElapsedMilliseconds}ms)");
        
        if (_errors.Count > 0)
        {
            Log.Error("Failures:");
            foreach (var err in _errors)
                Log.Error($"  • {err}");
        }
        
        Log.Info("══════════════════════════════════════════");
    }
    
    #region Test Runner Helpers
    
    private static void RunTest(string name, Action test)
    {
        try
        {
            test();
            _passed++;
            Log.Info($"  ✓ {name}");
        }
        catch (Exception ex)
        {
            _failed++;
            _errors.Add($"{name}: {ex.Message}");
            Log.Error($"  ✗ {name}: {ex.Message}");
        }
    }
    
    private static async Task RunTestAsync(string name, Func<Task> test)
    {
        try
        {
            await test();
            _passed++;
            Log.Info($"  ✓ {name}");
        }
        catch (Exception ex)
        {
            _failed++;
            _errors.Add($"{name}: {ex.Message}");
            Log.Error($"  ✗ {name}: {ex.Message}");
        }
    }
    
    private static void Assert(bool condition, string message = "Assertion failed")
    {
        if (!condition) throw new Exception(message);
    }
    
    private static void AssertEqual<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception(message ?? $"Expected {expected}, got {actual}");
    }
    
    #endregion
    
    #region Unit Tests
    
    private static void Test_CircularBuffer_BasicOperations()
    {
        var buffer = new CircularBuffer<float>(1024);
        
        AssertEqual(1024, buffer.Capacity);
        AssertEqual(0, buffer.Count);
        Assert(buffer.IsEmpty);
        
        Span<float> data = stackalloc float[100];
        for (int i = 0; i < 100; i++) data[i] = i;
        
        int written = buffer.Write(data);
        AssertEqual(100, written);
        AssertEqual(100, buffer.Count);
        
        Span<float> output = stackalloc float[50];
        int read = buffer.Read(output);
        AssertEqual(50, read);
        AssertEqual(0f, output[0]);
        AssertEqual(49f, output[49]);
    }
    
    private static void Test_CircularBuffer_WrapAround()
    {
        var buffer = new CircularBuffer<int>(8);
        
        Span<int> data = stackalloc int[6];
        for (int i = 0; i < 6; i++) data[i] = i;
        
        buffer.Write(data);
        
        Span<int> output = stackalloc int[4];
        buffer.Read(output);
        
        // Write more (wraps around)
        for (int i = 0; i < 4; i++) data[i] = 100 + i;
        buffer.Write(data[..4]);
        
        Span<int> all = stackalloc int[6];
        buffer.Read(all);
        
        AssertEqual(4, all[0]);
        AssertEqual(100, all[2]);
    }
    
    private static void Test_OpusDecoder_Create()
    {
        using var decoder = new OpusDecoder(48000, 2);
        
        AssertEqual(48000, decoder.SampleRate);
        AssertEqual(2, decoder.Channels);
        AssertEqual(AudioCodec.Opus, decoder.Codec);
        Assert(decoder.MaxFrameSize > 0);
    }
    
    private static void Test_OpusDecoder_DecodeEmpty_PLC()
    {
        using var decoder = new OpusDecoder(48000, 2);
        
        Span<float> output = stackalloc float[960 * 2];
        
        // Empty = Packet Loss Concealment
        int samples = decoder.Decode(ReadOnlySpan<byte>.Empty, output);
        
        Assert(samples > 0, "PLC should return samples");
    }
    
    private static void Test_AacDecoder_Create()
    {
        using var decoder = new AacDecoder(44100, 2);
        
        AssertEqual(AudioCodec.Aac, decoder.Codec);
    }
    
    private static void Test_NullBackend_BasicFlow()
    {
        using var backend = new NullAudioBackend();
        
        int callbackCount = 0;
        
        backend.Initialize(48000, 2, buffer =>
        {
            callbackCount++;
            buffer.Clear();
            return buffer.Length / 2;
        });
        
        Assert(!backend.IsPlaying);
        
        backend.Start();
        Assert(backend.IsPlaying);
        
        Thread.Sleep(100);
        
        backend.Stop();
        Assert(!backend.IsPlaying);
        Assert(callbackCount > 0, "Callback should be called");
    }
    
    #endregion
    
    #region Offline File Tests
    
#if AUDIO_TESTS_OFFLINE
    
    private static async Task Test_FileSource_OpenWebM()
    {
        if (!File.Exists(TestWebMPath))
            throw new Exception($"Test file not found: {TestWebMPath}");
        
        await using var source = new FileStreamSource(TestWebMPath);
        
        bool initialized = await source.InitializeAsync();
        Assert(initialized, "Should initialize");
        Assert(source.DurationMs > 0, "Should have duration");
        
        Log.Debug($"  [Test] Duration: {source.DurationMs}ms");
    }
    
    private static async Task Test_FileSource_ReadFrames()
    {
        if (!File.Exists(TestWebMPath))
            throw new Exception($"Test file not found: {TestWebMPath}");
        
        await using var source = new FileStreamSource(TestWebMPath);
        await source.InitializeAsync();
        
        int frameCount = 0;
        long lastTimestamp = 0;
        
        for (int i = 0; i < 100; i++)
        {
            var frame = await source.ReadFrameAsync();
            if (frame == null) break;
            
            frameCount++;
            lastTimestamp = frame.Value.TimestampMs;
            
            Assert(frame.Value.Data.Length > 0, "Frame should have data");
        }
        
        Assert(frameCount > 0, "Should read frames");
        Log.Debug($"  [Test] Read {frameCount} frames, last ts: {lastTimestamp}ms");
    }
    
    private static async Task Test_FileSource_DecodeFrames()
    {
        if (!File.Exists(TestWebMPath))
            throw new Exception($"Test file not found: {TestWebMPath}");
        
        await using var source = new FileStreamSource(TestWebMPath);
        await source.InitializeAsync();
        
        using var decoder = new OpusDecoder(48000, 2);
        Span<float> output = stackalloc float[decoder.MaxFrameSize * 2];
        
        int framesDecoded = 0;
        int totalSamples = 0;
        
        for (int i = 0; i < 50; i++)
        {
            var frame = await source.ReadFrameAsync();
            if (frame == null) break;
            
            int samples = decoder.Decode(frame.Value.Data.Span, output);
            
            if (samples > 0)
            {
                framesDecoded++;
                totalSamples += samples;
            }
        }
        
        Assert(framesDecoded > 0, "Should decode frames");
        Assert(totalSamples > 0, "Should produce samples");
        
        Log.Debug($"  [Test] Decoded {framesDecoded} frames, {totalSamples} samples");
    }
    
    private static async Task Test_Integration_PlayFile_NullBackend()
    {
        if (!File.Exists(TestWebMPath))
            throw new Exception($"Test file not found: {TestWebMPath}");
        
        // Full pipeline with NullBackend
        await using var source = new FileStreamSource(TestWebMPath);
        await source.InitializeAsync();
        
        using var decoder = new OpusDecoder(48000, 2);
        var pcmBuffer = new CircularBuffer<float>(48000 * 2 * 4);
        using var backend = new NullAudioBackend();
        
        int callbackCalls = 0;
        
        backend.Initialize(48000, 2, buffer =>
        {
            callbackCalls++;
            int read = pcmBuffer.Read(buffer);
            if (read < buffer.Length)
                buffer[read..].Clear();
            return read / 2;
        });
        
        // Decode some frames
        var decodeOutput = new float[decoder.MaxFrameSize * 2];
        
        for (int i = 0; i < 100; i++)
        {
            var frame = await source.ReadFrameAsync();
            if (frame == null) break;
            
            int samples = decoder.Decode(frame.Value.Data.Span, decodeOutput);
            if (samples > 0)
            {
                pcmBuffer.Write(decodeOutput.AsSpan(0, samples * 2));
            }
        }
        
        // Start playback
        backend.Start();
        await Task.Delay(500); // Let it run
        backend.Stop();
        
        Assert(callbackCalls > 0, "Backend should call callback");
        Log.Debug($"  [Test] Callback called {callbackCalls} times");
    }
    
    private static async Task Test_Integration_PlayFile_RealAudio()
    {
        if (!File.Exists(TestWebMPath))
            throw new Exception($"Test file not found: {TestWebMPath}");
        
        Log.Info("  [Test] Playing audio for 3 seconds...");
        
        await using var source = new FileStreamSource(TestWebMPath);
        await source.InitializeAsync();
        
        using var decoder = new OpusDecoder(48000, 2);
        var pcmBuffer = new CircularBuffer<float>(48000 * 2 * 4);
        
        IPlaybackBackend backend;
        try
        {
            backend = new MiniaudioBackend();
        }
        catch
        {
            Log.Warn("  [Test] MiniaudioBackend not available, using NullBackend");
            backend = new NullAudioBackend();
        }
        
        using (backend)
        {
            backend.Initialize(48000, 2, buffer =>
            {
                int read = pcmBuffer.Read(buffer);
                if (read < buffer.Length)
                    buffer[read..].Clear();
                return read / 2;
            });
            
            // Decode task
            var cts = new CancellationTokenSource();
            var decodeTask = Task.Run(async () =>
            {
                var output = new float[decoder.MaxFrameSize * 2];
                
                while (!cts.Token.IsCancellationRequested)
                {
                    // Wait for space
                    while (pcmBuffer.Available < output.Length && !cts.Token.IsCancellationRequested)
                        await Task.Delay(5);
                    
                    var frame = await source.ReadFrameAsync(cts.Token);
                    if (frame == null)
                    {
                        Log.Debug("  [Test] End of file");
                        break;
                    }
                    
                    int samples = decoder.Decode(frame.Value.Data.Span, output);
                    if (samples > 0)
                        pcmBuffer.Write(output.AsSpan(0, samples * 2));
                }
            });
            
            // Pre-buffer
            await Task.Delay(200);
            
            // Play
            backend.Start();
            
            // Wait 3 seconds
            await Task.Delay(3000);
            
            // Stop
            backend.Stop();
            cts.Cancel();
            
            try { await decodeTask; } catch { }
        }
        
        Log.Info("  [Test] Playback completed");
    }
    
#endif
    
    #endregion
}

#endif