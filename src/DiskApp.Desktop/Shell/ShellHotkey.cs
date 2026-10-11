using System.ComponentModel;
using System.Runtime.Versioning;

using DiskApp.Desktop.Platform.Windows;
using DiskApp.UI.Localization;
using DiskApp.UI.Services;

using Serilog;

namespace DiskApp.Desktop.Shell;

/// <summary>
/// Глобальное сочетание функции оболочки — быстрого поиска, главного окна или окна Drag&amp;Drop — с запасными вариантами:
/// если выбранное занято другим приложением, берётся первое свободное из запасных (ADR-0006, ADR-0011). Запасные,
/// назначенные другим функциям DiskApp, пропускаются, чтобы не отнять у них сочетание.
/// </summary>
/// <param name="id">Идентификатор сочетания в окне сообщений.</param>
/// <param name="target">Функция.</param>
/// <param name="pressed">Действие по нажатию.</param>
/// <param name="log">Журнал.</param>
internal sealed class ShellHotkey(int id, HotkeyTarget target, Action pressed, ILogger log) : IDisposable
{
    private GlobalHotkey? _registration;

    /// <summary>Функция.</summary>
    public HotkeyTarget Target => target;

    /// <summary>Действующее сочетание; <c>null</c>, если не зарегистрировано.</summary>
    public Hotkey? Active { get; private set; }

    /// <summary>Строка настроек, по которой сочетание регистрировалось в последний раз.</summary>
    public string? Configured { get; private set; }

    /// <summary>
    /// Кандидаты по порядку: сочетание из настроек (или по умолчанию, если строка не распознана), затем запасные,
    /// кроме занятых другими функциями DiskApp.
    /// </summary>
    public static HotkeyCandidates Candidates(string? configured, HotkeyTarget target, IReadOnlyCollection<Hotkey> reserved)
    {
        ArgumentNullException.ThrowIfNull(reserved);

        var recognized = Hotkey.TryParse(configured, out var preferred);
        preferred ??= Hotkey.DefaultFor(target);
        var list = new[] { preferred }
            .Concat(Hotkey.FallbacksFor(target).Where(fallback => !reserved.Contains(fallback)))
            .Distinct()
            .ToArray();
        return new HotkeyCandidates(recognized, preferred, list);
    }

    /// <summary>
    /// Регистрирует первое свободное сочетание из кандидатов вместо прежнего.
    /// </summary>
    /// <returns>Пояснение для пользователя; пусто, если действует выбранное сочетание.</returns>
    [SupportedOSPlatform("windows")]
    public string Register(MessageWindow window, string configured, IReadOnlyCollection<Hotkey> reserved)
    {
        ArgumentNullException.ThrowIfNull(window);

        Unregister();
        Configured = configured;

        var candidates = Candidates(configured, target, reserved);
        var failures = new List<string>();
        foreach (var candidate in candidates.List)
        {
            if (HotkeyKeys.ToVirtualKey(candidate.Key) is not { } virtualKey)
            {
                failures.Add(L.F("DesktopShell.TheKeyIsNotSupported2", candidate.DisplayText));
                continue;
            }

            GlobalHotkey hotkey;
            try
            {
                hotkey = GlobalHotkey.Register(window, id, (uint)candidate.Modifiers, virtualKey);
            }
            catch (Win32Exception exception)
            {
                failures.Add($"{candidate.DisplayText} — {exception.Message}");
                log.Debug("Сочетание {Hotkey} для {Target} не зарегистрировано: {Reason}", candidate.DisplayText, target, exception.Message);
                continue;
            }

            hotkey.Pressed += (_, _) => pressed();
            _registration = hotkey;
            Active = candidate;

            var message = !candidates.Recognized
                ? L.F("DesktopShell.TheShortcutInSettingsIsNot", configured, candidate.DisplayText)
                : candidate == candidates.Preferred
                    ? string.Empty
                    : L.F("DesktopShell.IsTakenByAnotherAppSo", candidates.Preferred.DisplayText, candidate.DisplayText);
            if (message.Length > 0)
            {
                log.Warning("Запасное сочетание для {Target}: {Message}", target, message);
            }

            return message;
        }

        var failed = L.T("DesktopShell.NoShortcutCouldBeRegistered") + string.Join("; ", failures) + L.T(target switch
        {
            HotkeyTarget.MainWindow => "DesktopShell.MainWindowOpensWithDoubleClick",
            HotkeyTarget.DropWindow => "DesktopShell.DropWindowOpensWithClick",
            _ => "DesktopShell.SearchOpensFromTrayMenu",
        });
        log.Warning("Горячая клавиша для {Target} недоступна: {Message}", target, failed);
        return failed;
    }

    /// <inheritdoc />
    public void Dispose() => Unregister();

    private void Unregister()
    {
        // Сочетание регистрируется только в Windows — там его и снимать.
        if (OperatingSystem.IsWindows())
        {
            _registration?.Dispose();
        }

        _registration = null;
        Active = null;
    }
}

/// <summary>
/// Кандидаты на регистрацию сочетания.
/// </summary>
/// <param name="Recognized">Строка настроек распознана.</param>
/// <param name="Preferred">Выбранное сочетание (из настроек или по умолчанию).</param>
/// <param name="List">Кандидаты по порядку, первым — выбранное.</param>
internal sealed record HotkeyCandidates(bool Recognized, Hotkey Preferred, IReadOnlyList<Hotkey> List);
