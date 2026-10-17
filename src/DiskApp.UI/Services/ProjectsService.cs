using DiskApp.Client;
using DiskApp.Contracts.Projects;
using DiskApp.UI.Localization;

namespace DiskApp.UI.Services;

/// <summary>Проект (PLAN.md, этап 24, ADR-0017).</summary>
/// <param name="Id">Идентификатор.</param>
/// <param name="Number">Свой номер: «417», «10S-417».</param>
/// <param name="Title">Название; может быть пустым.</param>
/// <param name="Color">Цвет иконки <c>#RRGGBB</c>; <c>null</c> — цвет по номеру.</param>
/// <param name="FolderPath">Основная рабочая папка; подпапки — тоже проект.</param>
/// <param name="NoteCount">Заметок проекта.</param>
public sealed record ProjectInfo(long Id, string Number, string Title, string? Color, string? FolderPath, int NoteCount = 0)
{
    /// <summary>Номер и название: «417 · Школа на Ленина».</summary>
    public string DisplayName => Title.Length > 0 ? $"{Number} · {Title}" : Number;

    /// <summary>
    /// Проект, к которому относится папка: её путь — папка проекта или внутри неё; из вложенных — самый глубокий.
    /// </summary>
    public static ProjectInfo? ForPath(IEnumerable<ProjectInfo> projects, string? path)
    {
        ArgumentNullException.ThrowIfNull(projects);
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var target = path.TrimEnd('\\', '/');
        return projects
            .Where(project => project.FolderPath is { Length: > 0 } folder && IsInside(target, folder.TrimEnd('\\', '/')))
            .MaxBy(static project => project.FolderPath!.Length);
    }

    /// <inheritdoc />
    public override string ToString() => DisplayName;

    private static bool IsInside(string path, string folder)
        => path.Equals(folder, StringComparison.OrdinalIgnoreCase)
            || (path.Length > folder.Length && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && path[folder.Length] is '\\' or '/');
}

/// <summary>Новый проект или правка: номер, название, цвет, основная папка.</summary>
/// <param name="Number">Номер.</param>
/// <param name="Title">Название.</param>
/// <param name="Color">Цвет иконки; <c>null</c> — по номеру.</param>
/// <param name="FolderPath">Основная рабочая папка; <c>null</c> — не привязана.</param>
public sealed record ProjectInput(string Number, string Title, string? Color, string? FolderPath);

/// <summary>Проекты (PLAN.md, этап 24, ADR-0017): список по номеру, создание, правка, удаление.</summary>
public interface IProjectsService
{
    /// <summary>Проекты изменились: создан, изменён или удалён (иконки на «Разборе» и в заметках перечитываются).</summary>
    event EventHandler? Changed;

    /// <summary>Проекты по номеру: «2» раньше «10».</summary>
    Task<IReadOnlyList<ProjectInfo>> GetProjectsAsync(CancellationToken cancellationToken = default);

    /// <summary>Новый проект; занятый номер — ошибка.</summary>
    Task<ProjectInfo> CreateAsync(ProjectInput input, CancellationToken cancellationToken = default);

    /// <summary>Сохраняет номер, название, цвет и папку.</summary>
    Task<ProjectInfo> UpdateAsync(long id, ProjectInput input, CancellationToken cancellationToken = default);

    /// <summary>Удаляет проект; папка и заметки остаются, заметки — без проекта.</summary>
    Task DeleteAsync(long id, CancellationToken cancellationToken = default);
}

/// <summary>Проекты на сервере: <c>/api/v1/projects</c>.</summary>
public sealed class ApiProjectsService(IDiskAppApi api) : IProjectsService
{
    private readonly IDiskAppApi _api = api ?? throw new ArgumentNullException(nameof(api));

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProjectInfo>> GetProjectsAsync(CancellationToken cancellationToken = default)
        => [.. (await _api.GetProjectsAsync(cancellationToken).ConfigureAwait(false)).Select(ToProject)];

