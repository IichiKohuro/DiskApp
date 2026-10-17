using Avalonia.Data.Converters;

using DiskApp.UI.Models;

namespace DiskApp.UI.Controls;

/// <summary>
/// Конвертеры вида файла для разметки.
/// </summary>
public static class FileKindConverters
{
    /// <summary>Вид файла → иконка FluentIcons для заглушки.</summary>
    public static FuncValueConverter<FileKind, FluentIcons.Common.Symbol> Symbol { get; } = new(FileKindSymbols.For);

    /// <summary>Иконка — папка: для класса <c>folder</c>, который красит её жёлтым (AppStyles.axaml).</summary>
    public static FuncValueConverter<FluentIcons.Common.Symbol, bool> IsFolder { get; } = new(FileKindSymbols.IsFolder);
}
