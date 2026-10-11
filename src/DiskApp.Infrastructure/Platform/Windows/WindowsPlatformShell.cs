using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using DiskApp.Core.Abstractions;
using DiskApp.Core.Localization;
using DiskApp.Core.Results;

namespace DiskApp.Infrastructure.Platform.Windows;

/// <summary>
/// Действия оболочки Windows (PLAN.md, этап 7.3): открыть (<c>ShellExecuteEx</c>), «Открыть с помощью»
/// (<c>SHOpenWithDialog</c>), показать в Проводнике (<c>SHOpenFolderAndSelectItems</c>), свойства (<c>SHObjectProperties</c>),
/// запуск от имени администратора (verb <c>runas</c>), корзина (<c>SHFileOperation</c> с <c>FOF_ALLOWUNDO</c>), пути
/// и файлы в буфер обмена. Вызовы идут на отдельном STA-потоке: расширения оболочки ждут однопоточный апартамент.
/// Корзина используется только на локальных жёстких дисках: на сетевых и съёмных её нет, и Windows удалила бы файлы
/// безвозвратно — тогда действие отклоняется.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPlatformShell : IPlatformShell, IDisposable
{
    private readonly StaThread _sta = new("DiskApp shell");

    /// <inheritdoc />
    public Task<Result> OpenAsync(string path, CancellationToken cancellationToken = default)
        => RunAsync(path, () => Start(new ProcessStartInfo(path) { UseShellExecute = true }), cancellationToken);

    /// <inheritdoc />
    public Task<Result> OpenWithAsync(string path, CancellationToken cancellationToken = default)
        => RunAsync(path, () =>
        {
            var file = Marshal.StringToHGlobalUni(path);
            try
            {
                var info = new ShellNative.OpenAsInfo { File = file, Flags = ShellNative.OaifAllowRegistration | ShellNative.OaifExec };
                var result = ShellNative.ShOpenWithDialog(0, ref info);
                return result >= 0 || result == ShellNative.HresultCancelled
                    ? Result.Ok()
                    : Fail("shell.open_with_failed", L.F("WindowsPlatformShell.TheOpenWithDialogDidNot", result));
            }
            finally
            {
                Marshal.FreeHGlobal(file);
            }
        }, cancellationToken);

    /// <inheritdoc />
    public Task<Result> RevealInExplorerAsync(string path, CancellationToken cancellationToken = default)
        => RunAsync(path, () =>
        {
            if (ShellNative.ShParseDisplayName(path, 0, out var pidl, 0, out _) < 0 || pidl == 0)
            {
                return Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }

            try
            {
                var result = ShellNative.ShOpenFolderAndSelectItems(pidl, 0, 0, 0);
                return result >= 0 ? Result.Ok() : Fail("shell.reveal_failed", L.F("WindowsPlatformShell.FileExplorerDidNotOpen0x", result));
            }
            finally
            {
                ShellNative.IlFree(pidl);
            }
        }, cancellationToken);

    /// <inheritdoc />
    public Task<Result> ShowPropertiesAsync(string path, CancellationToken cancellationToken = default)
        => RunAsync(path, () => ShellNative.ShObjectProperties(0, ShellNative.ShopFilePath, path, null)
            ? Result.Ok()
            : Fail("shell.properties_failed", L.T("WindowsPlatformShell.ThePropertiesWindowDidNotOpen")), cancellationToken);

    /// <inheritdoc />
    public Task<Result> RunAsAdminAsync(string path, CancellationToken cancellationToken = default)
        => RunAsync(path, () => Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas" }), cancellationToken);

    /// <inheritdoc />
    public Task<Result> MoveToRecycleBinAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
        {
            return Task.FromResult(Result.Ok());
        }

        foreach (var path in paths)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return Task.FromResult(NotFound(path));
            }

            if (!HasRecycleBin(path))
            {
                return Task.FromResult(Fail("shell.recycle_unavailable",
                    L.F("WindowsPlatformShell.HasNoRecycleBinNetworkOr", path)));
            }
        }

        return _sta.RunAsync(() =>
        {
            var from = Marshal.StringToHGlobalUni(ShellNative.DoubleNullList(paths));
            try
            {
                // FOF_WANTNUKEWARNING: если корзина не примет файл (слишком большой), Windows спросит, а не удалит молча.
                var operation = new ShellNative.ShFileOpStruct
                {
                    Function = ShellNative.FoDelete,
                    From = from,
                    Flags = (ushort)(ShellNative.FofAllowUndo | ShellNative.FofNoConfirmation | ShellNative.FofWantNukeWarning | ShellNative.FofSilent),
                };
                var result = ShellNative.ShFileOperation(ref operation);
                return result != 0
                    ? Fail("shell.recycle_failed", L.F("WindowsPlatformShell.WindowsDidNotMoveItTo", result))
                    : operation.AnyOperationsAborted != 0
                        ? Fail("shell.cancelled", L.T("WindowsPlatformShell.MovingToTheRecycleBinWas"))
                        : Result.Ok();
            }
            finally
            {
                Marshal.FreeHGlobal(from);
            }
        }).WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result> CopyPathsToClipboardAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var text = System.Text.Encoding.Unicode.GetBytes(string.Join(Environment.NewLine, paths) + '\0');
        return _sta.RunAsync(() => SetClipboard(ShellNative.CfUnicodeText, text)).WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result> CopyFilesToClipboardAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var missing = paths.FirstOrDefault(path => !File.Exists(path) && !Directory.Exists(path));
        return missing is not null
            ? Task.FromResult(NotFound(missing))
            : _sta.RunAsync(() => SetClipboard(ShellNative.CfHdrop, ShellNative.DropFiles(paths))).WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose() => _sta.Dispose();

    /// <summary>
    /// Есть ли корзина для пути: только локальные жёсткие диски; сетевые пути и съёмные носители — нет.
    /// </summary>
    internal static bool HasRecycleBin(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            return Path.GetPathRoot(path) is { Length: > 0 } root && new DriveInfo(root) is { DriveType: DriveType.Fixed, IsReady: true };
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static Result Fail(string code, string message) => Result.Fail(new DomainError(code, message));

    private static Result NotFound(string path) => Fail("shell.not_found", L.F("WindowsPlatformShell.IsNoLongerOnDisk", path));

    private static Result Start(ProcessStartInfo start)
    {
        try
        {
            using var process = Process.Start(start);
            return Result.Ok();
        }
        catch (Win32Exception exception)
        {
            return exception.NativeErrorCode switch
            {
                1223 => Fail("shell.cancelled", L.T("WindowsPlatformShell.TheActionWasCancelled")),
                1155 => Fail("shell.no_association", L.T("WindowsPlatformShell.NoAppForThisFileType")),
                2 or 3 => Fail("shell.not_found", L.T("WindowsPlatformShell.FileNotFound")),
                _ => Fail("shell.failed", exception.Message),
            };
        }
    }

    private static Result SetClipboard(uint format, byte[] data)
    {
        for (var attempt = 0; !ShellNative.OpenClipboard(0); attempt++)
        {
            if (attempt >= 10)
            {
                return Fail("shell.clipboard_busy", L.T("WindowsPlatformShell.TheClipboardIsBusyWithAnother"));
            }

            Thread.Sleep(50);
        }

        try
        {
            ShellNative.EmptyClipboard();
            var memory = ShellNative.GlobalAlloc(ShellNative.GmemMoveable | ShellNative.GmemZeroInit, (nuint)data.Length);
            if (memory == 0)
            {
                return Fail("shell.clipboard_failed", L.T("WindowsPlatformShell.NotEnoughMemoryForTheClipboard"));
            }

            var pointer = ShellNative.GlobalLock(memory);
            Marshal.Copy(data, 0, pointer, data.Length);
            ShellNative.GlobalUnlock(memory);
            if (ShellNative.SetClipboardData(format, memory) == 0)
            {
                ShellNative.GlobalFree(memory);
                return Fail("shell.clipboard_failed", L.T("WindowsPlatformShell.WindowsDidNotAcceptTheClipboard"));
            }

            return Result.Ok();
        }
        finally
        {
            ShellNative.CloseClipboard();
        }
    }

    private Task<Result> RunAsync(string path, Func<Result> action, CancellationToken cancellationToken)
        => !File.Exists(path) && !Directory.Exists(path)
            ? Task.FromResult(NotFound(path))
            : _sta.RunAsync(action).WaitAsync(cancellationToken);
}
