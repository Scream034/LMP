// Core/Data/Repositories/SettingsRepository.cs
using System.Text.Json;
using LMP.Core.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace LMP.Core.Data.Repositories;

public interface ISettingsRepository
{
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default) where T : class;
    Task<T> GetOrDefaultAsync<T>(string key, T defaultValue, CancellationToken ct = default) where T : class;
    Task SetAsync<T>(string key, T value, CancellationToken ct = default);
}

public class SettingsRepository : ISettingsRepository
{
    private readonly IDbContextFactory<LibraryDbContext> _factory;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public SettingsRepository(IDbContextFactory<LibraryDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default) where T : class
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var entity = await ctx.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        
        if (entity is null) return null;
        
        return JsonSerializer.Deserialize<T>(entity.Value, JsonOptions);
    }

    public async Task<T> GetOrDefaultAsync<T>(string key, T defaultValue, CancellationToken ct = default) where T : class
    {
        return await GetAsync<T>(key, ct) ?? defaultValue;
    }

    public async Task SetAsync<T>(string key, T value, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        
        var json = JsonSerializer.Serialize(value, JsonOptions);
        var existing = await ctx.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        
        if (existing != null)
        {
            existing.Value = json;
            ctx.Settings.Update(existing);
        }
        else
        {
            ctx.Settings.Add(new SettingEntity { Key = key, Value = json });
        }

        await ctx.SaveChangesAsync(ct);
    }
}