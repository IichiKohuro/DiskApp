using System.Runtime.Versioning;

namespace DiskApp.Desktop.Platform.Windows;

/// <summary>Чем закончился переход диалога в папку.</summary>
internal enum QuickSwitchResult
{
    /// <summary>Диалог получил путь и команду «ОК»: для папки это переход в неё.</summary>
    Navigated,

    /// <summary>Диалог уже закрыт.</summary>
    DialogClosed,

    /// <summary>Папки нет на диске: путь в диалог не передаётся, иначе «ОК» сохранил бы файл с таким именем.</summary>
    FolderMissing,

    /// <summary>Диалог не принял текст или команду (завис, нестандартный элемент, нет прав).</summary>
    Failed,
}

/// <summary>Найденный стандартный диалог файлов: окно и поле имени файла.</summary>
/// <param name="Window">Окно диалога класса <c>#32770</c>.</param>
/// <param name="FileNameEdit">Поле ввода имени файла.</param>
internal sealed record FileDialogTarget(nint Window, nint FileNameEdit);

/// <summary>
/// Quick Switch (PLAN.md, этап 8.6, ADR-0008): находит стандартный диалог «Сохранить» или «Открыть» Windows и переводит
/// его в папку тем же способом, что и человек, — путь в поле имени файла и «ОК». Исходное имя файла возвращается в поле.
/// Работает с диалогами <c>IFileDialog</c> (Windows Vista и новее) и старыми диалогами в стиле Проводника; программы со
/// своими диалогами не распознаются.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class FileDialogBridge
{
    /// <summary>Идентификатор поля имени файла в диалогах Проводника (<c>cmb13</c>).</summary>
    private const int FileNameComboId = 0x47C;

    /// <summary>Идентификатор поля имени файла в старых диалогах (<c>edt1</c>).</summary>
    private const int FileNameEditId = 0x480;

    private const int MaxDescendants = 4_096;
    private const uint MessageTimeoutMs = 1_000;

    /// <summary>Задержки повторной проверки имени файла: диалог может очистить поле уже после перехода.</summary>
    private static readonly TimeSpan[] RestoreChecks = [TimeSpan.Zero, TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(400)];

    /// <summary>Стандартный диалог файлов на переднем плане или <c>null</c>.</summary>
    public static FileDialogTarget? FromForeground() => FromWindow(NativeMethods.GetForegroundWindow());

    /// <summary>
    /// Распознаёт стандартный диалог файлов: класс <c>#32770</c>, внутри — список файлов Проводника и поле имени файла.
    /// Окна сообщений и другие диалоги того же класса не подходят.
    /// </summary>
    public static FileDialogTarget? FromWindow(nint window)
    {
        if (window == 0 || !NativeMethods.IsWindow(window) || NativeMethods.GetClassName(window) != "#32770")
        {
            return null;
        }

        var descendants = Descendants(window);
        var hasFileView = descendants.Exists(static child => NativeMethods.GetClassName(child) is "SHELLDLL_DefView" or "DUIViewWndClassName");
        if (!hasFileView)
        {
            return null;
        }

        var edit = FindFileNameEdit(descendants);
        return edit == 0 ? null : new FileDialogTarget(window, edit);
    }

    /// <summary>
    /// Переводит диалог в папку: путь в поле имени файла, команда «ОК» (для папки — переход), исходное имя обратно.
    /// </summary>
    public static async Task<QuickSwitchResult> NavigateAsync(FileDialogTarget dialog, string folder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        if (!NativeMethods.IsWindow(dialog.Window) || !NativeMethods.IsWindow(dialog.FileNameEdit))
        {
            return QuickSwitchResult.DialogClosed;
        }

        if (!Directory.Exists(folder))
        {
            return QuickSwitchResult.FolderMissing;
        }

        if (GetText(dialog.FileNameEdit) is not { } original || !SetText(dialog.FileNameEdit, folder))
        {
            return QuickSwitchResult.Failed;
        }

        NativeMethods.SetForegroundWindow(dialog.Window);

        // WM_COMMAND(IDOK) — то же, что щелчок по «Сохранить»: диалог разбирает поле и переходит в папку, пока
        // обрабатывает сообщение, поэтому после ответа имя файла можно вернуть, не рискуя сохранить файл в старой папке.
        var okButton = NativeMethods.GetDlgItem(dialog.Window, NativeMethods.IdOk);
        if (!Send(dialog.Window, NativeMethods.WmCommand, NativeMethods.IdOk, okButton))
        {
            SetText(dialog.FileNameEdit, original);
            return QuickSwitchResult.Failed;
        }

        foreach (var delay in RestoreChecks)
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            if (!NativeMethods.IsWindow(dialog.FileNameEdit))
            {
                // Диалог закрылся: так ведёт себя выбор папки (FOS_PICKFOLDERS) — «ОК» выбирает её.
                return QuickSwitchResult.Navigated;
            }

            if (!string.Equals(GetText(dialog.FileNameEdit), original, StringComparison.Ordinal))
            {
                SetText(dialog.FileNameEdit, original);
            }
        }

        return QuickSwitchResult.Navigated;
    }

    /// <summary>
    /// Поле имени файла: <c>Edit</c> внутри элемента <c>cmb13</c>, иначе <c>edt1</c>, иначе первое поле внутри видимого
    /// выпадающего списка.
    /// </summary>
    private static nint FindFileNameEdit(List<nint> descendants)
    {
        foreach (var child in descendants)
        {
            if (NativeMethods.GetDlgCtrlID(child) == FileNameComboId)
            {
                var edit = NativeMethods.GetClassName(child) == "Edit" ? child : FirstEdit(child);
                if (edit != 0)
                {
                    return edit;
                }
            }
        }

        foreach (var child in descendants)
        {
            if (NativeMethods.GetDlgCtrlID(child) == FileNameEditId && NativeMethods.GetClassName(child) == "Edit")
            {
                return child;
            }
        }

        foreach (var child in descendants)
        {
            if (NativeMethods.GetClassName(child) is "ComboBoxEx32" or "ComboBox" && NativeMethods.IsWindowVisible(child))
            {
                var edit = FirstEdit(child);
                if (edit != 0)
                {
                    return edit;
                }
            }
        }

        return 0;
    }

    private static nint FirstEdit(nint parent)
        => Descendants(parent).Find(static child => NativeMethods.GetClassName(child) == "Edit");

    /// <summary>Все потомки окна в порядке обхода в глубину (как у <c>EnumChildWindows</c>).</summary>
    private static List<nint> Descendants(nint window)
    {
        var result = new List<nint>();
        Collect(window, result);
        return result;

        static void Collect(nint parent, List<nint> result)
        {
            var child = NativeMethods.FindWindowExW(parent, 0, null, null);
            while (child != 0 && result.Count < MaxDescendants)
            {
                result.Add(child);
                Collect(child, result);
                child = NativeMethods.FindWindowExW(parent, child, null, null);
            }
        }
    }

    private static unsafe string? GetText(nint window)
    {
        if (NativeMethods.SendMessageTimeoutW(window, NativeMethods.WmGetTextLength, 0, 0, NativeMethods.SmtoAbortIfHung, MessageTimeoutMs, out var length) == 0)
        {
            return null;
        }

        var buffer = new char[(int)length + 1];
        fixed (char* pointer = buffer)
        {
            if (NativeMethods.SendMessageTimeoutW(window, NativeMethods.WmGetText, buffer.Length, (nint)pointer, NativeMethods.SmtoAbortIfHung, MessageTimeoutMs, out var copied) == 0)
            {
                return null;
            }

            return new string(pointer, 0, (int)Math.Min(copied, length));
        }
    }

    private static unsafe bool SetText(nint window, string text)
    {
        fixed (char* pointer = text)
        {
            return NativeMethods.SendMessageTimeoutW(window, NativeMethods.WmSetText, 0, (nint)pointer, NativeMethods.SmtoAbortIfHung, MessageTimeoutMs, out var result) != 0
                && result != 0;
        }
    }

    private static bool Send(nint window, uint message, nint wParam, nint lParam)
        => NativeMethods.SendMessageTimeoutW(window, message, wParam, lParam, NativeMethods.SmtoAbortIfHung, MessageTimeoutMs * 5, out _) != 0;
}
