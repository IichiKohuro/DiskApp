using System.Text.RegularExpressions;

namespace DiskApp.UI.Text;

/// <summary>Уровень записи журнала.</summary>
public enum LogSeverity
{
    /// <summary>Подробности.</summary>
    Verbose,

    /// <summary>Отладка.</summary>
    Debug,

    /// <summary>Сведения.</summary>
    Information,

    /// <summary>Предупреждение.</summary>
    Warning,

    /// <summary>Ошибка.</summary>
    Error,

    /// <summary>Сбой, после которого процесс завершается.</summary>
    Fatal,
}

/// <summary>Запись журнала: строка с уровнем и, для ошибок, стек следующими строками.</summary>
/// <param name="Text">Текст записи.</param>
/// <param name="Severity">Уровень.</param>
public sealed record LogLine(string Text, LogSeverity Severity)
{
    /// <summary>Ошибка или сбой.</summary>
    public bool IsError => Severity >= LogSeverity.Error;

    /// <summary>Предупреждение.</summary>
    public bool IsWarning => Severity == LogSeverity.Warning;

    /// <inheritdoc />
    public override string ToString() => Text;
}

/// <summary>
/// Разбор хвоста журнала Serilog (PLAN.md, этап 9.5) в формате <c>08:52:25.693 [INF] Источник: сообщение</c>. Строки
/// без отметки времени — продолжение предыдущей записи (стек исключения) и остаются с ней, чтобы фильтр по уровню не
/// отрывал стек от ошибки.
/// </summary>
public static partial class LogTail
{
    /// <summary>Записи из строк файла по порядку.</summary>
    public static IReadOnlyList<LogLine> Parse(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var entries = new List<LogLine>(lines.Count);
        foreach (var line in lines)
        {
            var match = Header().Match(line);
            if (match.Success)
            {
                entries.Add(new LogLine(line, Severity(match.Groups[1].Value)));
            }
            else if (entries.Count > 0)
            {
                var last = entries[^1];
                entries[^1] = last with { Text = last.Text + "\n" + line };
            }
            else if (line.Length > 0)
            {
                // Начало хвоста пришлось на середину стека: без уровня — как сведения.
                entries.Add(new LogLine(line, LogSeverity.Information));
            }
        }

        return entries;
    }

    /// <summary>Записи не ниже уровня.</summary>
    public static IEnumerable<LogLine> AtLeast(IEnumerable<LogLine> entries, LogSeverity minimum)
        => entries.Where(entry => entry.Severity >= minimum);

    private static LogSeverity Severity(string code) => code switch
    {
        "VRB" => LogSeverity.Verbose,
        "DBG" => LogSeverity.Debug,
        "WRN" => LogSeverity.Warning,
        "ERR" => LogSeverity.Error,
        "FTL" => LogSeverity.Fatal,
        _ => LogSeverity.Information,
    };

    [GeneratedRegex(@"^\d{2}:\d{2}:\d{2}(?:\.\d+)? \[(VRB|DBG|INF|WRN|ERR|FTL)\]", RegexOptions.CultureInvariant)]
    private static partial Regex Header();
}
