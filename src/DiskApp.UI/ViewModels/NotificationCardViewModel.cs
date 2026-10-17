using CommunityToolkit.Mvvm.Input;

using DiskApp.UI.Services;

using FluentAvalonia.UI.Controls;

using FluentIcons.Common;

namespace DiskApp.UI.ViewModels;

/// <summary>Кнопка карточки уведомления.</summary>
/// <param name="Text">Подпись.</param>
/// <param name="Command">Выполнить и закрыть карточку.</param>
public sealed record NotificationCardAction(string Text, IRelayCommand Command);

/// <summary>
/// Тихая карточка уведомления у области уведомлений (PLAN.md, этап 21.1, ADR-0013): заголовок, текст, до трёх кнопок
/// и «Закрыть». Окно оболочки показывает её без звука и фокуса и закрывает через <see cref="AutoCloseAfter"/>, пока
/// над ней нет указателя.
/// </summary>
public sealed class NotificationCardViewModel
{
    /// <summary>Сколько кнопок помещается в карточку: напоминанию нужны «Разобрать», «Позже» и «Сегодня не напоминать».</summary>
    public const int MaxActions = 3;

    /// <summary>Создаёт карточку.</summary>
    public NotificationCardViewModel(Notification notification)
    {
        Notification = notification ?? throw new ArgumentNullException(nameof(notification));
        Actions = [.. notification.Actions.Take(MaxActions).Select(action => new NotificationCardAction(action.Text, new RelayCommand(() =>
        {
            Close();
            action.Run();
        })))];
        CloseCommand = new RelayCommand(Close);
    }

    /// <summary>Карточку нужно закрыть: нажата кнопка или «Закрыть».</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Уведомление.</summary>
    public Notification Notification { get; }

    /// <summary>Заголовок.</summary>
    public string Title => Notification.Title;

    /// <summary>Текст.</summary>
    public string Text => Notification.Text;

    /// <summary>Есть текст.</summary>
    public bool HasText => Notification.Text.Length > 0;

    /// <summary>Кнопки.</summary>
    public IReadOnlyList<NotificationCardAction> Actions { get; }

    /// <summary>Есть кнопки.</summary>
    public bool HasActions => Actions.Count > 0;

    /// <summary>Закрыть.</summary>
    public IRelayCommand CloseCommand { get; }

    /// <summary>Через сколько карточка закрывается сама: срочная висит дольше.</summary>
    public TimeSpan AutoCloseAfter => Notification.Importance == NotificationImportance.Urgent ? TimeSpan.FromSeconds(15) : TimeSpan.FromSeconds(8);

    /// <summary>Значок по виду: успех, предупреждение, ошибка, сведения.</summary>
    public Symbol Symbol => Notification.Severity switch
    {
        FAInfoBarSeverity.Success => Symbol.CheckmarkCircle,
        FAInfoBarSeverity.Warning => Symbol.Warning,
        FAInfoBarSeverity.Error => Symbol.ErrorCircle,
        _ => Symbol.Info,
    };

    /// <summary>Ошибка: значок красный.</summary>
    public bool IsError => Notification.Severity == FAInfoBarSeverity.Error;

    /// <summary>Предупреждение: значок жёлтый.</summary>
    public bool IsWarning => Notification.Severity == FAInfoBarSeverity.Warning;

    /// <summary>Успех: значок зелёный.</summary>
    public bool IsSuccess => Notification.Severity == FAInfoBarSeverity.Success;

    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
