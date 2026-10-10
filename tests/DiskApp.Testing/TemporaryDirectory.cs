namespace DiskApp.Testing;

/// <summary>
/// Уникальная временная папка для одного теста: создаётся в конструкторе внутри <c>%TEMP%\DiskApp.Tests</c>,
/// в <see cref="Dispose"/> удаляется целиком вместе с содержимым, включая файлы «только для чтения».
/// </summary>
/// <remarks>
/// Относительные пути разрешаются только внутри папки: абсолютный путь или выход через <c>..</c> отклоняются,
/// чтобы тест не мог случайно изменить или удалить что-то за пределами своей песочницы.
/// </remarks>
public sealed class TemporaryDirectory : IDisposable
{
    private const string RootFolderName = "DiskApp.Tests";
    private const int DeleteAttempts = 5;
    private static readonly TimeSpan DeleteRetryDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Создаёт пустую папку <c>%TEMP%\DiskApp.Tests\&lt;prefix&gt;-&lt;guid&gt;</c>.
    /// </summary>
    /// <param name="prefix">Понятный человеку префикс имени папки; по умолчанию <c>test</c>.</param>
    public TemporaryDirectory(string prefix = "test")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        FullPath = Path.Combine(Path.GetTempPath(), RootFolderName, $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(FullPath);
    }

    /// <summary>
    /// Абсолютный путь к папке без завершающего разделителя.
    /// </summary>
    public string FullPath { get; }

    /// <summary>
    /// Абсолютный путь внутри папки по относительному. Ничего не создаёт.
    /// </summary>
    /// <param name="relativePath">Путь относительно папки; <c>.</c> означает саму папку.</param>
    /// <exception cref="ArgumentException">Путь абсолютный или выходит за пределы папки.</exception>
    public string Resolve(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        if (Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException($"Ожидался путь относительно временной папки, получен «{relativePath}».", nameof(relativePath));
        }

        var fullPath = Path.GetFullPath(Path.Combine(FullPath, relativePath));
        var isInside = fullPath.Equals(FullPath, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(FullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        return isInside
            ? fullPath
            : throw new ArgumentException($"Путь «{relativePath}» выходит за пределы временной папки «{FullPath}».", nameof(relativePath));
    }

    /// <summary>
    /// Создаёт папку (вместе с промежуточными) по относительному пути и возвращает абсолютный.
    /// </summary>
    public string CreateDirectory(string relativePath)
    {
        var fullPath = Resolve(relativePath);
        Directory.CreateDirectory(fullPath);
        return fullPath;
    }

    /// <summary>
    /// Создаёт текстовый файл в UTF-8 по относительному пути, при необходимости — промежуточные папки.
    /// Существующий файл перезаписывается.
    /// </summary>
    public string CreateFile(string relativePath, string content = "")
    {
        ArgumentNullException.ThrowIfNull(content);

        var fullPath = Resolve(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    /// <summary>
    /// Создаёт двоичный файл по относительному пути, при необходимости — промежуточные папки.
    /// Существующий файл перезаписывается.
    /// </summary>
    public string CreateFile(string relativePath, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var fullPath = Resolve(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, content);
        return fullPath;
    }

    /// <summary>
    /// Удаляет папку со всем содержимым. Снимает атрибут «только для чтения» и повторяет попытку,
    /// если файл ещё удерживается (антивирус, индексатор, не закрытый наблюдатель). Повторный вызов безопасен.
    /// </summary>
    /// <exception cref="IOException">Папку не удалось удалить за несколько попыток.</exception>
    public void Dispose()
    {
        for (var attempt = 1; Directory.Exists(FullPath); attempt++)
        {
            try
            {
                ClearReadOnlyAttributes();
                Directory.Delete(FullPath, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt >= DeleteAttempts)
                {
                    throw new IOException($"Не удалось удалить временную папку «{FullPath}» за {DeleteAttempts} попыток.", exception);
                }

                Thread.Sleep(DeleteRetryDelay);
            }
        }
    }

    /// <inheritdoc />
    public override string ToString() => FullPath;

    private void ClearReadOnlyAttributes()
    {
        foreach (var entry in new DirectoryInfo(FullPath).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReadOnly))
            {
                entry.Attributes &= ~FileAttributes.ReadOnly;
            }
        }
    }
}
