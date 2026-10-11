using System.Globalization;

using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;

namespace DiskApp.UI.Localization;

/// <summary>
/// Разметка <c>{loc:Str Key}</c> (PLAN.md, этап 9.2): строка интерфейса по ключу, которая обновляется при смене языка.
/// Привязка идёт к номеру смены языка <see cref="Localizer.Revision"/>, а ключ передаётся конвертеру.
/// </summary>
public sealed class StrExtension : MarkupExtension
{
    private static readonly IValueConverter Lookup = new FuncValueConverter<int, string, string>(static (_, key) => Localizer.Instance[key ?? string.Empty]);

    /// <summary>Создаёт разметку без ключа (для XAML-компилятора).</summary>
    public StrExtension()
    {
    }

    /// <summary>Создаёт разметку для ключа.</summary>
    public StrExtension(string key)
    {
        Key = key;
    }

    /// <summary>Ключ строки в <c>Strings.resx</c>.</summary>
    public string Key { get; set; } = string.Empty;

    /// <inheritdoc />
    public override object ProvideValue(IServiceProvider serviceProvider)
        => new ReflectionBinding(nameof(Localizer.Revision))
        {
            Source = Localizer.Instance,
            Mode = BindingMode.OneWay,
            Converter = Lookup,
            ConverterParameter = Key,
            ConverterCulture = CultureInfo.InvariantCulture,
        };
}
