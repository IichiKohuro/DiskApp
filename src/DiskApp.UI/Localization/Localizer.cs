using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace DiskApp.UI.Localization;

/// <summary>
/// Строки интерфейса на текущем языке (PLAN.md, этап 9.2): русский — основной (нейтральные ресурсы), английский — в
/// <c>Strings.en.resx</c>. Смена языка поднимает <see cref="PropertyChanged"/>, и разметка <c>{loc:Str Key}</c>
/// перечитывает строки без перезапуска; строки, собранные в коде, обновляются при следующем показе.
/// </summary>
public sealed class Localizer : INotifyPropertyChanged
{
    /// <summary>Язык «как в системе» в настройках.</summary>
    public const string SystemLanguage = "system";

    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    private readonly ResourceManager _resources = new("DiskApp.UI.Localization.Strings", typeof(Localizer).Assembly);

    private Localizer()
    {
        Culture = Pick(CultureInfo.CurrentUICulture);
    }

    /// <summary>Общий экземпляр приложения.</summary>
    public static Localizer Instance { get; } = new();

    /// <summary>Языки интерфейса: код и название на самом языке.</summary>
    public static IReadOnlyList<(string Code, string Name)> Languages { get; } = [("ru", "Русский"), ("en", "English")];

    /// <summary>Текущая культура интерфейса: <c>ru-RU</c> или <c>en-US</c>.</summary>
    public CultureInfo Culture { get; private set; }

    /// <summary>Номер смены языка: разметка подписана на него, чтобы перечитать строки.</summary>
    public int Revision { get; private set; }

    /// <summary>Интерфейс на английском.</summary>
    public bool IsEnglish => Culture.TwoLetterISOLanguageName == "en";

    /// <summary>Строка по ключу на текущем языке; неизвестный ключ возвращается как есть, чтобы пропуск был виден.</summary>
    public string this[string key] => _resources.GetString(key, Culture) ?? key;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Язык сменился.</summary>
    public event EventHandler? LanguageChanged;

    /// <summary>
    /// Культура для настройки языка: <c>system</c> — по языку Windows (русский для русского, иначе английский).
    /// </summary>
    public static CultureInfo Resolve(string? language) => language switch
    {
        "ru" => Russian,
        "en" => English,
        _ => Pick(CultureInfo.InstalledUICulture),
    };

    /// <summary>
    /// Включает язык: культура интерфейса и форматов для всех потоков, в том числе сервера внутри процесса.
    /// </summary>
    public void SetLanguage(string? language)
    {
        var culture = Resolve(language);
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        if (Equals(culture, Culture))
        {
            return;
        }

        Culture = culture;
        Revision++;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Culture)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Revision)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    private static CultureInfo Pick(CultureInfo culture) => culture.TwoLetterISOLanguageName == "ru" ? Russian : English;
}
