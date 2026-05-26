using Avalonia.Controls;
using ReactiveUI;
using System.Reactive;

namespace LMP.UI.Dialogs.Content;

/// <summary>
/// Контент overlay-диалога ввода текста.
/// 
/// <para><b>Особенность:</b> текст считывается напрямую из TextBox
/// при подтверждении, минуя двустороннюю привязку.
/// Это проще и надёжнее чем INotifyPropertyChanged на UserControl.</para>
/// </summary>
public partial class InputDialogContent : UserControl
{
    private static LocalizationService L => LocalizationService.Instance;

    /// <summary>Заголовок диалога.</summary>
    public string Title { get; }

    /// <summary>Подсказка над полем ввода.</summary>
    public string Prompt { get; }

    /// <summary>Placeholder в поле ввода.</summary>
    public string Watermark { get; }

    /// <summary>Текст кнопки подтверждения.</summary>
    public string ConfirmText { get; }

    /// <summary>Текст кнопки отмены.</summary>
    public string CancelText { get; }

    /// <summary>Подтвердить ввод.</summary>
    public ReactiveCommand<Unit, Unit> ConfirmCommand { get; }

    /// <summary>Отменить ввод.</summary>
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    /// <summary>
    /// Конструктор для XAML-компилятора и превьюера.
    /// </summary>
    public InputDialogContent()
        : this("Input Title", "Please enter value:", "Placeholder...", _ => { })
    {
    }

    /// <param name="title">Заголовок.</param>
    /// <param name="prompt">Подсказка над полем ввода.</param>
    /// <param name="watermark">Placeholder.</param>
    /// <param name="onResult">Callback: введённый текст или <c>null</c> при отмене.</param>
    public InputDialogContent(
        string title,
        string prompt,
        string watermark,
        Action<string?> onResult)
    {
        Title = title;
        Prompt = prompt;
        Watermark = watermark;
        ConfirmText = L["Common_OK"];
        CancelText = L["Common_Cancel"];

        // Считываем текст напрямую из TextBox при подтверждении —
        // не нужен двусторонний биндинг и INotifyPropertyChanged
        ConfirmCommand = ReactiveCommand.Create(() =>
        {
            var textBox = this.FindControl<TextBox>("InputTextBox");
            onResult(textBox?.Text);
        });

        CancelCommand = ReactiveCommand.Create(() => onResult(null));

        InitializeComponent();
        DataContext = this;
    }
}