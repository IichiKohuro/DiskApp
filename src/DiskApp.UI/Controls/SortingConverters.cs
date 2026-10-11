using Avalonia.Data.Converters;

using FluentIcons.Common;

namespace DiskApp.UI.Controls;

/// <summary>
/// Конвертеры страницы «Разбор» (PLAN.md, этап 17).
/// </summary>
public static class SortingConverters
{
    /// <summary>Значок узла дерева: новая папка — с плюсом.</summary>
    public static FuncValueConverter<bool, Symbol> FolderSymbol { get; } = new(static isNew => isNew ? Symbol.FolderAdd : Symbol.Folder);
}
