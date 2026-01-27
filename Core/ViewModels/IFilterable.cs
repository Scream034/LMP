using System.Reactive;
using LMP.Core.Models;
using LMP.Core.Services;
using ReactiveUI;

namespace LMP.Core.ViewModels;

public interface IFilterable
{
    string FilterQuery { get; set; }
    ContentFilterType FilterType { get; set; }
    ReactiveCommand<string, Unit> SetFilterTypeCommand { get; }

    LocalizationService L { get; }
}