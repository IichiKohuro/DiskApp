using System.Diagnostics;
using System.Runtime.Versioning;

using DiskApp.Desktop.Platform.Windows;

namespace DiskApp.Desktop.Tests.Platform;

/// <summary>
/// Мост к стандартному диалогу файлов для Quick Switch (PLAN.md, этап 8.6, ADR-0008). Живой тест с настоящим диалогом
/// «Сохранить как» (WinForms <c>SaveFileDialog</c> поверх <c>IFileDialog</c> в отдельном процессе PowerShell) показывает
/// окно, поэтому включается переменной <c>DISKAPP_SHELL_TESTS=1</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FileDialogBridgeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DiskApp.QuickSwitchTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void FromWindow_NoWindowOrNotDialog_ReturnsNull()
    {
        SkipUnlessWindows();

        // Arrange
        using var window = new MessageWindow();

        // Act & Assert
        FileDialogBridge.FromWindow(0).ShouldBeNull();
        FileDialogBridge.FromWindow(window.Handle).ShouldBeNull("окно сообщений — не диалог файлов");
    }

    [Fact]
    public async Task NavigateAsync_ClosedDialog_ReportsClosed()
    {
        SkipUnlessWindows();

        // Arrange: окно сообщений создаётся и уничтожается — его описатель гарантированно недействителен.
        nint handle;
        using (var window = new MessageWindow())
        {
            handle = window.Handle;
        }

        // Act
        var result = await FileDialogBridge.NavigateAsync(new FileDialogTarget(handle, handle), Path.GetTempPath(), TestContext.Current.CancellationToken);

        // Assert
        result.ShouldBe(QuickSwitchResult.DialogClosed);
    }

    [Fact]
    public async Task NavigateAsync_RealSaveDialog_SwitchesFolderAndKeepsFileName()
    {
        SkipUnlessWindows();
        if (Environment.GetEnvironmentVariable("DISKAPP_SHELL_TESTS") != "1")
        {
            Assert.Skip("Показывает настоящий диалог «Сохранить как»: включается переменной DISKAPP_SHELL_TESTS=1.");
        }

        // Arrange
        var token = TestContext.Current.CancellationToken;
        var start = Directory.CreateDirectory(Path.Combine(_root, "Начало")).FullName;
        var target = Directory.CreateDirectory(Path.Combine(_root, "Цель")).FullName;
        var title = "DiskApp Quick Switch test " + Guid.NewGuid().ToString("N");
        using var process = StartSaveDialog(start, "note.txt", title);
        try
        {
            var dialog = await WaitForDialogAsync(title, token);

            // Act
            var missing = await FileDialogBridge.NavigateAsync(dialog, Path.Combine(_root, "нет такой"), token);
            var result = await FileDialogBridge.NavigateAsync(dialog, target, token);

            // Даём диалогу дойти до новой папки и жмём «Сохранить», как человек: имя файла должно остаться прежним.
            await Task.Delay(TimeSpan.FromSeconds(1.5), token);
            NativeMethods.SendMessageTimeoutW(dialog.Window, NativeMethods.WmCommand, NativeMethods.IdOk, 0, NativeMethods.SmtoAbortIfHung, 5_000, out _);
            var output = await process.StandardOutput.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);

            // Assert
            missing.ShouldBe(QuickSwitchResult.FolderMissing);
            result.ShouldBe(QuickSwitchResult.Navigated);
            output.Trim().ShouldBe(Path.Combine(target, "note.txt"));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Временная папка теста.
        }
    }

    private static void SkipUnlessWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Диалоги файлов есть только в Windows.");
        }
    }

    private static Process StartSaveDialog(string folder, string fileName, string title)
    {
        // Пути передаются через переменные окружения: так не нужно экранировать кавычки и кириллицу в командной строке.
        const string Script = """
            [Console]::OutputEncoding = [Text.Encoding]::UTF8
            Add-Type -AssemblyName System.Windows.Forms
            $dialog = New-Object System.Windows.Forms.SaveFileDialog
            $dialog.InitialDirectory = $env:DISKAPP_TEST_FOLDER
            $dialog.FileName = $env:DISKAPP_TEST_FILE
            $dialog.Title = $env:DISKAPP_TEST_TITLE
            $dialog.OverwritePrompt = $false
            if ($dialog.ShowDialog() -eq 'OK') { $dialog.FileName } else { 'CANCEL' }
            """;
        var info = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-STA");
        info.ArgumentList.Add("-Command");
        info.ArgumentList.Add(Script);
        info.Environment["DISKAPP_TEST_FOLDER"] = folder;
        info.Environment["DISKAPP_TEST_FILE"] = fileName;
        info.Environment["DISKAPP_TEST_TITLE"] = title;
        return Process.Start(info) ?? throw new InvalidOperationException("PowerShell не запустился.");
    }

    private static async Task<FileDialogTarget> WaitForDialogAsync(string title, CancellationToken token)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var window = NativeMethods.FindWindowExW(0, 0, "#32770", title);
            if (window != 0 && FileDialogBridge.FromWindow(window) is { } dialog)
            {
                return dialog;
            }

            await Task.Delay(200, token);
        }

        throw new TimeoutException("Диалог «Сохранить как» не появился за 20 секунд.");
    }
}
