using System.Text.Json.Nodes;

namespace DiskApp.UI.Services;

/// <summary>Обучающие подсказки (PLAN.md, этап 9.3).</summary>
public static class Tips
{
    /// <summary>Горячая клавиша быстрого поиска — на главной.</summary>
    public const string Hotkey = "hotkey";

    /// <summary>Фильтры и справка — в окне поиска.</summary>
    public const string Filters = "filters";

    /// <summary>Перетаскивание папок — на главной.</summary>
    public const string Drop = "drop";
}

/// <summary>
/// Какие обучающие подсказки уже показаны: каждая показывается один раз и только после мастера первого запуска.
/// </summary>
public interface ITipsService
{
    /// <summary>Подсказку пора показать.</summary>
    bool ShouldShow(string tip);

    /// <summary>Подсказка показана: больше не показывать.</summary>
    void MarkShown(string tip);
}

/// <summary>
/// Подсказки в настройках (<c>onboarding.tipsShown</c>).
/// </summary>
public sealed class SettingsTipsService(ISettingsService settings) : ITipsService
{
    private readonly Lock _gate = new();
    private readonly HashSet<string> _shown = new(StringComparer.Ordinal);
    private bool _ready;

    /// <summary>
    /// Загружает состояние; до загрузки и пока мастер первого запуска не пройден, подсказки не показываются.
    /// </summary>
    public async Task LoadAsync()
    {
        var current = await settings.GetAsync();
        lock (_gate)
        {
            _shown.Clear();
            _shown.UnionWith(current.Onboarding.TipsShown);
            _ready = current.Onboarding.Completed;
        }
    }

    /// <summary>
    /// Мастер запущен заново: все подсказки покажутся снова после него; до <see cref="Enable"/> они не показываются.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            _shown.Clear();
            _ready = false;
        }

        _ = SaveAsync(new JsonObject { ["onboarding"] = new JsonObject { ["tipsShown"] = new JsonArray() } });
    }

    /// <summary>Мастер пройден: подсказки можно показывать.</summary>
    public void Enable()
    {
        lock (_gate)
        {
            _ready = true;
        }
    }

    /// <inheritdoc />
    public bool ShouldShow(string tip)
    {
        lock (_gate)
        {
            return _ready && !_shown.Contains(tip);
        }
    }

    /// <inheritdoc />
    public void MarkShown(string tip)
    {
        JsonArray array;
        lock (_gate)
        {
            if (!_shown.Add(tip))
            {
                return;
            }

            array = [.. _shown.Order(StringComparer.Ordinal).Select(static name => JsonValue.Create(name))];
        }

        _ = SaveAsync(new JsonObject { ["onboarding"] = new JsonObject { ["tipsShown"] = array } });
    }

    private async Task SaveAsync(JsonObject patch)
    {
        try
        {
            await settings.PatchAsync(patch);
        }
        catch (InvalidOperationException)
        {
            // Подсказка покажется ещё раз при следующем запуске — не страшно.
        }
    }
}

/// <summary>
/// Подсказки не показываются (превью, тесты).
/// </summary>
public sealed class NoTips : ITipsService
{
    /// <inheritdoc />
    public bool ShouldShow(string tip) => false;

    /// <inheritdoc />
    public void MarkShown(string tip)
    {
    }
}
