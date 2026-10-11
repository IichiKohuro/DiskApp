using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;

using DiskApp.UI.Localization;
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
        FileOperationState.Queued => L.T("OperationItem.Queued"),
        FileOperationState.Running => Info.Fraction is { } fraction ? Formatting.Percent(fraction) : L.T("OperationItem.Running"),
        FileOperationState.WaitingDecision => L.T("OperationItem.WaitingForADecisionAFile"),
        FileOperationState.Completed => L.T("Common.Done"),
        FileOperationState.Failed => L.T("Common.Error"),
        _ => L.T("OperationItem.Cancelled"),
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
                parts.Add(L.F("OperationItem.Of", Formatting.Size(Info.ProcessedBytes), Formatting.Size(Info.TotalBytes)));
            }
            else if (Info.TotalItems > 0)
            {
                parts.Add(L.F("OperationItem.Of", Info.ProcessedItems, Formatting.Count(Info.TotalItems, L.T("Common.Item.Few"), L.T("Common.Item.Many"), L.T("Common.Item.Many"))));
            }

            if (Info.State == FileOperationState.Running && Info.BytesPerSecond > 0)
            {
                parts.Add(Formatting.Size((long)Info.BytesPerSecond) + L.T("OperationItem.S"));
            }

            if (Info.State == FileOperationState.Running && Info.RemainingSeconds is { } remaining)
            {
                parts.Add(L.T("OperationItem.Remaining") + Remaining(remaining));
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
        var what = count == 1 ? $"«{PathText.FileName(info.Sources[0])}»" : Formatting.Count(count, L.T("Common.Item.Few"), L.T("Common.Item.Many"), L.T("Common.Item.Many"));
        var where = info.Destination is { Length: > 0 } destination ? L.F("OperationItem.To", PathText.FileName(destination)) : string.Empty;
        return info.Kind switch
        {
            FileOperationKind.Copy => L.F("OperationItem.Copying", what, where),
            FileOperationKind.Move => L.F("OperationItem.Moving", what, where),
            FileOperationKind.Rename => L.F("OperationItem.Renaming", what),
            FileOperationKind.Delete => L.F("OperationItem.ToRecycleBin", what),
            FileOperationKind.CreateFolder => L.F("OperationItem.NewFolder", where),
            _ => L.F("OperationItem.NewFile", where),
        };
    }

    private static string Remaining(double seconds) => seconds switch
    {
        < 60 => L.F("OperationItem.S2", Math.Max(1, (int)Math.Round(seconds)).ToString(CultureInfo.CurrentCulture)),
        < 3600 => L.F("OperationItem.Min", (int)Math.Round(seconds / 60)),
        _ => L.F("OperationItem.H", (seconds / 3600).ToString("0.#", CultureInfo.CurrentCulture)),
    };
}
