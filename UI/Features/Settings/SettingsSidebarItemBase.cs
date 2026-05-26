namespace LMP.UI.Features.Settings;

/// <summary>
/// Базовый маркер элемента sidebar.
/// <para>
/// Каждый наследник — отдельный тип → Avalonia выбирает DataTemplate по DataType.
/// ContentControl в зоне контента показывает страницу, соответствующую типу.
/// В каждый момент в visual tree — ОДИН sidebar item template + ОДНА страница контента.
/// </para>
/// </summary>
public abstract class SettingsSidebarItemBase(SettingsViewModel owner)
{
    /// <summary>Ссылка на VM — страницы биндятся через Owner.*</summary>
    public SettingsViewModel Owner { get; } = owner;
}

public sealed class AccountLanguageSidebarItem(SettingsViewModel owner) : SettingsSidebarItemBase(owner);
public sealed class NetworkSidebarItem(SettingsViewModel owner)         : SettingsSidebarItemBase(owner);
public sealed class StorageCacheSidebarItem(SettingsViewModel owner)    : SettingsSidebarItemBase(owner);
public sealed class MemorySidebarItem(SettingsViewModel owner)          : SettingsSidebarItemBase(owner);
public sealed class AppearanceSidebarItem(SettingsViewModel owner)      : SettingsSidebarItemBase(owner);
public sealed class AudioSidebarItem(SettingsViewModel owner)           : SettingsSidebarItemBase(owner);
public sealed class PlaybackSidebarItem(SettingsViewModel owner)        : SettingsSidebarItemBase(owner);
public sealed class WindowBehaviorSidebarItem(SettingsViewModel owner)  : SettingsSidebarItemBase(owner);
public sealed class GeneralSidebarItem(SettingsViewModel owner)         : SettingsSidebarItemBase(owner);