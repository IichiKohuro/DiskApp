using System.Collections.ObjectModel;
using System.Text.Json.Nodes;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DiskApp.Contracts.App;
using DiskApp.UI.Models;
using DiskApp.UI.Services;

using FluentAvalonia.UI.Controls;

namespace DiskApp.UI.ViewModels;

/// <summary>
/// Корень в настройках: путь, состояние, скрытые и системные файлы, глубина обхода.
/// </summary>
public sealed partial class RootSettingsItem : ObservableObject
{
    private readonly Func<RootSettingsItem, Task> _save;
    private bool _ready;

    /// <summary>Создаёт строку корня; изменения флагов сохраняются через <paramref name="save"/>.</summary>
    public RootSettingsItem(RootInfo root, Func<RootSettingsItem, Task> save)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(save);

        Root = root;
        _save = save;
        IncludeHidden = root.IncludeHidden;
        IncludeSystem = root.IncludeSystem;
        DepthText = root.MaxDepth?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? string.Empty;
        _ready = true;
    }

    /// <summary>Корень, как его знает сервер.</summary>
    public RootInfo Root { get; private set; }

    /// <summary>Подпись состояния: «Доступен», «Недоступен: диск E: не подключён».</summary>
    public string StateText => Root.State switch
    {
        RootState.Offline => "Недоступен" + (Root.Message is { Length: > 0 } message ? ": " + message : string.Empty),
        RootState.Scanning => "Индексируется",
        RootState.Paused => "На паузе",
        RootState.Disabled => "Выключен",
        RootState.Error => "Ошибка" + (Root.Message is { Length: > 0 } error ? ": " + error : string.Empty),
        _ => "Доступен",
    };

    /// <summary>Скрытые файлы.</summary>
    [ObservableProperty]
    public partial bool IncludeHidden { get; set; }

    /// <summary>Системные файлы.</summary>
    [ObservableProperty]
    public partial bool IncludeSystem { get; set; }

    /// <summary>Глубина обхода; пусто — без предела.</summary>
    [ObservableProperty]
    public partial string DepthText { get; set; }

    /// <summary>Сохранённые значения после ответа сервера.</summary>
    public void Update(RootInfo root)
    {
        _ready = false;
        Root = root;
        IncludeHidden = root.IncludeHidden;
        IncludeSystem = root.IncludeSystem;
        DepthText = root.MaxDepth?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? string.Empty;
        OnPropertyChanged(nameof(StateText));
        _ready = true;
    }

    /// <summary>Глубина из текста: пусто — без предела; не число — <c>false</c>.</summary>
    public bool TryGetDepth(out int? depth)
    {
        depth = null;
        if (DepthText.Trim().Length == 0)
        {
            return true;
        }

        if (int.TryParse(DepthText.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.CurrentCulture, out var value) && value is >= 1 and <= 256)
        {
            depth = value;
            return true;
        }

        return false;
    }

    partial void OnIncludeHiddenChanged(bool value) => Changed();

    partial void OnIncludeSystemChanged(bool value) => Changed();

    partial void OnDepthTextChanged(string value) => Changed();

    private void Changed()
    {
        if (_ready)
        {
            _ = _save(this);
        }
    }
}

/// <summary>Правило исключения в настройках.</summary>
/// <param name="Kind">Вид правила.</param>
/// <param name="Pattern">Шаблон или число.</param>
public sealed record ExclusionItem(ExclusionKind Kind, string Pattern)
{
    /// <summary>Подпись для списка.</summary>
    public string Text => Kind switch
    {
        ExclusionKind.Glob => Pattern,
        ExclusionKind.Hidden => "Скрытые файлы и папки",
        ExclusionKind.System => "Системные файлы и папки",
        ExclusionKind.Size => $"Файлы больше {Pattern} байт",
        ExclusionKind.Depth => $"Глубже {Pattern} уровней",
        _ => Pattern,
    };
}

/// <summary>
/// Раздел «Индекс» (PLAN.md, этап 9.1): корни с добавлением папки или диска, параметрами, пересканированием и удалением;
/// правила исключения с проверкой шаблона.
/// </summary>
public sealed partial class SettingsViewModel
{
    private readonly IRootsService _roots;

    /// <summary>Корни индекса с параметрами.</summary>
    public ObservableCollection<RootSettingsItem> RootItems { get; } = [];

    /// <summary>Правила исключения.</summary>
    public ObservableCollection<ExclusionItem> Exclusions { get; } = [];

    /// <summary>Новый шаблон исключения, например <c>**/bin/**</c>.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddExclusionCommand))]
    public partial string NewExclusion { get; set; } = string.Empty;

    /// <summary>Ошибка шаблона исключения; пусто — шаблон годится.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExclusionError))]
    public partial string ExclusionError { get; set; } = string.Empty;

    /// <summary>Есть ошибка шаблона.</summary>
    public bool HasExclusionError => ExclusionError.Length > 0;

    /// <summary>Диски компьютера для добавления целиком.</summary>
    public ObservableCollection<string> Drives { get; } = [];

    /// <summary>
    /// Проверяет шаблон исключения: не пустой, без запрещённых в путях символов, разделители — <c>/</c> или <c>\</c>.
    /// </summary>
    /// <returns>Текст ошибки или <c>null</c>.</returns>
    public static string? ValidateGlob(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        var trimmed = pattern.Trim();
        if (trimmed.Length == 0)
        {
            return "Введите шаблон, например **/bin/** или *.tmp.";
        }

        if (trimmed.IndexOfAny(['<', '>', '"', '|', '\0']) >= 0)
        {
            return "В шаблоне есть символ, недопустимый в путях Windows: < > \" |.";
        }

        if (trimmed.Contains("***", StringComparison.Ordinal))
        {
            return "Больше двух звёздочек подряд не бывает: ** — любые папки, * — любая часть имени.";
        }

        return null;
    }

