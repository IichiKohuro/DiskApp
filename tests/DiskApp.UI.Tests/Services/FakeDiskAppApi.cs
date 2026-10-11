using System.Reflection;

using DiskApp.Client;
using DiskApp.Contracts.Entries;
using DiskApp.Contracts.Roots;
using DiskApp.Contracts.Search;

namespace DiskApp.UI.Tests.Services;

/// <summary>
/// Подделка клиента API для сервисов интерфейса: готовые ответы для поиска и подборок, остальные методы —
/// через <see cref="Handlers"/>; незаданный метод — ошибка теста.
/// </summary>
public class FakeDiskAppApi : DispatchProxy
{
    /// <summary>Имена вызванных методов по порядку.</summary>
    public List<string> Calls { get; } = [];

    /// <summary>Ответ на поиск.</summary>
    public Func<SearchRequest, SearchResponse> Search { get; set; } = _ => throw new InvalidOperationException("Поиск не задан.");

    /// <summary>Корни.</summary>
    public IReadOnlyList<RootDto> Roots { get; set; } = [];

    /// <summary>Недавно открытые.</summary>
    public IReadOnlyList<EntryDto> Recent { get; set; } = [];

    /// <summary>Закреплённые.</summary>
    public IReadOnlyList<EntryDto> Pinned { get; set; } = [];

    /// <summary>Состояние индекса.</summary>
    public IndexStatusDto Status { get; set; } = new(IndexerState.Idle, false, []);

    /// <summary>Последний запрос поиска.</summary>
    public SearchRequest? LastRequest { get; private set; }

    /// <summary>Последнее открытие: запись и запрос.</summary>
    public (long Id, string? Query)? Opened { get; private set; }

    /// <summary>Обработчики остальных методов по имени: получают аргументы, возвращают результат (уже задачу).</summary>
    public Dictionary<string, Func<object?[], object?>> Handlers { get; } = [];

    /// <summary>Создаёт клиента и доступ к подделке за ним.</summary>
    public static (IDiskAppApi Api, FakeDiskAppApi Fake) Create()
    {
        var api = DispatchProxy.Create<IDiskAppApi, FakeDiskAppApi>();
        return (api, (FakeDiskAppApi)(object)api);
    }

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        ArgumentNullException.ThrowIfNull(args);
        Calls.Add(targetMethod.Name);

        if (Handlers.TryGetValue(targetMethod.Name, out var handler))
        {
            return handler(args);
        }

        switch (targetMethod.Name)
        {
            case nameof(IDiskAppApi.SearchAsync):
                LastRequest = (SearchRequest)args[0]!;
                return Task.FromResult(Search(LastRequest));
            case nameof(IDiskAppApi.GetRootsAsync):
                return Task.FromResult(Roots);
            case nameof(IDiskAppApi.GetRecentAsync):
                return Task.FromResult(Recent);
            case nameof(IDiskAppApi.GetPinnedAsync):
                return Task.FromResult(Pinned);
            case nameof(IDiskAppApi.GetIndexStatusAsync):
                return Task.FromResult(Status);
            case nameof(IDiskAppApi.OpenAsync):
                Opened = ((long)args[0]!, (string?)args[1]);
                return Task.CompletedTask;
            default:
                throw new NotSupportedException(targetMethod.Name);
        }
    }
}
