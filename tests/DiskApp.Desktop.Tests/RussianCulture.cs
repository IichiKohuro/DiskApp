using System.Runtime.CompilerServices;

using DiskApp.UI.Localization;

namespace DiskApp.Desktop.Tests;

/// <summary>
/// Тесты проверяют русские тексты: язык интерфейса — русский, какой бы ни была культура машины или CI.
/// </summary>
internal static class RussianCulture
{
    [ModuleInitializer]
    internal static void Initialize() => Localizer.Instance.SetLanguage("ru");
}
