using MyLiteMusicPlayer.Services;
using ReactiveUI;

namespace MyLiteMusicPlayer.ViewModels;

public abstract class ViewModelBase : ReactiveObject
{
    public static LocalizationService L => LocalizationService.Instance;
}