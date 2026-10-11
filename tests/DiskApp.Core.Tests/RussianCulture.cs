using System.Globalization;
using System.Runtime.CompilerServices;

namespace DiskApp.Core.Tests;

/// <summary>
/// Тесты проверяют русские сообщения: язык — русский, какой бы ни была культура машины или CI.
/// </summary>
internal static class RussianCulture
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        var russian = CultureInfo.GetCultureInfo("ru-RU");
        CultureInfo.DefaultThreadCurrentUICulture = russian;
        CultureInfo.DefaultThreadCurrentCulture = russian;
        CultureInfo.CurrentUICulture = russian;
        CultureInfo.CurrentCulture = russian;
    }
}