    /// <inheritdoc />
    public async Task<ProjectInfo> CreateAsync(ProjectInput input, CancellationToken cancellationToken = default)
    {
        var project = ToProject(await _api.CreateProjectAsync(ToRequest(input), cancellationToken).ConfigureAwait(false));
        Changed?.Invoke(this, EventArgs.Empty);
        return project;
    }

    /// <inheritdoc />
    public async Task<ProjectInfo> UpdateAsync(long id, ProjectInput input, CancellationToken cancellationToken = default)
    {
        var project = ToProject(await _api.UpdateProjectAsync(id, ToRequest(input), cancellationToken).ConfigureAwait(false));
        Changed?.Invoke(this, EventArgs.Empty);
        return project;
    }

    /// <inheritdoc />
    public async Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        await _api.DeleteProjectAsync(id, cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static ProjectRequest ToRequest(ProjectInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return new ProjectRequest { Number = input.Number.Trim(), Title = input.Title.Trim(), Color = input.Color, FolderPath = input.FolderPath };
    }

    private static ProjectInfo ToProject(ProjectDto dto) => new(dto.Id, dto.Number, dto.Title, dto.Color, dto.FolderPath, dto.NoteCount);
}

/// <summary>
/// Проекты в памяти: превью дизайнера и тесты страниц. Поведение — как у сервера: номер уникален без учёта регистра,
/// список по номеру с числами как числа.
/// </summary>
public sealed class SampleProjectsService : IProjectsService
{
    private readonly Lock _gate = new();
    private readonly List<ProjectInfo> _projects = [];
    private long _nextId = 1;

    /// <summary>Создаёт пустой сервис.</summary>
    public SampleProjectsService()
    {
    }

    /// <summary>Создаёт сервис с проектами.</summary>
    public SampleProjectsService(params ProjectInput[] projects)
    {
        ArgumentNullException.ThrowIfNull(projects);
        foreach (var project in projects)
        {
            _ = CreateAsync(project);
        }
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <summary>Сколько заметок у проекта: подставляет страница заметок в тестах; по умолчанию 0.</summary>
    public Func<long, int>? NoteCounter { get; set; }

    /// <inheritdoc />
    public Task<IReadOnlyList<ProjectInfo>> GetProjectsAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<ProjectInfo> list = [.. _projects
                .Select(project => project with { NoteCount = NoteCounter?.Invoke(project.Id) ?? 0 })
                .OrderBy(static project => project.Number, Comparer<string>.Create(ProjectNumbers.Compare))
                .ThenBy(static project => project.Id)];
            return Task.FromResult(list);
        }
    }

    /// <inheritdoc />
    public Task<ProjectInfo> CreateAsync(ProjectInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ProjectInfo project;
        lock (_gate)
        {
            EnsureFree(input.Number, null);
            project = new ProjectInfo(_nextId++, input.Number.Trim(), input.Title.Trim(), input.Color, input.FolderPath);
            _projects.Add(project);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(project);
    }

    /// <inheritdoc />
    public Task<ProjectInfo> UpdateAsync(long id, ProjectInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ProjectInfo project;
        lock (_gate)
        {
            var index = _projects.FindIndex(candidate => candidate.Id == id);
            if (index < 0)
            {
                throw new InvalidOperationException(L.F("Projects.NotFound", id));
            }

            EnsureFree(input.Number, id);
            project = _projects[index] with { Number = input.Number.Trim(), Title = input.Title.Trim(), Color = input.Color, FolderPath = input.FolderPath };
            _projects[index] = project;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(project);
    }

    /// <inheritdoc />
    public Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _projects.RemoveAll(project => project.Id == id);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    private void EnsureFree(string number, long? exceptId)
    {
        if (_projects.Any(project => project.Id != exceptId && ProjectNumbers.Same(project.Number, number)))
        {
            throw new InvalidOperationException(L.F("Projects.NumberTaken", number.Trim()));
        }
    }
}
