// Core/Data/Entities/SettingEntity.cs
namespace LMP.Core.Data.Entities;

public class SettingEntity
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty; // JSON serialized
}