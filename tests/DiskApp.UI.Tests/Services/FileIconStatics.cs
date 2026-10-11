namespace DiskApp.UI.Tests.Services;

/// <summary>
/// Тесты, подменяющие общий кэш иконок <c>FileIcon.Cache</c>, идут отдельно от остальных: между их ожиданиями на потоке
/// интерфейса другой тест мог бы подменить кэш.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FileIconStatics
{
    /// <summary>Имя коллекции.</summary>
    public const string Name = "FileIconStatics";
}
