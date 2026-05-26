using Avalonia.Controls;

namespace LMP.UI.Features.Debug;

/// <summary>
/// Debug-окно с вкладками: Tests, YouTube, Memory, Audio.
/// Открывается по F9 в Debug-режиме.
/// </summary>
public partial class DebugWindow : Window
{
    public DebugWindow()
    {
        InitializeComponent();
        DataContext = new DebugViewModel();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        (DataContext as DebugViewModel)?.Dispose();
    }
}