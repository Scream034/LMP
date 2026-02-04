namespace LMP.Core.Data.Entities;

public sealed class SettingEntity
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty; // JSON serialized
}