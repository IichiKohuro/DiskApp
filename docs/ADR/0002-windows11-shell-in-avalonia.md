# ADR-0002: Стиль Windows 11 в Avalonia

**Статус:** Принято · **Дата:** 2026-10-10 · **Связано с:** PLAN.md решение 5, этапы 0.6 (спайк S1), 6.7, 6.8

## Контекст

Интерфейс должен выглядеть как приложение Windows 11: Mica в главном окне, собственный заголовок с системными кнопками, безрамочное всплывающее окно поиска с Acrylic и скруглёнными углами, светлая и тёмная тема вслед за системой, акцентный цвет пользователя. Окно поиска должно появляться по хоткею быстрее чем за 100 мс. Нужно было убедиться, что Avalonia 12.1 и FluentAvaloniaUI 3.1 дают это без собственного рендера рамок и без WinUI.

Проверка прошла не одноразовым спайком, а прототипом интерфейса (этапы 6–7 на макетных данных, 2026-10-10): код остался в `DiskApp.Desktop` и `DiskApp.UI`.

## Решение

1. **Главное окно наследует `FAAppWindow`** из FluentAvalonia 3.1: оно рисует заголовок и системные кнопки в стиле Windows 11, а содержимое расширяется в область заголовка (`TitleBar.ExtendsContentIntoTitleBar = true`, высота 32). Собственная полоса заголовка в разметке окна — иконка и название, `IsHitTestVisible="False"`, чтобы она работала как область перетаскивания.
2. **Mica** включается `TransparencyLevelHint = [Mica, AcrylicBlur, None]`, а поверх материала кладётся полупрозрачный фон окна в цвете темы (`Backdrop.ApplyMica`: тёмная тема — базовый цвет, затемнённый на 80 %, непрозрачность 0,78; светлая — осветлённый на 50 %, 0,9; приём из примера FluentAvalonia). Если `ActualTransparencyLevel` не Mica (Windows 10, отключённая прозрачность), фон — сплошной `SolidBackgroundFillColorBaseBrush`. Цвет обновляется при смене темы и уровня прозрачности.
3. **Всплывающее окно** — обычный `Window` с `WindowDecorations="None"`, `Topmost`, `ShowInTaskbar="False"`, `TransparencyLevelHint="AcrylicBlur, Blur, None"`, `TransparencyBackgroundFallback` сплошным цветом темы, `ExperimentalAcrylicBorder` для тонировки и обводка 1 px. Углы скругляет DWM: `DwmSetWindowAttribute(DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND)` по дескриптору из `TryGetPlatformHandle()`; на Windows 10 атрибут игнорируется. Окно создаётся при старте и прячется; показ ставит его в верхнюю треть монитора с курсором.
4. **Тема и акцент** — `FluentAvaloniaTheme` с `PreferSystemTheme` и `PreferUserAccentColor`; ручной выбор через `Application.RequestedThemeVariant` (`IThemeService`). Шрифт — системный Segoe UI Variable (`UseSystemFontOnWindows`), fallback Inter. Иконки — FluentIcons.Avalonia `SymbolIcon` в содержимом и `FASymbolIconSource` в `FANavigationView`.
5. **Ресурсы темы** берутся только через `DynamicResource` (`TextFillColor*`, `CardBackgroundFillColor*`, `AccentTextFillColorPrimaryBrush` и т. д.), чтобы смена темы перекрашивала всё без перезапуска.

## Последствия

- Замер показа заранее созданного окна поиска (Release, framework-dependent, Windows 11, 125 %): **82 мс до первого кадра при первом показе, 1 мс при последующих** — бюджет < 100 мс выполнен; первый показ включает построение шаблонов, при желании его можно прогреть при старте.
- Код привязан к FluentAvalonia 3.x: контролы с префиксом `FA` (`FANavigationView`, `FASettingsExpander`, `FAInfoBar`), `FAAppWindow`. Обновление мажорной версии — отдельная задача.
- Приём с полупрозрачным фоном поверх Mica опирается на поведение Avalonia; при обновлении Avalonia проверять по чеклисту.
- В Avalonia 12 нет `ExtendClientAreaChromeHints` и `Win32Properties`; `FAAppWindow` закрывает заголовок, хоткеи решает ADR-0003.
- Снимки окон для проверки делаются только через `PrintWindow(PW_RENDERFULLCONTENT)`: обычный захват экрана даёт чёрный прямоугольник из-за DirectComposition.
- Не проверено руками: Windows 10, отключённая прозрачность, высокая контрастность, масштаб 100 % — пункты QA_CHECKLIST.md.

## Альтернативы

- **Чистый Avalonia `ExtendClientAreaToDecorationsHint` с собственными кнопками заголовка.** Больше кода, менее нативный вид; FAAppWindow делает то же из коробки.
- **Системные декорации (`WindowDecorations="Full"`).** Нет Mica в заголовке и единого цвета, выглядит как Windows 10.
- **WinUI 3.** Нативно, но без Avalonia-кроссплатформенности, нужной этапам 14–15.

## Проверка

Прогон 2026-10-10 в `docs/QA_CHECKLIST.md` (снимки в `docs/images/`); замер показа — строки «Окно поиска: показ №N» в журнале. Пересмотр при обновлении Avalonia или FluentAvalonia и после ручной проверки на Windows 10.
