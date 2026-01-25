using System.Reactive;
using MyLiteMusicPlayer.Core.Models;
using MyLiteMusicPlayer.Core.Services;
using ReactiveUI;

namespace MyLiteMusicPlayer.Core.ViewModels;

public interface IFilterable
{
    string FilterQuery { get; set; }
    ContentFilterType FilterType { get; set; }
    ReactiveCommand<string, Unit> SetFilterTypeCommand { get; }

    LocalizationService L { get; }
}