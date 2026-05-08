using Avalonia.Media.Imaging;

namespace LMP.Core.Models;

/// <summary>
/// Bitmap с автоматическим подсчётом ссылок.
/// Dispose вызывается только когда refCount достигает 0.
/// 
/// Lock-free реализация: все операции используют Interlocked CAS
/// вместо lock + Interlocked (что было хуже каждого варианта по отдельности).
/// </summary>
public sealed class RefCountedBitmap : IDisposable
{
    /// <summary>
    /// -1 означает disposed. Начинаем с 1 — cache держит одну ссылку.
    /// </summary>
    private int _refCount = 1;

    public Bitmap Bitmap { get; }
    public int RefCount => Volatile.Read(ref _refCount);
    public long EstimatedBytes { get; }
    public DateTime CachedAt { get; }

    public RefCountedBitmap(Bitmap bitmap)
    {
        Bitmap = bitmap ?? throw new ArgumentNullException(nameof(bitmap));

        long pixelCount = (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height;
        EstimatedBytes = pixelCount * 4;
        CachedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Атомарно увеличивает счётчик ссылок.
    /// Возвращает false если объект уже disposed (refCount == -1).
    /// 
    /// CAS-loop: читаем текущее значение, если не disposed — пытаемся
    /// атомарно поставить +1. Повторяем при конкурентной гонке.
    /// </summary>
    public bool AddRef()
    {
        int current;
        do
        {
            current = Volatile.Read(ref _refCount);
            if (current <= 0) return false;
        }
        while (Interlocked.CompareExchange(ref _refCount, current + 1, current) != current);

        return true;
    }

    /// <summary>
    /// Атомарно уменьшает счётчик ссылок.
    /// Вызывает DisposeInternal если счётчик достиг 0.
    /// </summary>
    public void Release()
    {
        var result = Interlocked.Decrement(ref _refCount);

        if (result == 0)
        {
            // Ставим -1 чтобы AddRef в гонке вернул false
            Interlocked.Exchange(ref _refCount, -1);
            DisposeInternal();
        }
    }

    private void DisposeInternal()
    {
        try { Bitmap.Dispose(); }
        catch (Exception ex) { Log.Warn($"[RefCountedBitmap] Dispose failed: {ex.Message}"); }
    }

    public void Dispose()
    {
        // Атомарно сбрасываем в -1. Если уже -1 — уже disposed.
        if (Interlocked.Exchange(ref _refCount, -1) != -1)
            DisposeInternal();
    }
}