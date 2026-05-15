using Avalonia.Media.Imaging;

namespace LMP.Core.Models;

/// <summary>
/// Обёртка над Bitmap с учётом нативной памяти для GC pressure tracking.
/// 
/// <para>Ранее содержала lock-free ref-counting (AddRef/Release),
/// но ни один потребитель не использовал ref-count паттерн —
/// кэш единолично владеет временем жизни bitmap.
/// Упрощено до single-owner Dispose.</para>
/// </summary>
public sealed class RefCountedBitmap : IDisposable
{
    private int _disposed;

    public Bitmap Bitmap { get; }
    public long EstimatedBytes { get; }
    public DateTime CachedAt { get; }

    public RefCountedBitmap(Bitmap bitmap)
    {
        Bitmap = bitmap ?? throw new ArgumentNullException(nameof(bitmap));

        long pixelCount = (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height;
        EstimatedBytes = pixelCount * 4;
        CachedAt = DateTime.UtcNow;
    }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try { Bitmap.Dispose(); }
        catch (Exception ex) { Log.Warn($"[RefCountedBitmap] Dispose failed: {ex.Message}"); }
    }
}