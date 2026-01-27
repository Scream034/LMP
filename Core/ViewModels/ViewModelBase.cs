using LMP.Core.Services;
using ReactiveUI;

namespace LMP.Core.ViewModels;

public abstract class ViewModelBase : ReactiveObject
{
    // Статическое для кода
    public static LocalizationService SL => LocalizationService.Instance;
    
    // Нестатическое для XAML биндинга (через DataContext)
    public LocalizationService L => LocalizationService.Instance;
}