    private void ApplyExclusions(IReadOnlyList<ExclusionRuleDto> rules)
    {
        Exclusions.Clear();
        foreach (var rule in rules)
        {
            Exclusions.Add(new ExclusionItem(rule.Kind, rule.Pattern));
        }
    }

    private void SaveExclusions()
    {
        var array = new JsonArray();
        foreach (var rule in Exclusions)
        {
            array.Add(new JsonObject { ["kind"] = rule.Kind.ToString(), ["pattern"] = rule.Pattern });
        }

        Save("indexing", "exclusions", array);
    }

    private bool CanAddExclusion() => NewExclusion.Trim().Length > 0;

    /// <summary>Добавить правило исключения.</summary>
    [RelayCommand(CanExecute = nameof(CanAddExclusion))]
    private void AddExclusion()
    {
        if (ValidateGlob(NewExclusion) is { } error)
        {
            ExclusionError = error;
            return;
        }

        var pattern = NewExclusion.Trim().Replace('\\', '/');
        if (Exclusions.Any(rule => rule.Kind == ExclusionKind.Glob && string.Equals(rule.Pattern, pattern, StringComparison.OrdinalIgnoreCase)))
        {
            ExclusionError = "Такое правило уже есть.";
            return;
        }

        Exclusions.Add(new ExclusionItem(ExclusionKind.Glob, pattern));
        NewExclusion = string.Empty;
        ExclusionError = string.Empty;
        SaveExclusions();
    }

    /// <summary>Убрать правило исключения.</summary>
    [RelayCommand]
    private void RemoveExclusion(ExclusionItem rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (Exclusions.Remove(rule))
        {
            SaveExclusions();
        }
    }

    /// <summary>Вернуть правила исключения по умолчанию.</summary>
    [RelayCommand]
    private void ResetExclusions()
    {
        PendingSave = ResetExclusionsAsync();
    }

    private async Task ResetExclusionsAsync()
    {
        var previous = PendingSave;
        await previous;
        try
        {
            var saved = await _settings.PatchAsync(new JsonObject { ["indexing"] = new JsonObject { ["exclusions"] = null } });
            ApplyExclusions(saved.Indexing.Exclusions);
            SettingsNote = string.Empty;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SettingsNote = "Правила не сброшены: " + exception.Message;
        }
    }

    /// <summary>Перечитывает корни индекса.</summary>
    private async Task LoadRootsAsync()
    {
        var roots = await _roots.GetRootsAsync();
        RootItems.Clear();
        Roots.Clear();
        foreach (var root in roots)
        {
            RootItems.Add(new RootSettingsItem(root, SaveRootAsync));
            Roots.Add(root.Path);
        }

        Drives.Clear();
        foreach (var drive in ListDrives())
        {
            Drives.Add(drive);
        }
    }

    private static IEnumerable<string> ListDrives()
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (IOException)
        {
            yield break;
        }

        foreach (var drive in drives)
        {
            bool ready;
            try
            {
                ready = drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Network;
            }
            catch (IOException)
            {
                ready = false;
            }

            if (ready)
            {
                yield return drive.RootDirectory.FullName;
            }
        }
    }

    private async Task SaveRootAsync(RootSettingsItem item)
    {
        if (!item.TryGetDepth(out var depth))
        {
            SettingsNote = "Глубина — число от 1 до 256 или пусто, чтобы обходить все уровни.";
            return;
        }

        try
        {
            var updated = await _roots.UpdateAsync(item.Root with { IncludeHidden = item.IncludeHidden, IncludeSystem = item.IncludeSystem, MaxDepth = depth });
            item.Update(updated);
            SettingsNote = string.Empty;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SettingsNote = "Корень не изменён: " + exception.Message;
            item.Update(item.Root);
        }
    }

    /// <summary>Добавить папку в индекс.</summary>
    [RelayCommand]
    private async Task AddRootFolderAsync()
    {
        if (_dialogs is null || await _dialogs.PickFolderAsync("Папка для индекса") is not { } folder)
        {
            return;
        }

        await AddRootAsync(folder);
    }

    /// <summary>Добавить диск целиком.</summary>
    [RelayCommand]
    private Task AddDriveAsync(string drive) => AddRootAsync(drive);

    private async Task AddRootAsync(string path)
    {
        try
        {
            var root = await _roots.AddAsync(path);
            await LoadRootsAsync();
            _notices?.Show("Корень добавлен", $"«{root.Label}» индексируется: поиск находит файлы по мере обхода.", FAInfoBarSeverity.Success);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SettingsNote = "Корень не добавлен: " + exception.Message;
        }
    }

    /// <summary>Пересканировать корень.</summary>
    [RelayCommand]
    private async Task RescanRootAsync(RootSettingsItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        try
        {
            await _roots.RescanAsync(item.Root.Id);
            _notices?.Show("Пересканирование", $"«{item.Root.Label}» поставлен в очередь.", FAInfoBarSeverity.Informational);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SettingsNote = "Не удалось пересканировать: " + exception.Message;
        }
    }

    /// <summary>Убрать корень из индекса (файлы остаются).</summary>
    [RelayCommand]
    private async Task RemoveRootAsync(RootSettingsItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_dialogs is not null
            && !await _dialogs.ConfirmAsync("Убрать из индекса", $"Убрать «{item.Root.Path}» из индекса? Файлы на диске не тронутся, но искаться не будут.", "Убрать"))
        {
            return;
        }

        try
        {
            await _roots.RemoveAsync(item.Root.Id);
            await LoadRootsAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SettingsNote = "Корень не убран: " + exception.Message;
        }
    }
}
