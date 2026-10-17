using DiskApp.Core.Domain;

namespace DiskApp.Core.Repositories;

/// <summary>Проекты (PLAN.md, этап 24, ADR-0017).</summary>
public interface IProjectRepository
{
    /// <summary>Все проекты с числом заметок.</summary>
    Task<IReadOnlyList<Project>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Проект; <c>null</c> — нет такого.</summary>
    Task<Project?> GetAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Проект с этим номером без учёта регистра; <c>null</c> — номер свободен.</summary>
    Task<Project?> FindByNumberAsync(string number, CancellationToken cancellationToken = default);

    /// <summary>Создаёт проект.</summary>
    Task<Project> AddAsync(Project project, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);

    /// <summary>Заменяет номер, название, цвет и папку; <c>null</c> — проекта нет.</summary>
    Task<Project?> UpdateAsync(Project project, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);

    /// <summary>Удаляет проект; его заметки остаются, только без проекта. Папка не трогается. <c>false</c> — проекта нет.</summary>
    Task<bool> DeleteAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Папка проекта переименована или перенесена: путь меняется у всех проектов, где он совпадает.</summary>
    Task<int> MoveFolderAsync(string oldPath, string newPath, CancellationToken cancellationToken = default);
}
