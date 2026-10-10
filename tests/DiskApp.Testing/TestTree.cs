using System.Diagnostics;
using System.Globalization;

namespace DiskApp.Testing;

/// <summary>
/// Генератор тестовых деревьев внутри <see cref="TemporaryDirectory"/> (PLAN.md, этап 2.6): файлы по папкам заданной
/// глубины, junction (в том числе цикл на предка), папка без доступа, путь длиннее 260 символов, Unicode-имена.
/// Все пути относительные, с разделителем текущей ОС.
/// </summary>
public static class TestTree
{
    private static readonly string[] Extensions = ["txt", "pdf", "jpg", "docx", "cs", "zip", "mp4", "xlsx"];

    /// <summary>
    /// Создаёт дерево: <paramref name="folders"/> папок верхнего уровня, в каждой цепочка вложенных папок глубиной
    /// <paramref name="depth"/>, файлы равномерно по всем папкам. Возвращает относительные пути всех созданных записей
    /// (папок и файлов).
    /// </summary>
    public static IReadOnlyList<string> Generate(TemporaryDirectory directory, int files, int folders = 4, int depth = 3, string prefix = "")
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentOutOfRangeException.ThrowIfNegative(files);
        ArgumentOutOfRangeException.ThrowIfLessThan(folders, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(depth, 1);

        var created = new List<string>();
        var leafFolders = new List<string>();
        for (var folder = 0; folder < folders; folder++)
        {
            var path = string.Empty;
            for (var level = 0; level < depth; level++)
            {
                var name = level == 0
                    ? string.Create(CultureInfo.InvariantCulture, $"{prefix}folder{folder:000}")
                    : string.Create(CultureInfo.InvariantCulture, $"level{level}");
                path = path.Length == 0 ? name : Path.Combine(path, name);
                directory.CreateDirectory(path);
                created.Add(path);
                leafFolders.Add(path);
            }
        }

        for (var file = 0; file < files; file++)
        {
            var folder = leafFolders[file % leafFolders.Count];
            var name = string.Create(CultureInfo.InvariantCulture, $"file{file:000000}.{Extensions[file % Extensions.Length]}");
            var relPath = Path.Combine(folder, name);
            directory.CreateFile(relPath, name);
            created.Add(relPath);
        }

        return created;
    }

    /// <summary>
    /// Папка с файлом, полный путь к которому длиннее 260 символов. Возвращает относительный путь файла.
    /// </summary>
    public static string CreateLongPath(TemporaryDirectory directory, string top = "long")
    {
        ArgumentNullException.ThrowIfNull(directory);

        var path = top;
        while (Path.Combine(directory.FullPath, path).Length < 300)
        {
            path = Path.Combine(path, "очень-длинное-имя-папки-для-проверки");
        }

        var file = Path.Combine(path, "в-конце.txt");
        directory.CreateFile(file, "long");
        return file;
    }

    /// <summary>
    /// Файлы с Unicode-именами: кириллица, эмодзи, иероглифы, пробелы. Возвращает относительные пути.
    /// </summary>
    public static IReadOnlyList<string> CreateUnicodeNames(TemporaryDirectory directory, string folder = "юникод")
    {
        ArgumentNullException.ThrowIfNull(directory);

        string[] names = ["Отчёт за квартал.docx", "фото 🎉 праздник.jpg", "文件.txt", "Ελληνικά.pdf", "  пробелы вокруг .txt"];
        directory.CreateDirectory(folder);
        return names.Select(name => directory.CreateFile(Path.Combine(folder, name.Trim()), name))
            .Select(full => Path.GetRelativePath(directory.FullPath, full))
            .ToArray();
    }

    /// <summary>
    /// Создаёт junction <paramref name="linkRelPath"/> на папку <paramref name="targetRelPath"/> (Windows, без прав администратора).
    /// Возвращает <c>false</c>, если ОС не поддерживает junction или команда не удалась.
    /// </summary>
    public static bool TryCreateJunction(TemporaryDirectory directory, string linkRelPath, string targetRelPath)
    {
        ArgumentNullException.ThrowIfNull(directory);

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var link = directory.Resolve(linkRelPath);
        var target = directory.Resolve(targetRelPath);
        return Run("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") && Directory.Exists(link);
    }

    /// <summary>
    /// Запрещает всем чтение папки (Windows, <c>icacls</c>); освобождение результата возвращает доступ,
    /// чтобы <see cref="TemporaryDirectory"/> смогла удалить папку. <c>null</c>, если запретить не удалось.
    /// </summary>
    public static IDisposable? DenyAccess(TemporaryDirectory directory, string relPath)
    {
        ArgumentNullException.ThrowIfNull(directory);

        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var path = directory.Resolve(relPath);
        return Run("icacls.exe", $"\"{path}\" /deny *S-1-1-0:(OI)(CI)(RX)") ? new AccessRestorer(path) : null;
    }

    private static bool Run(string fileName, string arguments)
    {
        var info = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(info);
        if (process is null)
        {
            return false;
        }

        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit(TimeSpan.FromSeconds(30));
        return process.HasExited && process.ExitCode == 0;
    }

    private sealed class AccessRestorer(string path) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Run("icacls.exe", $"\"{path}\" /remove:d *S-1-1-0");
        }
    }
}
