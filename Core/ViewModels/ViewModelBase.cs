using MyLiteMusicPlayer.Core.Services;
using ReactiveUI;

namespace MyLiteMusicPlayer.Core.ViewModels;

public abstract class ViewModelBase : ReactiveObject
{
    public static LocalizationService L => LocalizationService.Instance;
}

