# Как участвовать в разработке DiskApp

Документ описывает, как устроена работа над кодом: единица работы, ветки, коммиты, стиль, тесты, документация. Что делаем и в каком порядке — в [PLAN.md](PLAN.md); как устроено приложение — в [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md); правила для сессий с Claude Code — в [CLAUDE.md](CLAUDE.md).

Язык документов, комментариев в коде и сообщений в тестах — русский. Идентификаторы, имена файлов кода, имена веток и сообщения коммитов — английский.

## Содержание

1. [Единица работы](#1-единица-работы)
2. [Ветки](#2-ветки)
3. [Коммиты](#3-коммиты)
4. [Pull request](#4-pull-request)
5. [Сборка и проверки](#5-сборка-и-проверки)
6. [Стиль кода](#6-стиль-кода)
7. [Тесты](#7-тесты)
8. [Документация](#8-документация)
9. [Зависимости](#9-зависимости)

## 1. Единица работы

Единица работы — подэтап плана (например, «0.4 Качество кода и соглашения») или один дефект. Подэтап готов, когда выполнен Definition of Done из PLAN.md (раздел 1):

1. Код собирается без предупреждений (`TreatWarningsAsErrors`), `dotnet format --verify-no-changes` проходит.
2. Для нового кода есть тесты; `dotnet test` зелёный целиком.
3. Для работы над интерфейсом пройден ручной сценарий из [docs/QA_CHECKLIST.md](docs/QA_CHECKLIST.md).
4. Документация обновлена: README (поведение продукта), ARCHITECTURE и ADR (решения), USER_GUIDE (пользовательский сценарий).
5. PLAN.md обновлён: чекбоксы, статус этапа, «Текущая позиция», журнал изменений плана.
6. Изменения закоммичены: один подэтап — 1–3 коммита.

## 2. Ветки

- `main` всегда собирается и проходит тесты. Работа идёт в ветках; готовую ветку сливаем в `main` сами (fast-forward, если возможно) и отправляем на GitHub. Pull request не обязателен: проект ведёт один человек, а CI (подэтап 0.5) будет проверять каждый коммит в `main`.
- Рабочая ветка называется по типу изменения и подэтапу: `<тип>/<подэтап>-<кратко>`, латиницей, через дефис.

| Префикс | Для чего | Пример |
|---|---|---|
| `feat/` | новая функциональность по плану | `feat/2.1-scanner` |
| `fix/` | исправление дефекта | `fix/3.2-empty-query-crash` |
| `chore/` | обслуживание: зависимости, конфигурация, инструменты, рефакторинг без изменения поведения | `chore/0.4-editorconfig` |
| `docs/` | только документация | `docs/0.7-readme-status` |

- Одна ветка — один подэтап или один дефект. Ветка живёт недолго: обновляется от `main` через rebase, после слияния удаляется.
- Ветки сессий Claude Code (`claude/…`) — рабочие ветки того же назначения: из них делается PR в `main`.

## 3. Коммиты

Формат — [Conventional Commits](https://www.conventionalcommits.org/ru/v1.0.0/): `<тип>(<область>): <описание>`.

- Тип — один из `feat`, `fix`, `perf`, `refactor`, `test`, `docs`, `build`, `ci`, `chore`.
- Область — проект или подсистема: `repo`, `solution`, `plan`, `core`, `index`, `watcher`, `search`, `api`, `hub`, `client`, `ui`, `desktop`, `tray`, `hotkey`, `settings`, `backup`, `inbox`, `tests`, `ci`, `release`. Новая область появляется вместе с подсистемой.
- Описание — на английском, в повелительном наклонении, без точки в конце, до 72 символов: `add`, `fix`, `move`, а не `added` или `adds`.
- Тело коммита (по желанию) объясняет *почему*, а не *что*. Ссылка на подэтап приветствуется: `Stage 2.1`.
- Ломающее изменение (формат базы, настроек, API) помечается `!` после области и футером `BREAKING CHANGE:`.

Примеры:

```text
feat(index): scan roots in parallel with bounded channel
fix(search): treat empty query as "recent files" instead of throwing
test(fixtures): add temporary directory and database fixtures
docs(plan): close stage 0.4 and move current position to 0.5
```

Один подэтап — 1–3 коммита: обычно «код и тесты», «документация», «план». Коммиты вида «wip» или «fix tests» в `main` не попадают: перед PR история приводится в порядок.

## 4. Слияние в `main`

- Перед слиянием ветка собрана в Release, тесты и `dotnet format --verify-no-changes` зелёные, PLAN.md обновлён.
- Слияние делается локально: `git checkout main`, `git merge --ff-only <ветка>`, `git push`. Если `main` ушёл вперёд, ветка сначала перебазируется (`git rebase main`), чтобы история оставалась линейной.
- Pull request на GitHub — по желанию, когда нужен просмотр изменений в браузере или обсуждение; тогда один PR — один подэтап, заголовок как у главного коммита, в описании что сделано, что проверено руками и что отложено.

## 5. Сборка и проверки

Перед коммитом:

```powershell
dotnet build -c Release
```

```powershell
dotnet test
```

```powershell
dotnet format --verify-no-changes
```

- `dotnet format` без `--verify-no-changes` исправляет пробелы, порядок `using`, неиспользуемые `using` и стиль автоматически.
- Тесты идут через Microsoft.Testing.Platform (`global.json` → `test.runner`). Отчёты TRX и покрытие — аргументами платформы после `--`: `dotnet test -- --report-trx --coverage`.
- Запуск приложения: `dotnet run --project src/DiskApp.Desktop`.
- Бенчмарки: `dotnet run -c Release --project tests/DiskApp.Benchmarks -- --filter "*"`.

### Замеры и профилирование (этап 10.1)

Бюджеты производительности (PLAN.md, раздел 8) снимает один скрипт. Он публикует текущий код как для пользователя и запускает портативную копию во временной папке, рабочие данные не трогаются. Итог — таблица «бюджет / замер» в консоли и в `artifacts\perf\perf-<дата>.md`.

```powershell
.\scripts\measure.ps1
```

- `-Quick` — короткая проверка самого скрипта (3 000 файлов, минута простоя, бенчмарки на малых объёмах); полный прогон занимает около 20 минут и нужен перед релизом и после оптимизаций.
- `-Exe <путь>` — замерить готовую сборку, например из `releases\`; `-SkipBenchmarks` — только приложение, без проверок бюджета ядра.
- Ноутбук держать от сети и не нагружать машину во время прогона: от этого зависят цифры параллельной нагрузки.

Горячие пути снимает `dotnet-trace` по сценариям: старт, индексация, поиск через API, прокрутка списка результатов, снимок управляемой кучи. Трассы и топы методов — в `artifacts\perf\profile-<дата>\`; трассу можно открыть в Visual Studio или перевести в speedscope (`dotnet tool run dotnet-trace -- convert <файл> --format speedscope`).

```powershell
.\scripts\profile.ps1 -Scenario startup,memory
```

Инструменты диагностики (`dotnet-trace`, `dotnet-counters`, `dotnet-gcdump`) — локальные, версии закреплены в `dotnet-tools.json`; `dotnet tool restore` ставит их, скрипт делает это сам.

В CI бенчмарки идут дымовым прогоном на малых объёмах без порогов: `dotnet run -c Release --project tests/DiskApp.Benchmarks -- smoke`. Он падает только на исключении, таблицы попадают в сводку сборки.

Если `dotnet` или `git` не находятся в терминале сразу после установки — обновить PATH (команда в CLAUDE.md).

## 6. Стиль кода

Правила заданы в `.editorconfig` и проверяются при сборке (`EnforceCodeStyleInBuild`, предупреждения считаются ошибками). Главное:

- Пространства имён в стиле файла (`namespace DiskApp.Core;`), `using` вне пространства имён; `System.*` первыми, группы разделены пустой строкой; неиспользуемые `using` — ошибка сборки.
- Фигурные скобки всегда, в том числе для однострочных `if`.
- Именование: типы, методы, свойства, события — `PascalCase`; интерфейсы — `IPascalCase`; параметры и локальные переменные — `camelCase`; приватные поля — `_camelCase`, статические тоже (без `s_`); константы и `static readonly` — `PascalCase`; асинхронные методы — с суффиксом `Async` (в тестах не требуется).
- `var` — когда тип очевиден из правой части; для встроенных типов (`int`, `string`) — явный тип.
- Модификаторы доступа всегда явные; неизменяемые поля — `readonly`; классы без наследников — `sealed`.
- Предпочтения выражений: сопоставление с образцом вместо `as` и проверки на `null`, `switch`-выражения, `?.` и `??`, инициализаторы объектов и коллекций, выражения коллекций `[…]`, первичные конструкторы — там, где они упрощают код. `dotnet format` применяет их сам.
- Публичные и внутренние типы и члены снабжаются XML-комментарием `<summary>` на русском: что делает и зачем, а не как. XML-файл документации генерируется, поэтому ссылки `<see cref>` проверяет компилятор; отсутствие комментария ошибкой не считается (CS1591 отключён).
- Платформенный код (P/Invoke, реестр, Shell) живёт только в `Infrastructure` и `Desktop`; `Core` не знает об Avalonia, ASP.NET Core и SQLite; `UI` не ссылается на `Core` и `Infrastructure`. Это проверяют архитектурные тесты.
- Nullable включён; `!` (null-forgiving) — только там, где значение заведомо не null, и это видно из соседней строки.
- Файлы пользователя удаляются только в корзину; безвозвратное удаление — явный флаг операции (решение 16 плана).
- Строки интерфейса и сообщений API — в ресурсах (этап 9.2), не в коде: `Localization/Strings.resx` (русский, нейтральный) и `Strings.en.resx` (английский) в `DiskApp.UI` (интерфейс и десктоп), `DiskApp.Server` (ответы API) и `DiskApp.Core` (ядро и инфраструктура). В коде — `L.T("Ключ")` или `L.F("Ключ", аргументы)`, в AXAML — `{loc:Str Ключ}`. Ключ — `Класс.КороткоеАнглийскоеОписание`; общие слова — `Common.*`, формы слова для `Formatting.Count` — `*.One`, `*.Few`, `*.Many`. Журналы, описания OpenAPI и синтаксис запросов не переводятся. Новые строки пакетом выносит `scripts/l10n/L10n.cs` (`extract` → перевод → `apply`; `check` и `spaces` проверяют ресурсы); тесты падают, если в AXAML осталась кириллица или у русской строки нет английской с теми же подстановками.

## 7. Тесты

### Проекты

| Проект | Что проверяет | Инструменты |
|---|---|---|
| `tests/DiskApp.Core.Tests` | домен, разбор запросов, ранжирование, архитектурные правила | xunit v3, Shouldly, NSubstitute |
| `tests/DiskApp.Infrastructure.Tests` | SQLite, миграции, FTS5, файловая система, наблюдатель | то же + фикстуры |
| `tests/DiskApp.Server.Tests` | endpoints, хаб SignalR, hosted services | то же + Kestrel на `127.0.0.1:0` |
| `tests/DiskApp.UI.Tests` | ViewModels и представления на headless-платформе Avalonia | то же + `Avalonia.Headless`, `HeadlessTest.RunAsync` |
| `tests/DiskApp.Testing` | общие фикстуры; не тестовый проект, подключается ко всем тестовым проектам автоматически (`tests/Directory.Build.targets`) | без зависимостей от `src/` |
| `tests/DiskApp.Benchmarks` | бюджеты производительности (PLAN.md, раздел 8); не входит в `dotnet test` | BenchmarkDotNet |

Пирамида: модульные тесты Core → интеграционные Infrastructure и Server → headless UI → ручной чеклист (`docs/QA_CHECKLIST.md`). Пакеты xunit, Shouldly, NSubstitute и глобальные `using` для них тестовые проекты получают из `tests/Directory.Build.targets`, в `.csproj` они не повторяются.

### Шаблон теста

Имя теста — `Method_Scenario_Expected`: что вызываем, при каких условиях, что ожидаем. Тело делится комментариями `// Arrange`, `// Act`, `// Assert`; если подготовка совпадает с действием или проверка — это ожидание исключения, допустимы `// Arrange & Act` и `// Act & Assert`. Один тест — одно ожидание; несколько `Should…` допустимы, если проверяют один результат.

```csharp
namespace DiskApp.Core.Tests.Search;

/// <summary>
/// Разбор строки запроса: токены, фильтры по расширению и корню, кавычки.
/// </summary>
public sealed class QueryParserTests
{
    [Fact]
    public void Parse_QuotedPhrase_KeepsSpacesInsideToken()
    {
        // Arrange
        var parser = new QueryParser();

        // Act
        var query = parser.Parse("\"quarterly report\" ext:pdf");

        // Assert
        query.Terms.ShouldBe(["quarterly report"]);
        query.Extensions.ShouldBe(["pdf"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_EmptyOrWhitespace_ReturnsEmptyQuery(string text)
    {
        // Arrange
        var parser = new QueryParser();

        // Act
        var query = parser.Parse(text);

        // Assert
        query.IsEmpty.ShouldBeTrue();
    }
}
```

- Класс тестов — `sealed`, по одному на тестируемый тип или сценарий, с `<summary>` о том, какое поведение он закрепляет.
- Асинхронный тест возвращает `Task`; в вызовы передаётся `TestContext.Current.CancellationToken` (xunit v3), чтобы зависший тест прерывался раннером.
- Исключения проверяются через `Should.Throw<T>(() => …)` и `await Should.ThrowAsync<T>(…)`.
- `customMessage` в Shouldly указывается, когда причина падения неочевидна из имени теста.
- Подмены (NSubstitute) — только для интерфейсов ядра. Файловая система и база не подменяются: для них есть фикстуры.
- Тесты интерфейса выполняются на UI-потоке через `HeadlessTest.RunAsync(() => …)`.

### Фикстуры

Временные папки и базы — только через фикстуры: `TemporaryDirectory` из `DiskApp.Testing` и `TemporaryDatabase` из `DiskApp.Infrastructure.Tests`. Они гарантируют очистку даже при падении теста.

```csharp
[Fact]
public void Scan_FolderWithNestedFiles_IndexesAllEntries()
{
    // Arrange
    using var folder = new TemporaryDirectory();
    folder.CreateFile(Path.Combine("docs", "report.pdf"));
    folder.CreateFile("notes.txt", "hello");
    using var database = new TemporaryDatabase();
    using var connection = database.OpenConnection();

    // Act … Assert …
}
```

- `TemporaryDirectory` — уникальная папка в `%TEMP%\DiskApp.Tests`; `CreateFile`, `CreateDirectory` и `Resolve` не выпускают за её пределы; `Dispose` снимает атрибут «только для чтения» и удаляет всё с повторными попытками.
- `TemporaryDatabase` — файловая база SQLite в собственной `TemporaryDirectory` (а не `:memory:`, чтобы проверять WAL и несколько соединений); `OpenConnection()` возвращает открытое соединение, `Dispose` сбрасывает пул соединений и удаляет папку.
- Пулы SQLite в тестах и замерах сбрасываются только через `SqliteConnectionFactory.ClearAllPools()`, а не `SqliteConnection.ClearAllPools()`: тесты идут параллельно, и сброс пулов без общей блокировки закрывает соединение, которое в этот миг открывает соседний тест (см. docs/ARCHITECTURE.md, этап 1.3).
- Общая на класс фикстура (`IClassFixture<T>`) — только для дорогих ресурсов, например базы на тысячи записей; по умолчанию каждый тест создаёт своё.
- Нестабильный тест помечается `[Fact(Skip = "…")]` со ссылкой на задачу и чинится, а не удаляется.

## 8. Документация

| Что изменилось | Что обновить |
|---|---|
| Поведение продукта, команды, параметры запуска | README.md, docs/USER_GUIDE.md |
| Решение из раздела 2 PLAN.md | docs/ADR/NNNN-….md, журнал изменений плана, docs/ARCHITECTURE.md |
| Структура проектов, конвейеры, API | docs/ARCHITECTURE.md |
| Экран или сценарий интерфейса | docs/QA_CHECKLIST.md |
| Любой подэтап | PLAN.md (чекбоксы, статус, «Текущая позиция», «Порядок работ», журнал), CHANGELOG.md (раздел Unreleased) |
| Пользователь уточнил или изменил замысел продукта | docs/VISION.md (его словами), затем PLAN.md: сверка и журнал |

## 9. Зависимости

Версии пакетов заданы централизованно в `Directory.Packages.props`; в `.csproj` попадает только `PackageReference` без версии. Новый пакет берётся из списка в PLAN.md (этап 0.3) или добавляется с записью в журнале изменений плана.
