using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;

using DiskApp.UI.Models;
using DiskApp.UI.Text;

using FluentIcons.Common;

namespace DiskApp.UI.ViewModels;

/// <summary>
/// Строка страницы «Операции»: что делается, ход, скорость и оставшееся время, итог.
/// </summary>
public sealed partial class OperationItemViewModel : ObservableObject
{
    /// <summary>
    /// Создаёт строку.
    /// </summary>
    public OperationItemViewModel(OperationInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        Info = info;
    }

    /// <summary>Операция.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(StateText), nameof(DetailText), nameof(ProgressPercent), nameof(IsIndeterminate), nameof(IsActive), nameof(CanCancel), nameof(CanUndo), nameof(IsFailed), nameof(Symbol), nameof(ShowProgress))]
    public partial OperationInfo Info { get; set; }

    /// <summary>Что делается: «Копирование 3 элементов в «Docs»».</summary>
    public string Title => Describe(Info);

    /// <summary>Состояние словами.</summary>
    public string StateText => Info.State switch
    {
        FileOperationState.Queued => "В очереди",
        FileOperationState.Running => Info.Fraction is { } fraction ? Formatting.Percent(fraction) : "Выполняется",
        FileOperationState.WaitingDecision => "Ждёт решения: файл с таким именем уже есть",
        FileOperationState.Completed => "Готово",
        FileOperationState.Failed => "Ошибка",
        _ => "Отменена",
    };

    /// <summary>Подробности: объём, скорость, оставшееся время или сообщение об ошибке.</summary>
    public string DetailText
    {
        get
        {
            if (Info.State is FileOperationState.Failed or FileOperationState.Cancelled)
            {
                return Info.Message ?? string.Empty;
            }

            var parts = new List<string>();
            if (Info.TotalBytes > 0)
            {
                parts.Add($"{Formatting.Size(Info.ProcessedBytes)} из {Formatting.Size(Info.TotalBytes)}");
            }
            else if (Info.TotalItems > 0)
            {
                parts.Add($"{Info.ProcessedItems} из {Formatting.Count(Info.TotalItems, "элемента", "элементов", "элементов")}");
            }

            if (Info.State == FileOperationState.Running && Info.BytesPerSecond > 0)
            {
                parts.Add(Formatting.Size((long)Info.BytesPerSecond) + "/с");
            }

            if (Info.State == FileOperationState.Running && Info.RemainingSeconds is { } remaining)
            {
                parts.Add("осталось " + Remaining(remaining));
            }

            if (Info.State == FileOperationState.Running && Info.CurrentPath is { Length: > 0 } current)
            {
                parts.Add(PathText.FileName(current));
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>Доля выполнения от 0 до 100.</summary>
    public double ProgressPercent => (Info.Fraction ?? 0) * 100;

    /// <summary>Объём неизвестен: бегущий индикатор.</summary>
    public bool IsIndeterminate => Info.Fraction is null;

    /// <summary>Показывать индикатор хода.</summary>
    public bool ShowProgress => Info.State is FileOperationState.Running or FileOperationState.Queued;

    /// <summary>Операция ещё идёт.</summary>
    public bool IsActive => Info.IsActive;

    /// <summary>Можно отменить.</summary>
    public bool CanCancel => Info.IsActive;

    /// <summary>Можно отменить результат.</summary>
    public bool CanUndo => Info.CanUndo && Info.State == FileOperationState.Completed;

    /// <summary>Завершилась ошибкой.</summary>
    public bool IsFailed => Info.State == FileOperationState.Failed;

    /// <summary>Значок вида операции.</summary>
    public Symbol Symbol => Info.Kind switch
    {
        FileOperationKind.Copy => Symbol.Copy,
        FileOperationKind.Move => Symbol.ArrowMove,
        FileOperationKind.Rename => Symbol.Rename,
        FileOperationKind.Delete => Symbol.Delete,
        FileOperationKind.CreateFolder => Symbol.FolderAdd,
        _ => Symbol.DocumentAdd,
    };

    /// <summary>
    /// Описание операции словами для строки и уведомлений.
    /// </summary>
    public static string Describe(OperationInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        var count = info.Sources.Count;
        var what = count == 1 ? $"«{PathText.FileName(info.Sources[0])}»" : Formatting.Count(count, "элемента", "элементов", "элементов");
        var where = info.Destination is { Length: > 0 } destination ? $" в «{PathText.FileName(destination)}»" : string.Empty;
        return info.Kind switch
        {
            FileOperationKind.Copy => $"Копирование {what}{where}",
            FileOperationKind.Move => $"Перемещение {what}{where}",
            FileOperationKind.Rename => $"Переименование {what}",
            FileOperationKind.Delete => $"В корзину: {what}",
            FileOperationKind.CreateFolder => $"Новая папка{where}",
            _ => $"Новый файл{where}",
        };
    }

    private static string Remaining(double seconds) => seconds switch
    {
        < 60 => $"{Math.Max(1, (int)Math.Round(seconds)).ToString(CultureInfo.CurrentCulture)} с",
        < 3600 => $"{(int)Math.Round(seconds / 60)} мин",
        _ => $"{(seconds / 3600).ToString("0.#", CultureInfo.CurrentCulture)} ч",
    };
}
