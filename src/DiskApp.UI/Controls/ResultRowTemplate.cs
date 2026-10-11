using Avalonia.Controls;
using Avalonia.Controls.Templates;

using DiskApp.UI.Models;

namespace DiskApp.UI.Controls;

/// <summary>
/// Шаблон строки списка результатов: файл (<see cref="SearchHit"/>) или заголовок секции (<see cref="ResultSectionHeader"/>).
/// Не переиспользует элементы между строками разных типов: при прокрутке строка-файл не станет заголовком.
/// </summary>
public sealed class ResultRowTemplate : IDataTemplate
{
    /// <summary>Шаблон строки-файла.</summary>
    public IDataTemplate? Hit { get; set; }

    /// <summary>Шаблон заголовка секции.</summary>
    public IDataTemplate? Header { get; set; }

    /// <inheritdoc />
    public Control? Build(object? param) => param is ResultSectionHeader ? Header?.Build(param) : Hit?.Build(param);

    /// <inheritdoc />
    public bool Match(object? data) => data is SearchHit or ResultSectionHeader;
}
