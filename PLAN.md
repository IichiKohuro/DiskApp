# План разработки DiskApp

Версия плана: 1.0 · Создан: 2026-10-10 · Продукт описан в [README.md](README.md).

Этот документ — рабочий. По нему ведётся разработка: здесь отмечается выполненное, фиксируется текущая позиция, пересматриваются решения. Любое изменение плана делается здесь же, с датой в разделе «Журнал изменений плана».

---

## Содержание

1. [Как пользоваться планом](#1-как-пользоваться-планом)
2. [Ключевые решения](#2-ключевые-решения)
3. [Открытые вопросы](#3-открытые-вопросы)
4. [Вехи и версии](#4-вехи-и-версии)
5. [Этапы](#5-этапы)
   - [Этап 0 — Окружение и каркас репозитория](#этап-0--окружение-и-каркас-репозитория)
   - [Этап 1 — Ядро: домен и хранилище](#этап-1--ядро-домен-и-хранилище)
   - [Этап 2 — Индексация и слежение за файловой системой](#этап-2--индексация-и-слежение-за-файловой-системой)
   - [Этап 3 — Поиск](#этап-3--поиск)
   - [Этап 4 — Сервер: Web API и SignalR](#этап-4--сервер-web-api-и-signalr)
   - [Этап 5 — Клиентская библиотека](#этап-5--клиентская-библиотека)
   - [Этап 6 — Десктоп: каркас, трей, окна, стиль Windows 11](#этап-6--десктоп-каркас-трей-окна-стиль-windows-11)
   - [Этап 7 — Быстрый поиск](#этап-7--быстрый-поиск)
   - [Этап 8 — Главное окно: обзор, операции, «добавить» и «сохранить»](#этап-8--главное-окно-обзор-операции-добавить-и-сохранить)
   - [Этап 9 — Настройки, локализация, онбординг, доступность](#этап-9--настройки-локализация-онбординг-доступность)
   - [Этап 10 — Производительность, надёжность, стабилизация](#этап-10--производительность-надёжность-стабилизация)
   - [Этап 11 — Сборка, установка, обновления, релиз 1.0](#этап-11--сборка-установка-обновления-релиз-10)
   - [Этап 12 — Расширенные возможности (v1.x)](#этап-12--расширенные-возможности-v1x)
   - [Этап 13 — Удалённый доступ и безопасность](#этап-13--удалённый-доступ-и-безопасность)
   - [Этап 14 — Android](#этап-14--android)
   - [Этап 15 — Web (Avalonia Browser)](#этап-15--web-avalonia-browser)
   - [Этап 16 — Модуль резервного копирования](#этап-16--модуль-резервного-копирования)
   - [Этап 17 — Разбор входящих: папки-накопители и быстрая сортировка](#этап-17--разбор-входящих-папки-накопители-и-быстрая-сортировка)
6. [Сквозные практики](#6-сквозные-практики)
7. [Риски и смягчение](#7-риски-и-смягчение)
8. [Бюджеты производительности](#8-бюджеты-производительности)
9. [Глоссарий](#9-глоссарий)
10. [Журнал изменений плана](#10-журнал-изменений-плана)

---

## 1. Как пользоваться планом

### Легенда статусов

| Знак | Значение |
|---|---|
| ⬜ | не начато |
| 🔄 | в работе |
| ✅ | готово (критерии готовности выполнены) |
| ⏸ | отложено (причина указана рядом) |
| ❌ | отменено (причина указана рядом) |

Задачи внутри подэтапов — чекбоксы `- [ ]` / `- [x]`. Пометка «(опц.)» — задача не блокирует критерии готовности этапа.

### Текущая позиция

> **Этап 0 → подэтап 0.5 «CI».**
> Подэтапы 0.1–0.4 выполнены 2026-10-10. Соглашения закреплены: `.editorconfig` (file-scoped namespaces, порядок `using`, `_camelCase`, `Async`; неиспользуемые `using` — ошибка сборки), CONTRIBUTING.md (ветки, Conventional Commits, шаблон теста), фикстуры `TemporaryDirectory` и `TemporaryDatabase` в `tests/DiskApp.Testing`. 33 теста зелёные, `dotnet format --verify-no-changes` проходит. Дальше — CI (0.5), затем спайки (0.6).

### Протокол сессии

Каждая рабочая сессия идёт по одному сценарию:

1. Прочитать «Текущую позицию» и «Открытые вопросы».
2. Взять первый незакрытый подэтап текущего этапа. Не брать задачи из следующих этапов, пока текущий не достиг критериев готовности (исключение — заранее отмеченные «(опц.)» и исправление дефектов).
3. Перед нетривиальным подэтапом — короткий план реализации на 3–7 пунктов и явные допущения.
4. Реализовать → тесты → проверка Definition of Done.
5. Обновить PLAN.md: чекбоксы, статус этапа, «Текущая позиция», при необходимости — «Открытые вопросы» и «Журнал изменений плана».
6. Краткий отчёт: что сделано, что дальше, что требует решения пользователя.

### Definition of Done подэтапа

1. Код компилируется без предупреждений (`TreatWarningsAsErrors`), `dotnet format --verify-no-changes` проходит.
2. Для нового кода написаны тесты (модульные и/или интеграционные); `dotnet test` зелёный целиком.
3. Для UI-работ пройден ручной сценарий из `docs/QA_CHECKLIST.md`.
4. Документация обновлена: README (если менялось поведение продукта), ARCHITECTURE/ADR (если менялись решения), USER_GUIDE (если менялся пользовательский сценарий).
5. PLAN.md обновлён.
6. Изменения закоммичены в формате Conventional Commits; один подэтап = 1–3 коммита.

### Правила изменения плана

- Решение из раздела 2 можно пересмотреть только с записью в «Журнал изменений плана» и новым ADR.
- Новые задачи добавляются в соответствующий подэтап; новая крупная функция — в этап 12 или отдельным этапом.
- Бюджеты производительности не ослабляются ради функции; функция уходит за флаг или откладывается.

---

## 2. Ключевые решения

Решения приняты по умолчанию, чтобы не блокировать старт. Каждое можно пересмотреть (см. «Открытые вопросы»).

| # | Вопрос | Решение | Почему | Альтернатива |
|---|---|---|---|---|
| 1 | Связь UI с ядром | Loopback HTTP + SignalR к Kestrel внутри процесса десктопа | Один путь кода для десктопа, Android и Web; API проверяется ежедневным использованием; сервер выносится в службу без правки UI | Прямые вызовы Core из UI (на микросекунды быстрее, два пути кода) |
| 2 | Стиль API | Minimal API + endpoint groups | Легче контроллеров, меньше рефлексии, дружит с триммингом | Controllers |
| 3 | Доступ к SQLite | Microsoft.Data.Sqlite + Dapper, SQL-миграции во встроенных ресурсах | Полный контроль над FTS5 и PRAGMA, быстрые пакетные вставки, малый след | EF Core 10 (удобные миграции, медленнее bulk, FTS5 всё равно raw SQL) |
| 4 | Полнотекстовый поиск | FTS5 с токенизатором `trigram` по имени файла (путь — по результатам спайка S4) | Поиск подстрок без учёта регистра за миллисекунды | `LIKE '%x%'` (полное сканирование), индекс в памяти |
| 5 | Тема UI | FluentAvaloniaUI 3.x поверх Avalonia 12.x | Контролы WinUI 3 (NavigationView, ContentDialog, InfoBar, SettingsExpander), системный акцент, Mica; FluentAvaloniaUI 3.1 собран под Avalonia 12.1 | Чистый FluentTheme Avalonia и собственные контролы |
| 6 | MVVM | CommunityToolkit.Mvvm (source generators) | Минимум шаблонного кода, нет тяжёлых зависимостей | ReactiveUI |
| 7 | Глобальные хоткеи (Windows) | `RegisterHotKey` через P/Invoke, приём `WM_HOTKEY` через `Win32Properties.AddWndProcHookCallback` | Без глобальных клавиатурных хуков — надёжнее и не раздражает антивирусы | SharpHook (понадобится для Linux/macOS) |
| 8 | Трей | Avalonia `TrayIcon` + `NativeMenu` | Кроссплатформенно из коробки | H.NotifyIcon |
| 9 | Установщик и обновления | Velopack + GitHub Releases | Дельта-обновления, одна команда сборки, портативная сборка тем же инструментом | MSIX (ограничения на интеграцию с оболочкой), Inno Setup (без автообновлений) |
| 10 | Публикация | Self-contained, single-file, ReadyToRun; NativeAOT — только эксперимент | Быстрый старт без риска для рефлексии Avalonia/SignalR | Framework-dependent (меньше размер, нужен установленный runtime) |
| 11 | Логирование | Serilog → файлы с ротацией | Стандарт де-факто, структурные логи | NLog, только Microsoft.Extensions.Logging |
| 12 | Тесты | xunit.v3 + Shouldly + NSubstitute на Microsoft.Testing.Platform; headless-тесты Avalonia 12 на xunit v3 (пока `Avalonia.Headless.XUnit` не собран под xunit v3 4.x — через `HeadlessUnitTestSession` из `Avalonia.Headless`); BenchmarkDotNet | FluentAssertions с v8 платный для коммерческого использования; headless-пакет Avalonia 12 требует xunit v3; на .NET 10 SDK `dotnet test` для xunit v3 работает только через Microsoft.Testing.Platform | FluentAssertions 7.x, Moq, NUnit |
| 13 | Формат решения | `.slnx` | Поддерживается .NET 10 SDK и VS 2026, читаемый diff | `.sln` |
| 14 | Порядок платформ | Windows → проверка Linux/macOS → Android → Web | Целевая аудитория и стиль Windows 11 | Кроссплатформенность с первого дня |
| 15 | Хоткей по умолчанию | `Ctrl+Alt+Space` | `Alt+Space` занят системным меню и PowerToys Run, `Ctrl+Space` конфликтует с IME | `Win+Shift+Space` и др. |
| 16 | Удаление файлов | Только в корзину; безвозвратное — явный флаг операции | Нельзя потерять данные из-за одного нажатия | Прямое удаление |
| 17 | Резервное копирование | Данные приложения — снимок БД через SQLite Online Backup API (`SqliteConnection.BackupDatabase`) в zip с манифестом и контрольными суммами; файлы пользователя — задания поверх `FileOperationService` с инкрементальностью из индекса | Консистентная копия без остановки индексации и без ручной работы с WAL; одна очередь операций и один прогресс для всего | Копирование файла БД после `wal_checkpoint`; сторонние утилиты бэкапа |
| 18 | Подсказки «куда положить» при разборе входящих | Объяснимая эвристика без ML: правила → история решений по признакам (расширение, вид, домен источника, токены имени) → недавние и избранные назначения → подпапки накопителя → шаблоны по виду | Предсказуемо, объяснимо («5 похожих файлов отправлены сюда»), обучается с первых решений, работает офлайн и быстро | Классификатор ML (непрозрачен, нужен корпус); только ручные правила (не обучается) |
| 19 | Назначения при разборе | Набор именованных «профильных складов» с профилем и закреплёнными номерами 1–9; любая другая папка — через поиск; склад может быть складом следующего уровня (каскад) | Закреплённые номера дают мышечную память и скорость «одна клавиша на файл»; профили дают уверенные подсказки ещё до обучения; каскад покрывает «склады поменьше» | Свободный выбор папки каждый раз (медленно); жёсткая структура подпапок внутри «Загрузок» (негибко) |
| 20 | Напоминания о разборе | Планировщик «умного момента» на стороне интерфейса: детектор занятости (простой ввода, `SHQueryUserNotificationState`, звонок по микрофону/камере, полный экран, категория окна) + отсрочка с нарастанием + лимиты частоты + обучение по реакциям; без модальных окон и звука | Напоминание должно догонять пользователя, когда он свободен, и молчать, когда занят; фиксированные сроки отсрочки занятость не угадывают | Только расписание и порог (навязчиво и невпопад); только ручной запуск (файлы копятся) |

---

## 3. Открытые вопросы

Пока ответов нет, работа идёт по допущениям из раздела 2.

| # | Вопрос | Текущее допущение | Когда нужен ответ |
|---|---|---|---|
| 1 | Лицензия проекта | ✅ Решено 2026-10-10: MIT | — |
| 2 | Название «DiskApp» — рабочее или финальное? Влияет на имя exe, протокол `diskapp://`, папку данных, ключи реестра | Рабочее, всё называется DiskApp | До этапа 6 |
| 3 | Нужны ли Linux/macOS в 1.0? | Нет, только Windows; абстракции сохраняются | До этапа 6 |
| 4 | Quick Switch для системных диалогов — в 1.0 или позже? | Экспериментально в этапе 8, за флагом | Этап 8 |
| 5 | Поиск по содержимому — в 1.0? | Нет, этап 12 | Этап 10 (заморозка) |
| 6 | Целые диски: обычного сканирования достаточно или сразу USN Journal? | Обычное сканирование; USN — этап 12 | После спайка S4 и этапа 2 |
| 7 | Репозиторий на GitHub: когда создаём, публичный или приватный? Нужен для CI и релизов | ✅ Решено 2026-10-10: пользователь создал публичный репозиторий https://github.com/IichiKohuro/DiskApp; подключён как `origin`, ветка `main` отправлена. CI (0.5) и защита ветки теперь возможны | — |
| 8 | Язык интерфейса по умолчанию | По языку системы, RU и EN | Этап 9 |
| 9 | Dapper vs EF Core — есть ли предпочтение? | Dapper | Этап 1 |
| 10 | Подпись кода (платный сертификат) | Без подписи, с инструкцией про SmartScreen | Этап 11 |
| 11 | IDE: Visual Studio 2026, Rider или VS Code? | ✅ Решено 2026-10-10: Visual Studio 2026 Community | — |
| 12 | Резервное копирование: достаточно ли в 1.0 копий данных приложения, а файлы пользователя — в v1.x? Нужны ли шифрование копий и прямые облачные назначения (API OneDrive/Google Drive)? | Данные приложения — v1.0 (16.1), файлы — v1.x (16.2); шифрование по желанию; облака только через папки клиентов синхронизации | Этап 10 (заморозка функций) |
| 13 | Разбор входящих: какой общий склад и какие профильные склады предлагать по умолчанию, когда напоминать? Нужен ли каскад (склад внутри склада) в первой версии? | ✅ Решено 2026-10-10 (допущения подтверждены): общий склад — «Загрузки» («Рабочий стол» по желанию); профильные склады по умолчанию — Документы, Картинки, Видео, Музыка, Архивы, Установщики, Проекты на стандартных папках Windows; напоминание ежедневно в 18:00 и при ≥ 10 неразобранных; каскад — в модели сразу, в интерфейсе после минимального среза | — |

---

## 4. Вехи и версии

| Версия | Этапы | Что получаем | Объём |
|---|---|---|---|
| v0.1 | 0 | Окружение, каркас решения, CI, снятые риски | M |
| v0.2 | 1–2 | Домен, SQLite, сканирование и слежение за файловой системой | L |
| v0.3 | 3–7 | Поиск, API + SignalR, клиент, трей, окно быстрого поиска. **Первая сборка для ежедневного использования** | XL |
| v0.4 | 8, 17 | Главное окно, обзор, файловые операции, «добавить» и «сохранить», разбор входящих (папки-накопители и быстрая сортировка) | XL |
| v0.5 | 9 | Настройки, локализация, онбординг, доступность | L |
| v1.0 | 10, 16.1, 11 | Производительность, надёжность, резервное копирование данных приложения, установщик, автообновление | L |
| v1.x | 12, 16.2 | Предпросмотр, поиск по содержимому, дубликаты, USN, расширения, Linux/macOS, резервное копирование файлов пользователя | XL |
| v2.0 | 13 | Безопасный удалённый доступ | L |
| v2.x | 14–15 | Android и Web | XL |

Объём: S — до дня работы, M — 2–3 дня, L — до недели, XL — больше недели. Оценки ориентировочные и относительные.

---

## 5. Этапы

### Этап 0 — Окружение и каркас репозитория

**Статус:** 🔄 (0.1–0.4 готовы) · **Объём:** M · **Версия:** v0.1 · **Зависит от:** —

**Цель.** На чистой машине получить рабочее окружение, собирающееся решение с тестами и CI, и снять технические риски короткими спайками до начала основной разработки.

#### 0.1 Установка инструментов

- [x] .NET SDK 10: установлен 10.0.401 (`winget install --id Microsoft.DotNet.SDK.10 -e`)
- [x] Git: установлен 2.55.0 (`winget install --id Git.Git -e`); настроены `init.defaultBranch main` и `core.autocrlf true`
- [x] Git: `user.name` и `user.email` заданы пользователем глобально (проверено 2026-10-10)
- [x] IDE: Visual Studio Community 2026 18.10 установлена в `C:\Program Files\Microsoft Visual Studio\18\Community` с нагрузками `Microsoft.VisualStudio.Workload.ManagedDesktop` и `Microsoft.VisualStudio.Workload.NetWeb` (`winget install --id Microsoft.VisualStudio.Community -e --override "--passive --norestart --wait --add … --includeRecommended"`). Альтернативы (Rider, VS Code) не ставились
- [x] Расширение «Avalonia for Visual Studio» 12.3.2.0 (`AvaloniaTeam.AvaloniaVS`, XAML-превью) установлено для текущего пользователя: VSIX скачан с Marketplace, поставлен через `VSIXInstaller /quiet`
- [x] Шаблоны Avalonia: установлены Avalonia.Templates 12.1.4 (`dotnet new install Avalonia.Templates`)
- [x] Дополнительно установлены: GitHub CLI 2.102.0 (`GitHub.cli`), PowerShell 7.6.6 (`Microsoft.PowerShell`), DB Browser for SQLite 3.13.1 (`DBBrowserForSQLite.DBBrowserForSQLite`)
- [x] Проверка окружения: тестовый `avalonia.app` (Avalonia 12.1.4, net10.0) собран без предупреждений и ошибок; папка удалена
- [x] Терминал перезапущен: `dotnet` и `git` находятся без ручного обновления PATH (проверено 2026-10-10)

#### 0.2 Репозиторий

- [x] `git init -b main`; `.gitignore` и `.editorconfig` из шаблонов `dotnet new` с разделами DiskApp (file-scoped namespaces, скобки, `var`, `_camelCase`, интерфейсы с `I`, суффикс `Async` — как предупреждения; данные portable-режима, логи и служебные файлы SQLite в `.gitignore`)
- [x] `global.json`: SDK 10.0.401, `rollForward: latestFeature`
- [x] LICENSE — MIT, взят из репозитория GitHub (правообладатель Iichi Kohuro). CHANGELOG.md в формате Keep a Changelog с разделом Unreleased
- [x] CLAUDE.md: правила работы по плану, команды сборки и тестов, соглашения, карта документов
- [x] `docs/`: ARCHITECTURE.md (процессы, проекты, запуск, данные с ER-схемой, индексация, поиск, API, операции, интерфейс, платформенные сервисы, разбор входящих, резервное копирование, безопасность, поставка, таблица замеров), `ADR/README.md` (правила, индекс, шаблон), `ADR/0001-architecture-style.md` (решения 1–4 и 13 с планом проверки спайками), QA_CHECKLIST.md (каркас по этапам 6–17), FEEDBACK.md (шаблон журнала dogfooding)
- [x] Репозиторий на GitHub: https://github.com/IichiKohuro/DiskApp (создан пользователем, публичный), подключён как `origin`; защита ветки `main` — вместе с CI в 0.5
- [x] Подпись автора задана глобально по данным пользователя; первый коммит `06eb56a chore(repo): project documentation and repository skeleton` сделан поверх начального коммита GitHub и отправлен в `origin/main`

#### 0.3 Каркас решения

- [x] `dotnet new sln -n DiskApp --format slnx`; папки решения `src`, `tests` и `Solution Items` (корневые файлы)
- [x] `Directory.Build.props`: `TargetFramework=net10.0`, `LangVersion=latest`, `Nullable=enable`, `ImplicitUsings=enable`, `TreatWarningsAsErrors=true`, `AnalysisLevel=latest-recommended`, `Deterministic=true`, общие метаданные (Authors, Product, Copyright). Дополнительно: `EnforceCodeStyleInBuild`, `ContinuousIntegrationBuild` в GitHub Actions, предупреждения NuGet об уязвимостях (NU1901–NU1904) не считаются ошибками; `tests/Directory.Build.props` и `.targets` — общие свойства и пакеты тестов
- [x] `Directory.Packages.props`: централизованное управление версиями пакетов (транзитивное закрепление выключено)
- [x] Проекты `src/`: `DiskApp.Core`, `DiskApp.Infrastructure`, `DiskApp.Contracts`, `DiskApp.Server` (classlib + `FrameworkReference Microsoft.AspNetCore.App`), `DiskApp.Client`, `DiskApp.UI` (classlib + Avalonia: `App` с `FluentAvaloniaTheme`, `Views/MainView`), `DiskApp.Desktop` (по шаблону `avalonia.app` 12.1.4: `OutputType=WinExe`, `AssemblyName=DiskApp`, `app.manifest` с Per-Monitor V2, `ApplicationIcon=assets/DiskApp.ico` — заглушка до набора иконок в 6.3; `MainWindow` и явный `ClassicDesktopStyleApplicationLifetime` в `Program`). В библиотеках — якорный тип `AssemblyMarker`
- [x] Проекты `tests/`: `DiskApp.Core.Tests` (архитектурные правила), `DiskApp.Infrastructure.Tests` (версия SQLite ≥ 3.34, `ENABLE_FTS5`, таблица FTS5 `trigram`, Dapper), `DiskApp.Server.Tests` (Kestrel на `127.0.0.1:0`, запрос по loopback), `DiskApp.UI.Tests` (headless: тема FluentAvalonia загружена, `MainView` показывается), `DiskApp.Benchmarks` (BenchmarkDotNet, `BenchmarkSwitcher`, вне `dotnet test`). Раннер — Microsoft.Testing.Platform (`global.json` → `test.runner`): на .NET 10 SDK `dotnet test` через VSTest для xunit v3 больше не поддерживается. `Avalonia.Headless.XUnit` 12.1.4 собран под xunit v3 3.2 и падает на 4.0 (`MissingMethodException` в `AvaloniaFactDiscoverer`), поэтому UI-тесты идут через `HeadlessUnitTestSession` из `Avalonia.Headless` (помощник `HeadlessTest.RunAsync`); вернуться к `[AvaloniaFact]`, когда выйдет сборка под xunit v3 4.x
- [x] Ссылки между проектами по таблице слоёв из README; архитектурный тест собственный, без NetArchTest: `LayerDependencyTests` читает `ProjectReference` из всех `src/*.csproj` и сверяет с таблицей слоёв (и состав проектов), `GetReferencedAssemblies` сборки `Core` не содержит проектов решения, Avalonia, ASP.NET Core и SQLite; `UiDependencyTests` — сборка `UI` не ссылается на `Core`, `Infrastructure`, `Server`
- [x] Базовые пакеты: Avalonia, Avalonia.Desktop, Avalonia.Fonts.Inter (fallback), FluentAvaloniaUI, FluentIcons.Avalonia, CommunityToolkit.Mvvm, Microsoft.Extensions.Hosting, Microsoft.Data.Sqlite, Dapper, Serilog + Serilog.Sinks.File + Serilog.Extensions.Hosting, Microsoft.AspNetCore.SignalR.Client, System.IO.Abstractions, xunit.v3, Shouldly, NSubstitute. Уточнения: Avalonia.Themes.Fluent не подключён — `FluentAvaloniaTheme` заменяет `FluentTheme` целиком; coverlet.collector (сборщик VSTest) заменён на Microsoft.Testing.Extensions.CodeCoverage + TrxReport; добавлены AvaloniaUI.DiagnosticsSupport (инструменты разработчика Avalonia 12, только Debug, как в шаблоне) и Avalonia.Headless
  - Стабильные версии на nuget.org по состоянию на 2026-10-10: Avalonia 12.1.4 · FluentAvaloniaUI 3.1.0 (требует Avalonia ≥ 12.1.0) · FluentIcons.Avalonia 2.1.343 · CommunityToolkit.Mvvm 8.4.2 · AvaloniaUI.DiagnosticsSupport 2.2.3 · Microsoft.Extensions.Hosting 10.0.12 · Microsoft.Data.Sqlite 10.0.12 · Dapper 2.1.89 · Serilog 4.4.0 · Serilog.Sinks.File 7.0.0 · Serilog.Extensions.Hosting 10.0.0 · Microsoft.AspNetCore.SignalR.Client 10.0.12 · Microsoft.Extensions.Http.Resilience 10.10.0 · System.IO.Abstractions 22.3.0 · Scalar.AspNetCore 2.17.14 · xunit.v3 4.0.2 · Avalonia.Headless 12.1.4 (Avalonia.Headless.XUnit 12.1.4 несовместим с xunit v3 4.x) · Microsoft.Testing.Extensions.CodeCoverage 18.12.0 · Microsoft.Testing.Extensions.TrxReport 2.5.1 · Shouldly 4.3.0 · NSubstitute 6.2.0 · BenchmarkDotNet 0.15.8 · NetArchTest.Rules 1.3.2 · Velopack 1.2.161 · MinVer 8.0.0 · SharpHook 8.0.0
- [x] `dotnet build` (Debug и Release) и `dotnet test` зелёные (16 тестов), `dotnet format --verify-no-changes` проходит; `DiskApp.Desktop` запускается и показывает пустое окно с заголовком «DiskApp» (проверено под Xvfb на Linux; запуск на Windows — у пользователя)

#### 0.4 Качество кода и соглашения

- [x] `.editorconfig`: порядок `using` (System первыми, группы через пустую строку), file-scoped namespaces, поля `_camelCase` (статические тоже, без `s_`), суффикс `Async`, `var` по правилам, предпочтения выражений — как подсказки, которые применяет `dotnet format`. Дополнительно: неиспользуемые `using` (IDE0005) и неявные модификаторы доступа — предупреждения, то есть ошибки сборки; для IDE0005 в `Directory.Build.props` включён `GenerateDocumentationFile` (CS1591 подавлен); исправлена ошибка шаблона (`type_parameters` были привязаны к `namespace`)
- [x] `dotnet format --verify-no-changes` проходит локально (новое правило сразу поймало лишний `using` в `TestAppBuilder.cs`)
- [x] Шаблон теста: Arrange/Act/Assert, имена `Method_Scenario_Expected` — описан в CONTRIBUTING.md (раздел 7) и показан на тестах фикстур. Фикстуры — в новом проекте `tests/DiskApp.Testing` (библиотека, не тестовый проект; ссылка и глобальный `using` для всех тестовых проектов — из `tests/Directory.Build.targets`): `TemporaryDirectory` (уникальная папка в `%TEMP%DiskApp.Tests`, пути только внутри неё, удаление с повторами и снятием «только для чтения») и `TemporaryDatabase` (файловая SQLite в собственной папке, сброс пула соединений при очистке); 17 тестов на фикстуры
- [x] Соглашения в CONTRIBUTING.md: единица работы и DoD, ветки `feat/`, `fix/`, `chore/`, `docs/`, Conventional Commits (типы, области, примеры), PR, команды проверки, стиль кода, тесты и фикстуры, карта документации, зависимости. Ссылки из README, CLAUDE.md и PLAN

#### 0.5 CI

- [ ] `.github/workflows/ci.yml`: `windows-latest`, `actions/setup-dotnet` по `global.json`, кэш NuGet, `dotnet restore` → `build -c Release` → `test --no-build` → `format --verify-no-changes`, публикация результатов тестов (TRX) и покрытия
- [ ] Бейджи CI в README (после появления репозитория)
- [ ] Правило ветки `main`: только через PR с зелёным CI

#### 0.6 Технические спайки

Одноразовый код в `spikes/` (не в решении, в `.gitignore` или отдельная ветка). Каждый спайк — не больше половины дня. Результат — цифры и решение в ADR.

- [ ] **S1 — Стиль Windows 11 в Avalonia.** Окно с Mica (`TransparencyLevelHint`) и собственным заголовком (`ExtendClientAreaToDecorationsHint`); безрамочный popup с Acrylic и скруглёнными углами через `DwmSetWindowAttribute(DWMWA_WINDOW_CORNER_PREFERENCE)`; автоматическая светлая/тёмная тема; системный акцент через FluentAvalonia; замер времени показа заранее созданного скрытого окна
- [ ] **S2 — Трей, хоткей, один экземпляр.** `TrayIcon` + `NativeMenu`; `RegisterHotKey` и приём `WM_HOTKEY` через `Win32Properties.AddWndProcHookCallback`; `Mutex` + `NamedPipeServerStream` для передачи аргументов второму экземпляру
- [ ] **S3 — Kestrel внутри Avalonia-процесса.** Minimal API + SignalR на `http://127.0.0.1:0`, получение фактического порта, общий DI-контейнер, подключение клиента; замер времени старта и памяти
- [ ] **S4 — SQLite FTS5 trigram.** Генератор 1 млн синтетических записей; скорость вставки пакетами, размер БД (имя vs имя+путь в FTS), задержка поиска для 2, 3, 5 символов, сравнение с `LIKE`; решение о составе FTS-таблицы
- [ ] **S5 (опц.) — `FileSystemWatcher` под нагрузкой.** 10 тыс. быстрых изменений, переполнение буфера, переименование поддерева с 5 тыс. файлов
- [ ] Выводы → `docs/ADR/0002…0005`; корректировка решений 1, 4, 5, 7 при необходимости; обновление бюджетов

#### 0.7 Документация этапа

- [ ] README: раздел «Статус», бейджи, уточнённые цифры из спайков
- [ ] ARCHITECTURE.md: диаграммы, решения спайков, схема запуска процесса

**Критерии готовности этапа 0**

- `dotnet --info` показывает SDK 10.0.x, `git --version` работает, IDE открывает решение с XAML-превью.
- `dotnet build`, `dotnet test`, `dotnet format --verify-no-changes` зелёные локально и в CI (если репозиторий создан).
- Все спайки S1–S4 выполнены, результаты и решения записаны в ADR; ни один блокирующий риск не остался без плана.

---

### Этап 1 — Ядро: домен и хранилище

**Статус:** ⬜ · **Объём:** M · **Версия:** v0.2 · **Зависит от:** 0

**Цель.** Модель предметной области, интерфейсы инфраструктуры и работающее SQLite-хранилище с миграциями, одним писателем и тестами.

#### 1.1 Модель домена (`DiskApp.Core`)

- [ ] `Root`: Id, Path, Label, IsEnabled, IncludeHidden, IncludeSystem, MaxDepth, LastScanUtc, Status (Online / Offline / Scanning / Paused / Error), ErrorMessage
- [ ] `FileEntry`: Id, RootId, ParentId?, Name, Extension (нижний регистр, без точки), IsDirectory, Size, CreatedUtc, ModifiedUtc, Attributes, RelPath, FullPath (вычисляемый), Kind
- [ ] `FileKind`: Image, Video, Audio, Document, Spreadsheet, Presentation, Pdf, Archive, Code, Executable, Shortcut, Folder, Other; таблица расширение → вид с возможностью переопределения в настройках
- [ ] `Tag` (Id, Name, Color), `Favorite` (EntryId, Order, PinnedUtc), `UsageStat` (EntryId, OpenCount, LastOpenedUtc), `Collection` (Id, Name, Kind: Manual | SavedQuery, Query?), `ExclusionRule` (Pattern, Kind: Glob | Hidden | System | Size | Depth)
- [ ] `SearchQuery` (термы, фразы, фильтры, отрицания, сортировка, лимит, курсор), `SearchHit` (Entry, Score, диапазоны подсветки имени и пути, причина ранга для отладки), `SearchPage`
- [ ] Ошибки домена через `Result<T>`/коды, без исключений в горячих путях
- [ ] Нормализация путей: регистр буквы диска, разделители, завершающий слэш, префикс `\\?\` для длинных путей, UNC; сравнение путей без учёта регистра на Windows
- [ ] Юнит-тесты: классификация расширений, валидация корня (абсолютный путь, вложенность корней — предупреждение), нормализация путей, построение `FullPath`

#### 1.2 Интерфейсы инфраструктуры (`DiskApp.Core`)

- [ ] Репозитории: `IFileIndexRepository` (bulk upsert, delete subtree, rename subtree, children, by id / by path, counts), `IRootRepository`, `IUsageRepository`, `IFavoriteRepository`, `ITagRepository`, `ICollectionRepository`, `IQueryHistoryRepository`
- [ ] `ISettingsStore<T>` с уведомлением об изменениях; `IClock`; `IAppPaths`; `IFileSystem` (System.IO.Abstractions); `IFileSystemWatcherFactory`
- [ ] `IPlatformShell`: Open, OpenWith, RevealInExplorer, ShowProperties, MoveToRecycleBin, RunAsAdmin, CopyPathsToClipboard, CopyFilesToClipboard
- [ ] `IIconProvider`: иконка по расширению, иконка конкретного файла, миниатюра заданного размера
- [ ] `IIndexEventBus`: EntriesChanged (пакет), IndexProgress, RootStatusChanged, OperationProgress, OperationCompleted — внутренняя шина, из которой сервер транслирует события в SignalR

#### 1.3 SQLite: схема и миграции (`DiskApp.Infrastructure`)

- [ ] Фабрика подключений: путь из `IAppPaths`; PRAGMA при открытии: `journal_mode=WAL`, `synchronous=NORMAL`, `temp_store=MEMORY`, `cache_size=-65536`, `mmap_size=268435456`, `foreign_keys=ON`, `busy_timeout=5000`
- [ ] Миграции: таблица `schema_version`; скрипты `V001__init.sql`, … как встроенные ресурсы; применение по порядку в транзакции при старте; тест апгрейда от V001 до текущей
- [ ] Схема V001: `roots`; `entries` с `UNIQUE(root_id, rel_path)` и индексами `(root_id, parent_id)`, `name COLLATE NOCASE`, `ext`, `modified_utc`, `size`, `kind`; `entries_fts` (FTS5, `tokenize='trigram'`, external content на `entries`, триггеры синхронизации); `usage`; `favorites`; `tags`; `entry_tags`; `collections`; `collection_items`; `query_history`; `meta`; `scan_log`
- [ ] Один писатель: `SqliteWriteQueue` на `Channel` — все записи проходят через него; чтение через пул соединений
- [ ] Проверка `PRAGMA quick_check` при старте; восстановление: переименовать повреждённый файл в `index.corrupt-<дата>.db`, создать новый, пометить корни на полное сканирование, уведомить
- [ ] `VACUUM`/`PRAGMA optimize` по расписанию при простое

#### 1.4 Репозитории (`DiskApp.Infrastructure`)

- [ ] `SqliteFileIndexRepository`: upsert пакетами (подготовленные команды, транзакция на пакет), `DeleteSubtree(rootId, relPathPrefix)`, `RenameSubtree(rootId, oldPrefix, newPrefix)` одним `UPDATE`, `MarkSweep(rootId, scanId)`, `GetChildren(parentId, sort, page)`, `GetByPath`, `GetStats(rootId)`
- [ ] Остальные репозитории; `UsageRepository.RecordOpen` с инкрементом и datetime, расчёт decay на стороне запроса
- [ ] Интеграционные тесты на временном файле БД (фикстура с удалением): CRUD, уникальность, каскады, rename subtree, mark-sweep, параллельные чтения во время записи, `quick_check` после нагрузки
- [ ] Микробенчмарки: вставка 100 тыс. записей ≤ 5 с; rename subtree на 50 тыс. записей ≤ 200 мс

#### 1.5 Настройки и пути

- [ ] `AppPaths`: `%LocalAppData%\DiskApp` или `.\data` при наличии файла `portable` рядом с exe либо флага `--portable`; создание `logs`, `cache`, `cache\icons`, `cache\thumbs`
- [ ] `settings.json` через System.Text.Json source generation; версия схемы настроек и миграция; атомарная запись (временный файл + переименование); уведомления об изменениях
- [ ] Значения по умолчанию: хоткей, тема, фон, исключения, лимиты поиска, поведение трея
- [ ] Тесты: чтение/запись, миграция старой версии, повреждённый файл → резервная копия и значения по умолчанию

**Критерии готовности этапа 1**

- Репозитории покрыты интеграционными тестами; покрытие `Core` ≥ 80 %.
- Бенчмарки вставки и переименования в пределах целей.
- Схема БД задокументирована в ARCHITECTURE.md (ER-диаграмма Mermaid).

---

### Этап 2 — Индексация и слежение за файловой системой

**Статус:** ⬜ · **Объём:** L · **Версия:** v0.2 · **Зависит от:** 1

**Цель.** Полный и инкрементальный индекс выбранных корней, устойчивый к сбоям и нагрузке, с прогрессом, паузой и управлением.

#### 2.1 Сканер

- [ ] `DirectoryScanner`: итеративный обход на стеке; `EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip по настройкам, ReturnSpecialDirectories = false }`; `FileSystemEnumerable<T>` для минимума аллокаций
- [ ] Параллелизм по подпапкам верхнего уровня; степень параллелизма по типу диска (SSD — до 4, HDD и сеть — 1–2); настройка
- [ ] Пропуск reparse points (junction, symlink) по умолчанию; защита от циклов по идентификаторам (том + file id); ограничение глубины
- [ ] `ExclusionMatcher`: glob (Microsoft.Extensions.FileSystemGlobbing либо собственный с кэшем), стандартный набор (`$RECYCLE.BIN`, `System Volume Information`, `node_modules`, `.git`, `bin`, `obj`, `*.tmp` — настраивается), скрытые/системные, по размеру
- [ ] Прогресс `IndexProgress { RootId, Phase (Enumerating / Writing / Sweeping / Done), Scanned, Written, CurrentPath, RatePerSec, Elapsed }` не чаще 4 раз в секунду
- [ ] Отмена и пауза/возобновление (`PauseToken`); фоновый приоритет (`ThreadPriority.BelowNormal`; на Windows — `THREAD_MODE_BACKGROUND_BEGIN` для снижения приоритета ввода-вывода)
- [ ] Ошибки доступа записываются в `scan_log` и не прерывают обход

#### 2.2 Конвейер записи

- [ ] Producer/consumer через ограниченный `Channel<EntryBatch>` (backpressure); пакеты 5–10 тыс.; единственный писатель
- [ ] Полное сканирование как mark-and-sweep: `scan_id` на записях, в конце удаление устаревших — так находятся файлы, удалённые пока приложение не работало
- [ ] Пропуск неизменённых строк (size + mtime совпадают) — меньше записи в WAL
- [ ] События `EntriesChanged` агрегируются по папкам, а не по каждой записи

#### 2.3 Слежение (`FileSystemWatcher`)

- [ ] `RootWatcher` на каждый корень: `IncludeSubdirectories`, `NotifyFilters = FileName | DirectoryName | LastWrite | Size | Attributes`, `InternalBufferSize = 64 КБ`
- [ ] Коалесцирование: очередь с debounce 300–500 мс на путь; объединение Created + Changed; цепочки Renamed; игнор временных файлов (`~$`, `.tmp`, `.crdownload`, `.part`) по настройке
- [ ] Применение: create / update через `stat`, delete как subtree, rename как subtree-prefix; перемещение внутри корня — как rename
- [ ] Переполнение буфера (`Error` / `InternalBufferOverflowException`) → пересканирование корня или известного поддерева с низким приоритетом
- [ ] Исчезновение диска или сети → корень Offline, watcher останавливается; опрос доступности раз в N секунд и перезапуск с последующей сверкой
- [ ] UNC-корни: предупреждение о ненадёжности событий и более частая плановая сверка

#### 2.4 Оркестрация (`IndexerService`, hosted service)

- [ ] При старте: загрузить корни → запустить watcher'ы → для каждого корня решить: полное сканирование (первый раз, давно, после сбоя) или сверка
- [ ] Сверка: обход только папок, у которых mtime отличается от сохранённого; полная сверка по расписанию (по умолчанию ночью или при простое)
- [ ] Очередь задач индексации с приоритетами: изменения от watcher > сверка > полное сканирование; одна тяжёлая задача на физический диск одновременно
- [ ] Управление: `PauseAll`, `ResumeAll`, `RescanRoot`, `RebuildAll`; статус на корень и агрегированный статус для иконки трея
- [ ] (опц.) Пауза тяжёлых задач при работе от батареи (`GetSystemPowerStatus`)

#### 2.5 Метаданные

- [ ] Вид файла по расширению; признак заглушки OneDrive/облака по атрибутам `RECALL_ON_DATA_ACCESS` и `OFFLINE` — содержимое таких файлов не читается
- [ ] (опц.) Лёгкие метаданные (размеры изображений, длительность медиа) в отдельной низкоприоритетной очереди

#### 2.6 Тесты и бенчмарки

- [ ] Генератор тестовых деревьев: N файлов, глубина, junction-цикл, папка без доступа, путь длиннее 260 символов, Unicode-имена
- [ ] Тесты сканера: полнота, исключения, цикл не зацикливает, недоступное не валит, отмена и пауза
- [ ] Тесты watcher'а на реальной файловой системе (с ожиданием и повторами): create / modify / delete / rename файла и папки; 1 000 быстрых изменений → индекс консистентен
- [ ] Бенчмарк: 100 тыс. файлов на SSD ≤ 60 с; пик памяти сканера ≤ 100 МБ

**Критерии готовности этапа 2**

- Индекс 100 тыс. файлов строится ≤ 60 с; изменения отражаются ≤ 1 с; хендлы и память не растут (проверка счётчиками).
- Отключение диска и переполнение буфера не приводят к падению или расхождению индекса.
- Пауза, возобновление и пересканирование работают через `IndexerService`.

---

### Этап 3 — Поиск

**Статус:** ⬜ · **Объём:** L · **Версия:** v0.3 · **Зависит от:** 1, 2

**Цель.** Быстрый и предсказуемый поиск с фильтрами и ранжированием, пригодный для поиска по мере ввода.

#### 3.1 Парсер запроса

- [ ] Токенизатор: слова, фразы в кавычках, фильтры `ключ:значение`, отрицание `-`, диапазоны (`size:>10mb`, `size:1mb..5mb`, `modified:2026-01..2026-03`), (опц.) `/regex/`
- [ ] Фильтры: `ext`, `type`, `in`, `root`, `size`, `modified`, `created`, `is`, `tag`, `fav`; синонимы и значения на RU и EN (`type:картинки`, `modified:неделя`)
- [ ] Устойчивость: незакрытые кавычки, неизвестные фильтры трактуются как текст; позиции токенов для подсветки и подсказок
- [ ] Тесты: таблица из ≥ 60 случаев; round-trip через `ToString()`

#### 3.2 Исполнение запроса

- [ ] Стратегия по длине терма: ≥ 3 символа — FTS5 trigram `MATCH`; 1–2 символа — префикс по индексу `name COLLATE NOCASE` с лимитом; пустой запрос — подборка (недавние, избранные)
- [ ] Фильтры → параметризованный `WHERE`: `in:` по `root_id` и префиксу `rel_path`; `type:` по `kind`; даты через UTC-границы
- [ ] Пагинация keyset по `(score, id)`; лимит кандидатов (например 2 000) до ранжирования
- [ ] Отмена долгих запросов (прерывание через `sqlite3_interrupt` по `CancellationToken`), таймаут 500 мс с частичным результатом и флагом
- [ ] Экранирование спецсимволов FTS (`"`, `*`, `-`), тесты генерации SQL и защиты от инъекций

#### 3.3 Ранжирование

- [ ] Компоненты score: тип совпадения (точное имя > начало имени > начало слова > подстрока > нечёткое), доля покрытия имени, bm25 из FTS, frecency = Σ log(1 + opens) · decay(дни с последнего открытия), избранное, штраф за глубину пути, свежесть изменения
- [ ] Веса в настройках с пресетами «По имени», «Недавнее», «Сбалансированно»
- [ ] Детерминированный tie-break по имени и пути
- [ ] Golden-тесты на фиксированном корпусе (~500 записей): ожидаемый топ-5 для ~30 запросов

#### 3.4 Нечёткое совпадение и подсветка

- [ ] Fuzzy-скорер в духе fzf: последовательные символы, бонусы за границы слов, camelCase и разделители, штраф за разрывы — над кандидатами из 3.2
- [ ] Диапазоны подсветки для имени и пути; Unicode: кириллица, ё/е, нормализация NFKD для диакритики (опц.)

#### 3.5 Поисковый сервис

- [ ] `SearchService.SearchAsync(request, ct)` → `SearchPage { Hits, ApproxTotal, ElapsedMs, ParsedQuery, Warnings }`
- [ ] Кэш уточнения: добавление символа к предыдущему запросу фильтрует прежних кандидатов в памяти, пока полнота гарантирована
- [ ] `SuggestAsync`: история, популярные фильтры, автодополнение значений (`ext:` из индекса, `tag:` из тегов, `in:` из папок)
- [ ] История запросов: лимит, удаление отдельных записей, очистка

#### 3.6 Производительность

- [ ] Генератор синтетической БД на 1 млн записей с фиксированным seed
- [ ] BenchmarkDotNet: p50/p95 для запросов из 2, 3 и 5 символов, с фильтрами и пустого; цель p95 < 50 мс
- [ ] Размер БД на 1 млн записей зафиксирован (ориентир ≤ 600 МБ с FTS); при превышении — FTS только по имени

**Критерии готовности этапа 3**

- Все фильтры и операторы работают и покрыты тестами; golden-тесты ранжирования зелёные.
- p95 < 50 мс на 1 млн записей; отмена запросов работает.

---

### Этап 4 — Сервер: Web API и SignalR

**Статус:** ⬜ · **Объём:** L · **Версия:** v0.3 · **Зависит от:** 1–3

**Цель.** Локальный сервис с полным API для всех клиентов, событиями в реальном времени и очередью файловых операций.

#### 4.1 Хост

- [ ] `DiskApp.Server` как библиотека: `AddDiskAppServer(services, options)`, `MapDiskAppApi(endpoints)`, `DiskAppServerHost` (создание `WebApplication`, старт, остановка, фактический адрес)
- [ ] Kestrel по умолчанию на `http://127.0.0.1:0` (динамический порт), адрес через `IServerAddressesFeature`; фиксированный порт и LAN — только при включённом удалённом доступе (этап 13)
- [ ] Общий DI-контейнер: сервисы Core и Infrastructure регистрируются здесь; десктоп добавляет свои сервисы в тот же контейнер
- [ ] Краткое логирование запросов, `ProblemDetails` для ошибок, глобальный обработчик исключений, `/health`
- [ ] (опц.) `DiskApp.Server.Host`: консольный хост и служба Windows (`UseWindowsService`) для headless-режима

#### 4.2 Контракты (`DiskApp.Contracts`)

- [ ] DTO: `RootDto`, `EntryDto` (включая ключ иконки, вид, подсветку), `SearchRequest`, `SearchResponse`, `SuggestResponse`, `BrowseResponse`, `OperationRequest`, `OperationDto`, `OperationProgressDto`, `ConflictDecisionDto`, `SettingsDto`, `IndexStatusDto`, `TagDto`, `CollectionDto`, `AppInfoDto`
- [ ] Маршруты константами (`ApiRoutes.V1.*`), префикс `/api/v1`
- [ ] `IDiskAppHubClient`: `OnIndexProgress`, `OnRootStatusChanged`, `OnEntriesChanged(batch)`, `OnOperationProgress`, `OnOperationCompleted`, `OnOperationNeedsDecision`, `OnSettingsChanged`
- [ ] `JsonSerializerContext` (source generation) для всех DTO; camelCase; перечисления строками

#### 4.3 Endpoints (Minimal API, группы)

- [ ] Roots: `GET /roots`, `POST /roots`, `PUT /roots/{id}`, `DELETE /roots/{id}`, `POST /roots/{id}/rescan`, `POST /roots/{id}/pause`, `POST /roots/{id}/resume`, `POST /index/rebuild`, `GET /index/status`
- [ ] Search: `GET /search?q&limit&cursor&sort`, `POST /search` для сложных запросов, `GET /suggest?q`, `GET /history`, `DELETE /history/{id}`, `DELETE /history`
- [ ] Entries: `GET /entries/{id}`, `GET /entries/{id}/children`, `GET /browse?path` (только внутри корней), `GET /entries/{id}/icon` (PNG, ETag), `GET /entries/{id}/thumbnail?size`, `GET /entries/{id}/content` (поток с Range — для удалённых клиентов)
- [ ] Actions: `POST /entries/{id}/open`, `/open-with`, `/reveal`, `/properties`, `/run-as-admin`; каждое открытие пишет в `usage`
- [ ] Operations: `POST /operations` (copy | move | rename | delete | create-folder | create-file, политика конфликтов), `GET /operations`, `GET /operations/{id}`, `POST /operations/{id}/cancel`, `POST /operations/{id}/decision`, `POST /operations/{id}/undo`, `POST /upload` (multipart в целевую папку)
- [ ] Favorites, Tags, Collections: CRUD; `POST /entries/{id}/tags`; `GET /recent`; `GET /pinned`
- [ ] Settings: `GET /settings`, `PATCH /settings` (частичное обновление с валидацией); `GET /app/info`
- [ ] OpenAPI (`Microsoft.AspNetCore.OpenApi`) и Scalar UI в Development; описания и примеры для всех endpoints

#### 4.4 SignalR

- [ ] `DiskAppHub : Hub<IDiskAppHubClient>`; методы клиента `SubscribeRoot`, `UnsubscribeRoot`, `GetStatus`
- [ ] Транслятор `IIndexEventBus` → хаб с батчингом 250 мс и ограничением размера пакета
- [ ] Протокол JSON (MessagePack — опц. позже); KeepAlive и таймауты под локальную сеть
- [ ] При переподключении клиента — отправка текущего `IndexStatus` и списка активных операций

#### 4.5 Файловые операции (`FileOperationService`)

- [ ] Очередь операций: id, состояния (Queued / Running / WaitingDecision / Completed / Failed / Cancelled), прогресс (файлы, байты, текущий файл, скорость, оценка времени)
- [ ] Копирование потоками с большим буфером и `FileOptions.SequentialScan`, сохранение атрибутов и времён; перемещение в пределах тома через `File.Move`, между томами — copy + delete с проверкой
- [ ] Конфликты: политики Ask / Skip / Overwrite / KeepBoth (`имя (2).ext`); при Ask операция ждёт решения клиента (событие `OnOperationNeedsDecision` + endpoint `decision`)
- [ ] Удаление в корзину через `IPlatformShell` (`IFileOperation` с `FOF_ALLOWUNDO`); безвозвратное — отдельный явный флаг
- [ ] Undo для rename / move / create — стек в рамках сессии
- [ ] Немедленное обновление индекса после операции, события в хаб
- [ ] Тесты на временных папках: все виды операций, конфликты, отмена посередине, cross-volume (через `IFileSystem`)

#### 4.6 Безопасность локального режима

- [ ] Привязка только к loopback; CORS выключен; (за флагом) заголовок `X-DiskApp-Token` со случайным токеном сессии, известным только десктопу — защита от других локальных процессов
- [ ] `browse?path` и операции только внутри разрешённых корней: нормализация, запрет `..`, проверка выхода через symlink; тесты обхода путей
- [ ] Лимиты размеров запросов и загрузок, таймауты

#### 4.7 Тесты

- [ ] `WebApplicationFactory` с тестовой БД: все endpoints (успех, валидация, 404), snapshot-тесты JSON-контрактов
- [ ] SignalR через `TestServer`: подписка, доставка `OnIndexProgress`, батчинг
- [ ] Нагрузочный smoke: 100 параллельных поисков по loopback, p95 < 60 мс (поиск + сериализация)

**Критерии готовности этапа 4**

- Scalar показывает все endpoints с описаниями; интеграционные тесты зелёные.
- Файловые операции с прогрессом, отменой и конфликтами работают через API.
- Поиск через API укладывается в p95 < 60 мс.

---

### Этап 5 — Клиентская библиотека

**Статус:** ⬜ · **Объём:** S · **Версия:** v0.3 · **Зависит от:** 4

**Цель.** Один типизированный клиент для десктопа, Android и Web без платформенного кода.

#### 5.1 REST-клиент

- [ ] `IDiskAppApi` со всеми операциями API; `HttpDiskAppApi` на `HttpClient` и `System.Net.Http.Json` с source-gen контекстом
- [ ] Устойчивость через `Microsoft.Extensions.Http.Resilience`: повторы только для идемпотентных GET, таймауты; `CancellationToken` для поиска по мере ввода
- [ ] Ошибки: `ProblemDetails` → `DiskAppApiException` с кодом и деталями
- [ ] Потоки: скачивание содержимого с прогрессом, загрузка файлов (multipart) с прогрессом

#### 5.2 SignalR-клиент

- [ ] `DiskAppHubConnection`: `HubConnectionBuilder` + `WithAutomaticReconnect`, типизированные события (C# events), состояние соединения
- [ ] Ресинхронизация после переподключения (запрос статуса); буферизация событий при отсутствии подписчиков

#### 5.3 Подключение к серверу

- [ ] `IServerEndpointProvider`: in-process (адрес запущенного сервера) и удалённый (адрес, токен, отпечаток сертификата — этап 13)
- [ ] DI-расширение `AddDiskAppClient(options)`

#### 5.4 Тесты

- [ ] Против `TestServer` этапа 4: успешные сценарии, ошибки, отмена, переподключение (эмуляция разрыва)

**Критерии готовности этапа 5**

- В клиенте нет платформенного кода; покрыт тестами; десктоп использует его без обходных путей.

---

### Этап 6 — Десктоп: каркас, трей, окна, стиль Windows 11

**Статус:** ⬜ · **Объём:** L · **Версия:** v0.3 · **Зависит от:** 0 (спайки), 5

**Цель.** Приложение живёт в трее, стартует быстро, открывает окна в стиле Windows 11 и запускает сервер внутри процесса.

#### 6.1 Каркас Avalonia

- [ ] `DiskApp.UI`: ресурсы `App`, `FluentAvaloniaTheme` (`PreferSystemTheme`, `PreferUserAccentColor`), `RequestedThemeVariant = Default`, шрифт Segoe UI Variable с fallback на Inter, FluentIcons
- [ ] MVVM: `ObservableObject`, `RelayCommand`, `ViewLocator`; сервисы `INavigationService`, `IDialogService` (`ContentDialog`), `IInfoBarService`, `IDispatcher` (абстракция для тестов)
- [ ] DI: один контейнер с сервером (`WebApplicationBuilder.Services`); ViewModels регистрируются по времени жизни
- [ ] Дизайн-токены в `Styles/`: радиусы 4/8 px, шкала отступов 4/8/12/16/24, типографика Fluent (Caption, Body, BodyStrong, Subtitle, Title), длительности анимаций 150/250 мс

#### 6.2 Сервер внутри процесса

- [ ] Старт Kestrel в фоне параллельно инициализации UI; UI ждёт готовности (`TaskCompletionSource`) с таймаутом и понятной ошибкой
- [ ] Занятый порт → повтор на динамическом; адрес в лог; корректная остановка при выходе через `IHostApplicationLifetime`
- [ ] Замер и лог времени старта: трей, сервер, окно

#### 6.3 Трей

- [ ] `TrayIcon` с `NativeMenu`: Открыть, Быстрый поиск, Пауза / Возобновить индексацию, Настройки, Выход; подсказка с состоянием индекса
- [ ] Набор `.ico` в `assets/` для состояний (простой / индексация / пауза / ошибка), варианты для светлой и тёмной панели задач
- [ ] ЛКМ → быстрый поиск; двойной клик → главное окно; закрытие главного окна → в трей (настраиваемо); подтверждение выхода при активных операциях

#### 6.4 Один экземпляр и IPC

- [ ] `Mutex` на пользователя + `NamedPipeServerStream`; второй экземпляр передаёт аргументы и завершается
- [ ] Команды: `--show`, `--search "q"`, `--add <paths>`, `--settings`, `--minimized`, `diskapp://open?path=…`, `diskapp://search?q=…`
- [ ] Тесты парсера аргументов и протокола IPC

#### 6.5 Глобальные горячие клавиши

- [ ] `IGlobalHotkeyService`; Windows: `RegisterHotKey` / `UnregisterHotKey`, приём `WM_HOTKEY` через `Win32Properties.AddWndProcHookCallback` скрытого окна
- [ ] Модель `Hotkey` (модификаторы + клавиша), сериализация в настройки; неудачная регистрация → `InfoBar` с предложением сменить сочетание
- [ ] Повторное нажатие при открытом окне поиска скрывает его (toggle)
- [ ] Заглушка для не-Windows (SharpHook — этап 12.7)

#### 6.6 Автозапуск

- [ ] Ключ `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` со значением `"<путь>\DiskApp.exe" --minimized`; чтение фактического состояния; переключатель в настройках; в портативном режиме — предупреждение о пути

#### 6.7 Окна

- [ ] `QuickSearchWindow`: `SystemDecorations = None`, `Topmost`, `ShowInTaskbar = false`, `TransparencyLevelHint = [AcrylicBlur, Blur]`, скруглённые углы через DWM, тень; позиция — верхняя треть монитора с курсором или активным окном (настраиваемо); запоминание размера; скрытие по `Esc`, потере фокуса и хоткею; окно создаётся заранее и скрывается для мгновенного показа
- [ ] `MainWindow`: `ExtendClientAreaToDecorationsHint`, собственный заголовок (название, поиск, системные кнопки), `TransparencyLevelHint = [Mica, AcrylicBlur, None]` с fallback-цветом, `NavigationView`, запоминание размера, позиции и монитора, возврат в границы экрана
- [ ] Страница настроек внутри `MainWindow`
- [ ] Несколько мониторов с разным DPI — ручной чеклист

#### 6.8 Чеклист стиля Windows 11

- [ ] Mica на главном окне, Acrylic на popup; при выключенной прозрачности или Windows 10 — сплошные цвета темы
- [ ] Светлая и тёмная тема по системе и вручную; акцентный цвет системы; режим высокой контрастности читаем
- [ ] Радиусы: окна 8 px, контролы 4 px; фокус-визуалы Fluent; состояния наведения и нажатия
- [ ] Иконки Segoe Fluent Icons / FluentIcons размеров 16/20/24
- [ ] Анимации: появление popup (fade + slide 150 мс), переходы страниц (`TransitioningContentControl`); отключаются при системной настройке «уменьшить движение»

#### 6.9 Тесты

- [ ] Avalonia.Headless: приложение стартует, `MainWindow` создаётся, навигация между пустыми страницами, команды ViewModel
- [ ] Ручной чеклист трея, хоткея и одиночного экземпляра в QA_CHECKLIST.md

**Критерии готовности этапа 6**

- Старт до иконки в трее < 1 с (тёплый), окно поиска по хоткею < 100 мс.
- Один экземпляр, IPC-команды и автозапуск работают; чеклист стиля Windows 11 пройден.

---

### Этап 7 — Быстрый поиск

**Статус:** ⬜ · **Объём:** L · **Версия:** v0.3 · **Зависит от:** 3–6

**Цель.** Главный сценарий «хоткей → три буквы → Enter» работает быстро и без сюрпризов. После этого этапа приложение используется ежедневно.

#### 7.1 ViewModel и список результатов

- [ ] `QuickSearchViewModel`: debounce 50–75 мс, отмена предыдущего запроса, отбрасывание устаревших ответов, состояния Idle / Searching / Results / Empty / Error
- [ ] Виртуализованный список; элемент: иконка, имя с подсветкой (runs), путь с сокращением середины, размер и дата, бейджи (избранное, тег, offline)
- [ ] Пустой запрос: секции «Недавно открытые», «Закреплённые», «Недавно изменённые»
- [ ] Строка состояния: «N результатов · 12 мс · индексация 43 %»; ошибки соединения с сервером с кнопкой повтора

#### 7.2 Клавиатура

- [ ] `↑↓`, `PgUp/PgDn`, `Home/End`, `Enter`, `Ctrl+Enter`, `Shift+Enter`, `Alt+Enter`, `Ctrl+C`, `Ctrl+Shift+C`, `F2`, `Del`, `Ctrl+P`, `Tab` / `Shift+F10`, `Ctrl+1…9`, `Esc` (очистить → закрыть), `↑` в пустом поле — история
- [ ] Карта клавиш в одном месте, настраиваемая на этапе 9
- [ ] Фокус всегда в поле ввода при показе; текст выделяется при повторном показе

#### 7.3 Действия над файлом (Windows `IPlatformShell`)

- [ ] Open (`ShellExecuteEx`), OpenWith (`SHOpenWithDialog`), Reveal (`SHOpenFolderAndSelectItems`), Properties (verb `properties`), RunAsAdmin (verb `runas`), CopyPaths, CopyFiles (`DataObject` с `DataFormats.Files`), MoveToRecycleBin
- [ ] Действия выполняет сервер (владеет файловой системой); UI вызывает API; ошибки → `InfoBar`
- [ ] Запись использования при открытии; эффект виден в следующем поиске

#### 7.4 Иконки и миниатюры

- [ ] `IIconProvider` (Windows): иконка по расширению через `SHGetFileInfo` с кэшем в памяти и на диске (`cache\icons`); per-file для `.exe`, `.lnk`, `.ico`; миниатюры через `IShellItemImageFactory` (не для облачных заглушек); `HBITMAP` → PNG
- [ ] Отдача через `/icon` и `/thumbnail` с ETag; LRU-кэш `Bitmap` в UI; асинхронная загрузка с плейсхолдером по виду файла
- [ ] Монохромные fallback-иконки FluentIcons корректны в тёмной теме

#### 7.5 Фильтры и подсказки

- [ ] Чипы фильтров над результатами (вид, корень, дата), переключение мышью и `Ctrl+F`
- [ ] Автодополнение при вводе `ext:`, `tag:`, `in:`; подсказка синтаксиса по `?`
- [ ] История запросов с удалением записей

#### 7.6 Drag & drop

- [ ] Из результатов во внешние приложения (`DragDrop.DoDragDrop` с файлами; копирование или перемещение по модификатору)
- [ ] В окно поиска из внешних приложений → сценарий «Добавить» (этап 8.5); до него — выбор цели и копирование

#### 7.7 Контекстное меню

- [ ] `MenuFlyout` с теми же действиями, иконками и подписями клавиш; (опц.) пункт «Ещё…» с меню оболочки Windows (`IContextMenu`) — этап 12.5

**Критерии готовности этапа 7**

- Сценарий «хоткей → 3 буквы → Enter» занимает < 1 с от нажатия до открытия файла; результаты обновляются ≤ 100 мс после ввода.
- Все клавиши и действия работают; frecency влияет на порядок; перетаскивание в Проводник, мессенджер и браузер работает.
- Начат dogfooding: замечания фиксируются в `docs/FEEDBACK.md`.

---

### Этап 8 — Главное окно: обзор, операции, «добавить» и «сохранить»

**Статус:** ⬜ · **Объём:** XL · **Версия:** v0.4 · **Зависит от:** 7

**Цель.** Полноценная работа с файлами внутри корней и два ключевых сценария сверх поиска: добавить файл в нужное место и сохранить туда из любой программы.

#### 8.1 Оболочка главного окна

- [ ] `NavigationView`: Главная, Поиск, Обзор, Коллекции, Теги, Входящие, Операции, Настройки; компактный режим; поиск в заголовке
- [ ] `BreadcrumbBar`, `CommandBar` с контекстными командами, панель сведений (метаданные, теги, действия), `InfoBar` состояния индекса и операций
- [ ] Анимированные переходы страниц; сохранение состояния страниц при переключении

#### 8.2 Главная

- [ ] Карточки корней (состояние, количество, последнее сканирование, действия), недавно открытые, закреплённые, изменённые за сутки, быстрые действия (добавить корень, зона перетаскивания)

#### 8.3 Обзор

- [ ] Дерево папок с ленивой загрузкой через `/children`; список и плитка с виртуализацией; переключение вида; сортировка (имя, дата, размер, тип); группировка; настраиваемые колонки
- [ ] Навигация как в Проводнике: `Backspace` / `Alt+←`, `Enter` в папку, адресная строка (`Ctrl+L`), фильтр по типу, быстрый фильтр по имени в текущей папке
- [ ] Множественный выбор; строка состояния «выбрано N, размер»; панель сведений
- [ ] Папки вне индекса (введённый путь) просматриваются через `/browse` без индексации

#### 8.4 Файловые операции в интерфейсе

- [ ] Drag & drop внутри приложения (копировать / переместить по модификатору, подсветка цели); `Ctrl+X / C / V` через системный буфер (`DataFormats.Files` + `Preferred DropEffect`); `F2`; `Del`; новая папка и файл (`Ctrl+Shift+N`); шаблоны новых файлов (пустые `.txt`, `.md`, `.json` и пользовательская папка шаблонов)
- [ ] Страница «Операции»: очередь, прогресс, пауза и отмена, ошибки, повтор; индикатор в `InfoBar` и подсказке трея
- [ ] Диалог конфликтов (`ContentDialog`): заменить / пропустить / оставить оба / применить ко всем, с именами, размерами и датами
- [ ] `Ctrl+Z` для переименования, перемещения и создания; уведомление «Отменить» после операции

#### 8.5 «Добавить»

- [ ] Зона перетаскивания в быстром поиске и на Главной: бросили файлы → выбор цели (недавние цели, избранные папки, поиск по папкам через `is:dir`), копировать или переместить, прогресс
- [ ] Интеграции Windows: ярлык «DiskApp» в `shell:sendto`; контекстное меню Проводника (`HKCU\Software\Classes\*\shell\DiskApp`, для папок и фона: «Добавить в DiskApp», «Открыть в DiskApp»); регистрация протокола `diskapp://`; включение и выключение из настроек с проверкой текущего состояния
- [ ] Буфер обмена в файл (`Ctrl+Shift+V`): текст → `.txt` или `.md` (эвристика Markdown), изображение → `.png`, список файлов → копирование; шаблон имени `{date}_{time}_{title}`; выбор папки как в зоне перетаскивания
- [ ] «Входящие»: папка по умолчанию для всего добавленного; страница с недавно добавленным — на этапе 17 становится центром разбора папок-накопителей

#### 8.6 «Сохранить» (помощь при сохранении)

- [ ] Избранные папки назначения: режим папок в быстром поиске (`is:dir`), действия «Скопировать путь» и «Открыть в Проводнике», чтобы вставить путь в любой диалог
- [ ] Quick Switch (экспериментально, флаг в настройках): детектор активного стандартного диалога (класс `#32770` с характерными дочерними контролами), хоткей внутри диалога открывает выбор папки DiskApp, выбор → запись пути в поле имени файла (`WM_SETTEXT` на `Edit` внутри `ComboBoxEx32`) + `Enter`, диалог переходит в папку; проверка в Блокноте, Word, браузере, VS Code; тайминги и откат при неудаче
- [ ] Исследование альтернативы через UI Automation (`IUIAutomation` COM interop), если Win32-подход окажется хрупким; решение в ADR

#### 8.7 Избранное, теги, коллекции

- [ ] Закрепление из любого места (`Ctrl+D`); страница избранного и секция на Главной
- [ ] Теги: создание, цвет, назначение нескольким файлам, чипы в списках, фильтр `tag:`
- [ ] Коллекции: ручные (перетаскиванием) и сохранённые запросы с автообновлением; экспорт и импорт списка путей

#### 8.8 Тесты

- [ ] Headless-тесты ViewModels обзора и операций с фейковым `IDiskAppApi`
- [ ] Тесты интеграций: регистрация и снятие ключей реестра в тестовом поддереве `HKCU\Software\DiskAppTests`
- [ ] Ручной чеклист: drag & drop в обе стороны, конфликты, undo, «Отправить», контекстное меню, буфер обмена, Quick Switch

**Критерии готовности этапа 8**

- Все базовые файловые операции с прогрессом, отменой и undo; drag & drop в обе стороны.
- «Отправить» и контекстное меню добавляют файлы; буфер обмена сохраняется в файл одной клавишей.
- Quick Switch работает хотя бы в Блокноте и Word либо помечен как экспериментальный с перечнем ограничений.

---

### Этап 9 — Настройки, локализация, онбординг, доступность

**Статус:** ⬜ · **Объём:** L · **Версия:** v0.5 · **Зависит от:** 8

#### 9.1 Настройки

- [ ] Корни и исключения: добавить папку (`StorageProvider.OpenFolderPickerAsync`) или диск (список `DriveInfo`), редактор исключений с проверкой шаблона и счётчиком затронутых, скрытые и системные, глубина, расписание сверки, пересканировать и удалить
- [ ] Поиск: нечёткий поиск, лимиты, пресет ранжирования, подборка при пустом запросе, история
- [ ] Горячие клавиши: контрол записи сочетания, проверка конфликта, сброс; карта клавиш окна поиска
- [ ] Внешний вид: тема (система / светлая / тёмная), фон (Mica / Acrylic / нет), акцент (система / свой), размер шрифта, компактный режим, уменьшить движение
- [ ] Поведение: автозапуск, старт свёрнутым, закрытие в трей, позиция и монитор popup, скрывать при потере фокуса, показывать путь, размер, дату
- [ ] Интеграции: «Отправить», контекстное меню, протокол, Quick Switch (флаг), ярлык на рабочем столе
- [ ] Входящие: папки-накопители, когда предлагать разбор, «не беспокоить», правила автосортировки, срок хранения журнала — наполняется на этапе 17
- [ ] Данные: путь к БД, размер индекса, пересобрать, очистить кэш, экспорт и импорт настроек, открыть папку логов, уровень логов; раздел «Резервные копии» (расписание, назначения, хранение, «Создать сейчас», «Восстановить…») — наполняется на этапе 16.1
- [ ] О программе: версия, проверка обновлений (этап 11), лицензии третьих сторон (автогенерация), ссылки
- [ ] Применение без перезапуска везде, где возможно; для остального — пометка и кнопка перезапуска

#### 9.2 Локализация

- [ ] `.resx` RU и EN; `Localizer` с разметкой в AXAML (`{loc:Str Key}`) и сменой языка на лету; форматы дат и размеров по культуре; множественные формы
- [ ] Все строки интерфейса и сообщений API вынесены; тест на захардкоженные строки в AXAML
- [ ] Язык по системе или вручную

#### 9.3 Онбординг

- [ ] Мастер первого запуска: выбрать корни → хоткей → автозапуск → тема → готово; запуск индексации с прогрессом
- [ ] `TeachingTip` для хоткея, фильтров и перетаскивания; пустые состояния с подсказками
- [ ] Страница «Что нового» после обновления

#### 9.4 Доступность

- [ ] `AutomationProperties.Name` и `HelpText` на всех интерактивных элементах; порядок табуляции; видимый фокус; цели нажатия ≥ 32 px
- [ ] Проверка Экранным диктором и в высокой контрастности; уважение «уменьшить движение»
- [ ] Масштаб шрифта до 150 % без обрезаний

#### 9.5 Диагностика и устойчивость интерфейса

- [ ] Глобальные обработчики (`AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException`, Avalonia) → лог и окно «Что-то пошло не так» с кнопкой «Собрать диагностический архив» (логи, настройки без секретов, версия, окружение)
- [ ] `--safe-mode`: без автоиндексации и интеграций; действие «Сбросить настройки»
- [ ] Страница логов: хвост файла, фильтр по уровню

**Критерии готовности этапа 9**

- Все настройки сохраняются и применяются; RU и EN без пропусков; онбординг проходит за ≤ 1 минуту.
- Чеклист доступности пройден; диагностический архив собирается одной кнопкой.

---

### Этап 10 — Производительность, надёжность, стабилизация

**Статус:** ⬜ · **Объём:** L · **Версия:** v1.0 · **Зависит от:** 9

#### 10.1 Измерение

- [ ] Скрипт замеров: старт до трея, показ popup, p95 поиска, память и CPU в простое (`dotnet-counters`); сравнение с разделом 8
- [ ] Профилирование горячих путей (`dotnet-trace`, PerfView): старт, первый поиск, прокрутка списка, индексация
- [ ] Бенчмарки этапов 1–3 — в CI как smoke без строгих порогов; полный прогон перед релизом

#### 10.2 Оптимизации

- [ ] Публикация: ReadyToRun, `TieredPGO`; проверка размера и времени старта; `InvariantGlobalization` не включать (нужна локализация)
- [ ] Старт: ленивое главное окно, заранее созданное окно поиска, отложенный прогрев кэша иконок, параллельный старт сервера
- [ ] Поиск: пулы буферов, `Span`-парсинг, source-gen сериализация, кэш уточнений
- [ ] UI: виртуализация везде, LRU-кэш `Bitmap` с лимитом, отложенные миниатюры, `CompiledBindings`
- [ ] Индексация: размеры пакетов и параллелизм по типу диска, фоновые приоритеты

#### 10.3 Надёжность

- [ ] Повреждение БД → восстановление (1.3) с уведомлением и предложением последней проверенной резервной копии (этап 16.1); резервная копия `settings.json`
- [ ] Отключение дисков, сон и пробуждение (перезапуск watcher'ов по `SystemEvents.PowerModeChanged`), смена сессии, нехватка места
- [ ] Длинные пути, Unicode-нормализация, запрещённые символы, скрытые и системные объекты, облачные заглушки — всё в тестах
- [ ] Сетевые корни: таймауты, offline-режим, частая сверка
- [ ] Soak-тест: 48 часов в трее с активной индексацией без роста памяти и хендлов

#### 10.4 Безопасность локального режима

- [ ] Повторная проверка: только loopback, обход путей, лимиты, нет секретов в логах
- [ ] `dotnet list package --vulnerable` в CI; обновление зависимостей; (опц.) SBOM

#### 10.5 Стабилизация

- [ ] Покрытие: Core ≥ 80 %, Infrastructure ≥ 70 %, Server ≥ 70 %; карантин нестабильных тестов с задачами
- [ ] Полный проход QA_CHECKLIST.md; разбор замечаний из FEEDBACK.md
- [ ] Заморозка функций: до релиза только исправления

**Критерии готовности этапа 10**

- Все бюджеты достигнуты и зафиксированы в документации с датой замера и конфигурацией машины.
- Две недели ежедневного использования без падений и расхождений индекса.

---

### Этап 11 — Сборка, установка, обновления, релиз 1.0

**Статус:** ⬜ · **Объём:** M · **Версия:** v1.0 · **Зависит от:** 10

#### 11.1 Публикация

- [ ] Профили `win-x64` и `win-arm64`: self-contained, single-file, ReadyToRun, `IncludeNativeLibrariesForSelfExtract`, иконка, метаданные сборки
- [ ] Версия из git (MinVer или Nerdbank.GitVersioning); CHANGELOG.md обновляется в каждом релизе
- [ ] Портативная сборка: zip с файлом `portable`

#### 11.2 Установщик (Velopack)

- [ ] `vpk pack` для x64 и arm64; ярлыки (меню «Пуск», по желанию рабочий стол); автозапуск по выбору; протокол и «Отправить» регистрирует приложение при первом запуске; удаление с вопросом о сохранении данных
- [ ] Проверка установки, обновления и удаления на чистых VM Windows 11 и Windows 10
- [ ] Альтернативы (MSIX для Store) задокументированы как отложенные

#### 11.3 Автообновление

- [ ] Проверка при старте и раз в сутки (GitHub Releases); каналы stable и beta; `InfoBar` «Доступна версия X»; скачивание в фоне; применение при перезапуске; отключение в настройках
- [ ] Заметки о выпуске внутри приложения («Что нового»)

#### 11.4 Подпись и доверие

- [ ] (опц.) Подпись кода (Azure Trusted Signing или сертификат); иначе — инструкция для пользователей о предупреждении SmartScreen

#### 11.5 Релизный конвейер

- [ ] `.github/workflows/release.yml` по тегу `v*`: build → test → publish → `vpk pack` → GitHub Release с артефактами и заметками; (опц.) манифест winget
- [ ] `docs/RELEASE.md`: чеклист релиза (версия, changelog, smoke на VM, тег, проверка автообновления с предыдущей версии)

#### 11.6 Документация

- [ ] `docs/USER_GUIDE.md`: установка, первые шаги, хоткеи, синтаксис, интеграции, FAQ, известные проблемы; скриншоты и GIF в `assets/`
- [ ] README: статус «v1.0», бейджи, ссылка на загрузку

**Критерии готовности этапа 11**

- Установка, обновление с предыдущей версии и удаление проходят на чистых VM в один клик.
- Релиз собирается из тега без ручных шагов; руководство пользователя опубликовано.

---

### Этап 12 — Расширенные возможности (v1.x)

**Статус:** ⬜ · **Объём:** XL (независимые подэтапы, порядок — по приоритету пользователя) · **Зависит от:** 11

#### 12.1 Панель предпросмотра

- [ ] Изображения (Skia, EXIF-поворот, зум); текст и код (AvaloniaEdit + грамматики TextMate, определение кодировки); Markdown; PDF (первая страница через PDFium — пакет `PDFtoImage`); миниатюры Office и видео через `IShellItemImageFactory`; метаданные медиа
- [ ] `Ctrl+P` в быстром поиске и панель в обзоре; лимиты размера и отмена загрузки

#### 12.2 Поиск по содержимому

- [ ] Экстракторы: txt, md, код, csv, json, xml; PDF через PdfPig; docx, xlsx, pptx через OpenXML; лимит размера; очередь низкого приоритета
- [ ] Таблица `content_fts` (`unicode61`, стемминг опц.); фильтр `content:` / `c:`; сниппеты с подсветкой; настройка «какие корни и типы индексировать»

#### 12.3 Дубликаты и анализ места

- [ ] Размер по папкам (treemap), крупные, старые и пустые файлы; дубликаты по размеру → частичному хэшу (xxHash64) → полному хэшу; удаление в корзину с отчётом

#### 12.4 Умные коллекции и правила

- [ ] Сохранённые запросы с автообновлением; правила для «Входящих» (по расширению, дате, маске → подпапка, переименование по шаблону); правила тегирования; журнал применённых правил с откатом

#### 12.5 Глубокая интеграция с Windows

- [ ] Jump List; toast-уведомления о завершении операций; меню оболочки файла (`IContextMenu`) внутри контекстного меню DiskApp; «Открыть в DiskApp» для папок
- [ ] Быстрая индексация NTFS через USN Journal и MFT (`FSCTL_ENUM_USN_DATA`): требует прав администратора — отдельный elevated helper или честный отказ; сравнение скорости с обычным сканированием

#### 12.6 Расширяемость

- [ ] Провайдеры действий (интерфейс + загрузка из `extensions/`): действия по виду файла (сжать, конвертировать, отправить); командная палитра приложения (`Ctrl+Shift+P`); PowerShell-хуки на события

#### 12.7 Linux и macOS

- [ ] Проверка `TrayIcon`; хоткеи через SharpHook; `xdg-open` / `open`; корзина (freedesktop Trash / Finder); пути данных (`XDG_DATA_HOME`, `~/Library/Application Support`); сборка и smoke на VM; сплошной фон вместо Mica

**Критерии готовности этапа 12**

- Каждая функция за флагом, не ухудшает бюджеты, покрыта тестами и описана в USER_GUIDE.

---

### Этап 13 — Удалённый доступ и безопасность

**Статус:** ⬜ · **Объём:** L · **Версия:** v2.0 · **Зависит от:** 11

**Цель.** Безопасная основа для Android и Web: ПК раздаёт API по локальной сети только сопряжённым устройствам.

#### 13.1 Режим сервера

- [ ] Включение в настройках: выбор сетевых интерфейсов, фиксированный порт, HTTPS с самоподписанным сертификатом (генерация, хранение, отпечаток в UI); правило брандмауэра с подтверждением
- [ ] Headless-режим (`--headless` или `DiskApp.Server.Host` как служба Windows) с тем же API

#### 13.2 Сопряжение и авторизация

- [ ] QR-код в десктопе (адрес + отпечаток сертификата + одноразовый код); регистрация устройства → подписанный отзываемый токен; список устройств с правами (только чтение, разрешённые корни, загрузка, удаление); отзыв; блокировка после N неудачных попыток
- [ ] Middleware авторизации на всех endpoints и хабе; журнал доступа; rate limiting; CORS только для веб-клиента

#### 13.3 Обнаружение

- [ ] mDNS / DNS-SD `_diskapp._tcp` (Zeroconf или Makaretu.Dns); ручной ввод адреса; QR

#### 13.4 Потоки файлов

- [ ] Скачивание с Range и ETag; zip папки на лету; возобновляемая загрузка чанками; миниатюры и предпросмотр для удалённых клиентов; лимиты

#### 13.5 Безопасность

- [ ] `docs/SECURITY.md` с моделью угроз; тесты обхода путей и авторизации; фаззинг парсера запросов; пиннинг сертификата в клиентах; ротация токенов; проверка зависимостей

**Критерии готовности этапа 13**

- Телефон в той же сети сопрягается по QR и безопасно ищет и скачивает файлы; чеклист безопасности пройден.

---

### Этап 14 — Android

**Статус:** ⬜ · **Объём:** XL · **Версия:** v2.x · **Зависит от:** 13

#### 14.1 Проект

- [ ] Окружение: `dotnet workload install android`, Android SDK и JDK (рабочая нагрузка VS «.NET Multi-platform App UI development» или `dotnet build -t:InstallAndroidDependencies`), эмулятор и устройство
- [ ] `DiskApp.Android` (Avalonia Android head), min API 26, разрешения, safe areas, адаптивная навигация (нижняя панель на телефоне), сенсорные размеры целей

#### 14.2 Функции

- [ ] Сопряжение (сканер QR или ввод кода), список серверов, автоподключение в домашней сети
- [ ] Поиск и обзор удалённых корней, предпросмотр, скачивание в кэш и открытие через `Intent` (`FileProvider`), «Поделиться», избранное
- [ ] Share target «Отправить в DiskApp» из любого приложения → загрузка во «Входящие» ПК с выбором папки
- [ ] Offline: кэш последних результатов и избранного

#### 14.3 Сборка

- [ ] Подпись APK и AAB; CI job с Android SDK; внутренняя бета (APK); Play Store позже

**Критерии готовности этапа 14**

- Основные сценарии работают на реальном устройстве; бета у 2–3 пользователей.

---

### Этап 15 — Web (Avalonia Browser)

**Статус:** ⬜ · **Объём:** L · **Версия:** v2.x · **Зависит от:** 13

- [ ] `dotnet workload install wasm-tools`; `DiskApp.Browser` (Avalonia Browser head); раздача сервером как статики по `/` с Brotli-прекомпрессией; PWA-манифест
- [ ] Тот же `DiskApp.Client` (HttpClient → fetch, SignalR → WebSockets); авторизация токеном после сопряжения по коду
- [ ] Адаптация интерфейса: без трея, хоткеев и оболочки; скачивание через JS interop (`[JSImport]` → blob + ссылка download); загрузка через `OpenFilePicker`; буфер обмена через браузерный API
- [ ] Первая загрузка ≤ 15 МБ (trimming, lazy assemblies), кэширование; проверка в Chromium, Firefox, Safari

**Критерии готовности этапа 15**

- Открыть `https://pc:port/` с любого устройства в сети, искать, просматривать и скачивать файлы.

---

### Этап 16 — Модуль резервного копирования

**Статус:** ⬜ · **Объём:** 16.1 — M, 16.2 — XL · **Версия:** 16.1 — v1.0 (выполняется после этапа 10, до релиза в этапе 11), 16.2 — v1.x · **Зависит от:** 16.1 — этапы 1, 4, 9; 16.2 — этапы 2, 4, 8 и 16.1

**Цель.** Пользователь не теряет ни данные DiskApp (настройки, корни, избранное, теги, коллекции, статистику, индекс), ни, по своему решению, сами файлы. Модуль настраиваемый: что копировать, куда, как часто, сколько хранить, как проверять и как восстанавливать.

**Что защищаем**

| Данные | Ценность | Восстановление |
|---|---|---|
| `settings.json` | Высокая | Из копии |
| Метаданные в `index.db`: корни, избранное, теги, коллекции, история запросов, статистика использования | Высокая, невоспроизводимы | Из копии БД или из JSON-экспорта метаданных |
| Индекс файлов в `index.db` | Средняя, воспроизводим пересканированием | Из копии (быстро) или пересканирование (долго на больших дисках) |
| Кэш иконок и миниатюр | Нулевая | Не копируется, пересоздаётся |
| Файлы пользователя в корнях | По решению пользователя | Задания резервного копирования (16.2) |

#### 16.1 Резервное копирование данных приложения (v1.0)

- [ ] `IBackupService` в Core, реализация в Infrastructure; снимок БД через SQLite Online Backup API (`SqliteConnection.BackupDatabase`) — консистентная копия во время работы, индексация и поиск не останавливаются, файлы `-wal` и `-shm` не трогаются
- [ ] Состав копии: снимок `index.db`, `settings.json`, `manifest.json` (версия приложения, версия схемы БД, дата, размеры, SHA-256 каждого файла, состав); упаковка в `DiskApp-backup-YYYYMMDD-HHmmss.zip`; вариант «только метаданные» — JSON-экспорт корней, избранного, тегов, коллекций, истории и статистики без индекса (маленький файл, переносим между версиями и машинами)
- [ ] Расписание: вручную; при выходе из приложения; ежедневно или еженедельно в заданное время при простое; автоматически перед миграцией схемы, пересборкой индекса, восстановлением и обновлением приложения — «страховочная» копия
- [ ] Назначения: по умолчанию `%LocalAppData%\DiskApp\backups`; любая папка пользователя, в том числе синхронизируемая облачным клиентом (OneDrive, Google Drive) или сетевая (UNC); несколько назначений одновременно; недоступное назначение — предупреждение и повтор позже
- [ ] Хранение: оставить N последних; схема «дни / недели / месяцы» (GFS); лимит общего размера; автоматическое удаление старых; страховочные копии хранятся отдельно (последние 3)
- [ ] Шифрование по желанию: AES-256 с паролем пользователя (пароль не хранится, ключ выводится через PBKDF2 или Argon2); настоятельно предлагается, когда в настройках появляются токены удалённого доступа (этап 13)
- [ ] Восстановление: список копий с датой, размером, версией и статусом проверки; проверка `PRAGMA integrity_check` и контрольных сумм до замены; выбор «всё / только настройки / только метаданные / только индекс»; текущие данные перед заменой уходят в страховочную копию; аккуратный перезапуск приложения
- [ ] Автовосстановление: при повреждённой БД на старте (1.3) предложить последнюю проверенную копию вместо пересборки с нуля
- [ ] Экспорт и импорт метаданных в JSON как самостоятельные действия: перенос на другую машину, слияние избранного и тегов с существующими, разрешение конфликтов
- [ ] Совместимость версий: копия хранит версию схемы; восстановление старой копии в новую версию проходит через миграции; новая копия в старую версию — отказ с понятным сообщением
- [ ] API и события: `GET /backups`, `POST /backups` (создать сейчас, с параметрами состава и назначения), `GET /backups/{id}`, `POST /backups/{id}/verify`, `POST /backups/{id}/restore`, `DELETE /backups/{id}`, `GET /backups/settings`, `PUT /backups/settings`, `POST /metadata/export`, `POST /metadata/import`; SignalR `OnBackupProgress`, `OnBackupCompleted`
- [ ] Интерфейс: «Настройки → Данные → Резервные копии» (расписание, назначения, хранение, шифрование, «Создать сейчас», «Восстановить…», журнал копий); `InfoBar` при сбое копирования; напоминание, если копия давно не делалась (настраивается)
- [ ] Тесты: снимок во время активной записи в БД консистентен; ретенция удаляет ровно лишнее; восстановление возвращает настройки и метаданные один в один; шифрование и расшифровка; повреждённый архив отклоняется; импорт метаданных со слиянием

**Критерии готовности 16.1**

- Копия создаётся по расписанию без заметного влияния на поиск и индексацию (замер).
- Восстановление из любой копии возвращает настройки и метаданные один в один (автотест).
- Страховочная копия делается перед каждой миграцией, пересборкой и восстановлением.

#### 16.2 Резервное копирование файлов пользователя (v1.x)

- [ ] Модель «задание»: источник (корень, папка, коллекция, тег или сохранённый запрос), назначение (папка, внешний диск по идентификатору тома, сетевой путь), режим, расписание, фильтры (исключения, расширения, размер), политика удалений, число хранимых версий, способ проверки
- [ ] Режимы: зеркало (односторонняя синхронизация; удалённые в источнике файлы помечаются и удаляются из назначения через N дней), версионный (изменённые файлы сохраняются с меткой времени, хранится K версий), снимок (zip-архив на дату)
- [ ] Инкрементальность через индекс: изменения из этапа 2 (watcher, mtime, размер) дают список «что изменилось с последнего запуска» без повторного обхода; опциональная проверка xxHash64 для подозрительных файлов (размер тот же, mtime новее)
- [ ] Расписание и триггеры: вручную; по расписанию; «при подключении диска» (`DriveInfo` и `WM_DEVICECHANGE`); «непрерывно» с задержкой тишины (например 10 минут после последнего изменения); пауза на батарее и на лимитном подключении
- [ ] Выполнение через `FileOperationService` (4.5): очередь, прогресс, отмена, ограничение скорости, низкий приоритет ввода-вывода; журнал запусков (скопировано, пропущено, ошибки); отчёт по завершении; повтор неудавшихся файлов
- [ ] Восстановление: просмотр содержимого назначения, восстановление файла или папки в исходное либо указанное место, список версий файла в версионном режиме, сравнение источника и копии
- [ ] Облачные назначения — только через папки клиентов синхронизации; прямые API облаков — отдельное решение позже (вопрос 12)
- [ ] API: `GET /backup-jobs`, `POST /backup-jobs`, `PUT /backup-jobs/{id}`, `DELETE /backup-jobs/{id}`, `POST /backup-jobs/{id}/run`, `POST /backup-jobs/{id}/cancel`, `GET /backup-jobs/{id}/runs`, `GET /backup-jobs/{id}/runs/{runId}`, `POST /backup-jobs/{id}/restore`; события `OnBackupJobProgress`, `OnBackupJobCompleted`
- [ ] Интерфейс: страница «Резервное копирование» в `NavigationView` (список заданий, состояние, последний и следующий запуск), мастер создания задания, страница запуска с журналом, панель восстановления; действие «Добавить в задание…» в контекстном меню файла и папки
- [ ] Тесты: все режимы на временных папках; политика удалений; версии; инкрементальный запуск после изменений; отключение диска посреди запуска; восстановление байт-в-байт по хэшам

**Критерии готовности 16.2**

- Задание на 100 тыс. файлов без изменений выполняется инкрементально за секунды.
- Восстановление возвращает файлы байт-в-байт (автотест с хэшами).
- Сбой диска посреди запуска не оставляет назначение в неконсистентном состоянии: журнал фиксирует, повтор доделывает.

---

### Этап 17 — Разбор входящих: папки-накопители и быстрая сортировка

**Статус:** ⬜ · **Объём:** L · **Версия:** v0.4 (выполняется сразу после этапа 8, до этапа 9) · **Зависит от:** 2 (слежение), 3 (поиск папок), 4.5 (операции), 7 (окно быстрого поиска), 8.4–8.5 (операции в интерфейсе, «Входящие»)

**Сценарий.** Файлы приходят отовсюду — Skype, почта, браузер, мессенджеры — и сбрасываются на один **общий склад** («Загрузки», «Рабочий стол», любая папка под присмотром). Затем, в удобный момент, DiskApp помогает быстро рассортировать склад и переслать каждый файл на свой **профильный склад** поменьше: «Документы», «Счета», «Картинки», «Установщики», «Проекты». Набор складов и их профили задаёт пользователь. Профильный склад сам может быть складом следующего уровня со своей сортировкой (каскад: «Документы» → «Счета», «Договоры»).

**Принципы.** Разбор — не диалог на каждый файл, а одна короткая сессия: файл → подсказка → одна клавиша. Номера складов закреплены (1 — Документы, 2 — Картинки, …), чтобы работала мышечная память, а подсказка лишь подсвечивает рекомендуемый. При разборе приложение только перемещает, никогда не удаляет само; каждое решение отменяемо; всё, что сделали правила автоматически, видно в журнале.

**Минимальный полезный срез** (делается первым, чтобы начать dogfooding): 17.1 без источников файлов, 17.2 с набором складов по умолчанию, 17.3 без пакетного режима и перетаскивания, подсказки 17.4 только по профилю склада и истории расширений, запуск вручную. Остальное наслаивается сверху.

#### 17.1 Общий склад: папки-накопители и сбор

- [ ] Модель `Collector` (папка-накопитель, он же «общий склад»; складов может быть несколько): путь, включать подпапки (по умолчанию нет), фильтры (игнорировать `.crdownload`, `.part`, `.tmp`, `~$`, скрытые), «время успокоения» (файл не меняется N секунд — загрузка завершена), что делать с уже лежащими файлами при добавлении (взять всё / только новые), включён/выключен
- [ ] Значения по умолчанию при первом запуске: «Загрузки» и «Рабочий стол» предлагаются в онбординге, не включаются молча; добавить можно любую папку, в том числе вне корней — накопитель автоматически становится лёгким корнем только для слежения (без полной индексации)
- [ ] Обнаружение новых файлов через watcher этапа 2 и сверку при старте; таблица `triage_items` (запись, накопитель, когда обнаружен, источник, состояние New / Pending / Snoozed / Sorted / Kept / Removed, решение, время решения, кем принято — вручную или правилом)
- [ ] Источник файла, если доступен: `Zone.Identifier` (альтернативный поток NTFS, поля `HostUrl` и `ReferrerUrl` → домен), расширение и вид, токены имени, дата — для подсказок и правил; чтение потока без блокировки файла
- [ ] Счётчик неразобранных: подсказка и бейдж на иконке трея, заголовок главного окна, страница «Входящие»
- [ ] Управление накопителями в «Настройки → Входящие» и на странице «Входящие»: добавить, изменить, выключить, удалить, «разобрать сейчас», «пересканировать»

#### 17.2 Профильные склады (назначения с профилем)

- [ ] Модель `Warehouse` (профильный склад): название, папка (любая, в том числе на другом диске), цвет и иконка, закреплённый номер 1–9 для клавиатуры, порядок, профиль — что сюда относится: виды файлов, расширения, ключевые слова в имени, домены источника, диапазон размеров; шаблон подпапки внутри склада (`{Год}`, `{Год}/{Месяц}`, `{Источник}` или без подпапок); флаг «сам является складом следующего уровня» (каскад) со своим набором профильных складов
- [ ] Набор по умолчанию предлагается в онбординге и привязывается к стандартным папкам Windows или подпапкам общего склада: Документы, Картинки, Видео, Музыка, Архивы, Установщики, Проекты/Работа; всё редактируется, лишнее удаляется
- [ ] Профиль проверяется автоматически: совпадение по профилю даёт уверенную подсказку; при совпадении с несколькими складами показываются все, первым — склад с большим числом совпавших признаков
- [ ] Управление складами на странице «Входящие» и в настройках: добавить (мастер: папка → название → номер → профиль), изменить, переставить номера перетаскиванием, выключить; быстрое создание склада прямо из сессии разбора (`Ctrl+W`: текущую папку назначения сделать складом с ближайшим свободным номером)
- [ ] Статистика склада: сколько файлов пришло за неделю и месяц, последний файл; «открыть склад» в обзоре DiskApp или в Проводнике
- [ ] Переезд склада (сменили папку) без потери истории и профиля; удаление склада не трогает файлы
- [ ] Тесты: сопоставление профиля, конфликт номеров, каскад (файл сначала уходит на «Документы», затем сессия внутри «Документов» отправляет его на «Счета»), переезд склада

#### 17.3 Сессия разбора (окно `TriageWindow`)

- [ ] Отдельное быстрое окно в стиле окна быстрого поиска: сверху текущий файл (превью или иконка, имя, размер, дата, источник, с какого общего склада), счётчик «3 из 12»; снизу — профильные склады кнопками с закреплёнными номерами 1–9, рекомендуемые подсвечены и подписаны объяснением («профиль: PDF», «5 похожих файлов отправлены сюда», «правило»); ниже — дополнительные подсказки папок (недавние, избранные, подпапки склада)
- [ ] Клавиши: `1…9` — отправить на профильный склад с этим номером; `Enter` — отправить на рекомендуемый; `T` — найти любую папку (окно быстрого поиска в режиме `is:dir`, `Enter` — выбрать, `Ctrl+N` — создать подпапку в выбранной); `N` — новая подпапка внутри рекомендуемого склада (имя предлагается из токенов файла); `Ctrl+W` — сделать текущую папку назначения новым складом; `Space` или `K` — оставить здесь (считается разобранным); `S` — отложить (до вечера / до завтра / до следующей сессии); `R` — переименовать перед перемещением; `O` — открыть; `Del` — в корзину (с подтверждением, никогда не по умолчанию); `U` или `Ctrl+Z` — отменить последнее; `←` `→` — предыдущий/следующий; `Esc` — завершить сессию, остальное остаётся в очереди
- [ ] Пакетный режим: «похожие файлы» (то же расширение, тот же источник, общий токен имени) предлагается обработать вместе («применить к 4 похожим» одной клавишей); режим списка: все ожидающие сгруппированы по рекомендуемому складу, кнопка «Отправить все уверенные», остальное — по одному
- [ ] Перетаскивание мышью: кнопки складов принимают перетаскивание файла, плюс компактное дерево папок и избранных назначений в окне разбора
- [ ] Перемещение через `FileOperationService`: конфликты имён по политике (по умолчанию «оставить оба»), занятые файлы пропускаются и возвращаются в очередь с пометкой, прогресс для больших файлов; индекс обновляется сразу
- [ ] Итог сессии: сколько разложено и куда, список с «Отменить» по каждому пункту и «Отменить всё»
- [ ] Скорость: показ окна < 150 мс, превью подгружается асинхронно и не блокирует решение, переход к следующему файлу без задержки

#### 17.4 Подсказки назначений

- [ ] Источники подсказок по убыванию уверенности: правила (17.5) → профиль склада (вид, расширение, ключевые слова, домен источника) → история решений по признакам → склады, куда недавно отправляли с этого общего склада → остальные склады по порядку номеров → любая папка через поиск; если складов ещё нет — предлагаются шаблонные по виду («Документы», «Картинки», «Установщики», «Архивы», «Видео»)
- [ ] Объяснимая оценка без ML: таблица «признак → папка → счётчик с затуханием по времени»; признаки — расширение, вид, домен источника, токены имени после нормализации (регистр, разделители, отсечение чисел и дат); объяснение показывается рядом с подсказкой
- [ ] Обучение на решениях: каждое решение обновляет счётчики; «оставить здесь» и «отложить» тоже учитываются, чтобы не предлагать перемещение настойчиво
- [ ] Подпапка внутри склада по его шаблону (`{Год}/{Месяц}`, `{Источник}`) создаётся автоматически при отправке; для складов без шаблона подпапки предлагаются по токенам имени файла
- [ ] Тесты: детерминированные подсказки на фиксированной истории; объяснение соответствует источнику подсказки; после 3–5 однотипных решений нужный склад выходит на первое место

#### 17.5 Правила автоматической сортировки

- [ ] Правило: условия (накопитель, расширение или вид, маска имени glob или regex, домен источника, размер, возраст) → действие (отправить на профильный склад или в папку с шаблоном пути `{Вид}`, `{Год}`, `{Месяц}`, `{Источник}`, `{Расширение}`; переименовать по шаблону; добавить тег; оставить и считать разобранным) → режим «автоматически после успокоения» или «только предлагать первым»
- [ ] Создание правила из решения в сессии одной клавишей (`Ctrl+R`): «Всегда отправлять PDF из Загрузок на склад „Документы“?» с предпросмотром условий
- [ ] Порядок правил, включение и выключение, «сухой прогон» на текущей очереди (показать, что куда уйдёт), импорт и экспорт правил в JSON
- [ ] Автоправила работают тихо и пишут в журнал; сводное уведомление («сегодня автоматически разложено 7 файлов») с «Отменить»
- [ ] Тесты: соответствие условий, шаблоны путей, порядок применения, сухой прогон, отмена результата автоправила

#### 17.6 Когда предлагать разбор: «умный момент»

Идея: напоминание само ищет момент, когда пользователь не занят, и не возвращается слишком часто. «Отложить» без выбора срока означает «предложи позже, когда я освобожусь» — и так до тех пор, пока очередь не разобрана. Планировщик живёт на стороне интерфейса (`DiskApp.UI` + платформенный монитор в `DiskApp.Desktop`), очередь и настройки берёт через API.

- [ ] Поводы (что создаёт потребность в разборе; настраиваются глобально и на накопитель): появление новых файлов; порог (≥ N неразобранных или ≥ M МБ); расписание (ежедневно в указанное время, выбранные дни); «давно не разбирали» (очередь старше N дней); вручную — глобальный хоткей (по умолчанию `Ctrl+Alt+I`), пункт трея, команда «Разобрать входящие» в быстром поиске. Повод сам ничего не показывает — он ставит заявку планировщику напоминаний
- [ ] Детектор занятости (`IUserActivityMonitor`, Windows): простой ввода через `GetLastInputInfo`; состояние уведомлений через `SHQueryUserNotificationState` (полноэкранное приложение, презентация, «не беспокоить» и фокусировка Windows, занятость); идущий звонок — микрофон или камера используются приложением (реестр `CapabilityAccessManager\ConsentStore`, включается в настройках); категория активного окна (IDE, офис, браузер, мессенджер, проводник) и частота переключения окон; интенсивность ввода за последние минуты; блокировка и разблокировка сеанса; питание от батареи. Из сигналов считается оценка занятости 0–1 с объяснением («идёт звонок», «полноэкранное приложение», «интенсивная работа в IDE»); все сигналы обрабатываются локально, в журнал попадают только агрегаты
- [ ] Хорошие моменты: возвращение к компьютеру после простоя ≥ 10 минут и 2–3 минут лёгкой активности; переход в «лёгкие» приложения (проводник, браузер не в полном экране) после периода работы; разблокировка сеанса; окончание звонка; привычное окно разбора по статистике (например 17:00–19:00); конец рабочего дня по расписанию. Плохие моменты: звонок, полный экран, презентация, «не беспокоить» Windows, часы тишины, интенсивный ввод, первые минуты после возвращения
- [ ] Планировщик (`TriageReminderScheduler`): заявка от повода → если момент хороший и лимиты соблюдены — показать; иначе ждать и проверять каждые 1–2 минуты (дёшево, без обращения к диску). «Отложить» без срока — умная отсрочка с нарастанием: не раньше чем через 45 минут, затем 1,5 часа, 3 часа, затем первое хорошее окно следующего дня; нарастание сбрасывается после разбора. Явные варианты: «через час», «вечером», «завтра», «сегодня больше не напоминать». Лимиты: не чаще одного напоминания в заданный интервал (по умолчанию 60 минут) и не больше N в день (по умолчанию 3). Закрытие уведомления без ответа считается «отложить». Заявки объединяются («7 новых файлов с утра»). Очередь напоминаний переживает перезапуск приложения
- [ ] Эскалация без вторжения: 1) бейдж и подсказка на иконке трея появляются сразу и остаются; 2) тихое уведомление без звука с кнопками «Разобрать», «Позже», «Сегодня не напоминать»; 3) `InfoBar` в главном окне, если оно открыто. Никаких модальных окон, перехвата фокуса и звука по умолчанию; любое напоминание закрывается одной клавишей или кликом
- [ ] Обучение моментов: по каждому напоминанию записываются контекст (час, день недели, оценка занятости, категория окна, число файлов) и реакция (разобрал, отложил, закрыл, проигнорировал); доля принятых по часам и состояниям сдвигает планировщик к окнам, где пользователь чаще соглашается; объяснение в настройках («вы чаще всего разбираете в 17–19 и после обеда»); статистику можно сбросить
- [ ] Настройки: ползунок настойчивости (редко / обычно / часто — меняет лимиты и минимальный интервал), максимум напоминаний в день, минимальный интервал, часы тишины, учитывать «не беспокоить» Windows, не напоминать во время звонков, напоминать при возвращении к компьютеру, поведение на батарее; «не беспокоить» внутри DiskApp на час или до завтра
- [ ] Отложенные файлы остаются в очереди и возвращаются вместе с заявкой; «больше не напоминать об этих файлах» помечает их как оставленные (Kept), не трогая сами файлы
- [ ] API и события: `GET /triage/reminder` (состояние, следующая проверка, причина ожидания), `POST /triage/reminder/snooze` (умная или явная отсрочка), `POST /triage/reminder/dismiss-today`, `GET /triage/reminder/stats`; событие `OnTriageReminderDue` для клиентов
- [ ] Тесты: детерминированный планировщик с подменяемыми часами и сигналами — сценарии «занят → отложил → освободился → напомнили один раз», «три отсрочки → следующий день», «лимит в день исчерпан», «звонок блокирует до окончания», «возвращение к компьютеру»; проверка, что напоминания не чаще минимального интервала ни при какой комбинации поводов

#### 17.7 Журнал, отмена, страница «Входящие»

- [ ] Страница «Входящие» в главном окне — центр разбора: общий склад (накопители) со счётчиками, профильные склады с номерами и статистикой, кнопка «Разобрать» (открывает сессию), очередь ожидающих с группировкой по рекомендуемому складу, отложенные, правила, журнал
- [ ] Журнал решений: что, откуда, куда, когда, кем (вручную или правило), «Отменить» в пределах срока хранения (по умолчанию 30 дней), «Открыть папку назначения»
- [ ] Отмена возвращает файл на исходное место с исходным именем; если место занято — «оставить оба»; отмена результата автоправила предлагает выключить правило
- [ ] Статистика для настройки и dogfooding: файлов разобрано за неделю, среднее время на файл, доля принятых первых подсказок

#### 17.8 API, контракты, тесты

- [ ] API: `GET /collectors`, `POST /collectors`, `PUT /collectors/{id}`, `DELETE /collectors/{id}`, `POST /collectors/{id}/scan`; `GET /warehouses`, `POST /warehouses`, `PUT /warehouses/{id}`, `DELETE /warehouses/{id}`, `POST /warehouses/reorder`, `GET /warehouses/{id}/stats`; `GET /triage/items?state`, `GET /triage/items/{id}/suggestions`, `POST /triage/items/{id}/decide` (действие, назначение, применить к похожим), `POST /triage/items/{id}/snooze`, `POST /triage/decisions/{id}/undo`, `GET /triage/history`; `GET/POST/PUT/DELETE /triage/rules`, `POST /triage/rules/dry-run`; SignalR `OnTriageQueueChanged`, `OnTriageDecisionApplied`, `OnWarehousesChanged`
- [ ] Тесты: обнаружение с «успокоением» (файл ещё дописывается), игнор временных файлов, решения и отмена на временных папках, занятые файлы, подсказки, правила, headless-тесты ViewModel сессии (клавиша → команда → вызов API)

**Критерии готовности этапа 17**

- Сценарий «пришло 10 файлов на общий склад → уведомление → сессия → всё разослано по профильным складам» занимает ≤ 30 секунд, в среднем одна клавиша на файл.
- Файл не теряется никогда: любое решение отменяется из журнала, конфликты не перезаписывают, занятые файлы остаются в очереди.
- Подсказки объяснимы и после 3–5 однотипных решений выводят нужный склад на первое место (автотест на фиксированном сценарии).
- Напоминания не приходят во время звонков, полноэкранных приложений и «не беспокоить»; после «Отложить» следующее напоминание — не раньше минимального интервала и только в «хороший момент»; не больше N в день (автотесты планировщика). За две недели dogfooding — ни одного напоминания, которое пользователь счёл неуместным (журнал реакций).

---

## 6. Сквозные практики

### Git и коммиты

- `main` всегда собирается и проходит тесты; работа в ветках `feat/<этап>-<кратко>`, `fix/…`, `chore/…`, `docs/…`.
- Conventional Commits с областью: `feat(index): …`, `fix(search): …`, `perf(ui): …`, `refactor`, `test`, `docs`, `build`, `ci`, `chore`.
- Один подэтап — 1–3 коммита (или один PR). Теги релизов `vX.Y.Z` по SemVer.

### Тестирование

- Пирамида: модульные тесты Core → интеграционные Infrastructure и Server → headless UI → ручной чеклист.
- Временные папки и БД только через фикстуры с гарантированной очисткой: `TemporaryDirectory` и `TemporaryDatabase` из `tests/DiskApp.Testing`.
- Нестабильный тест уходит в карантин с задачей на исправление, а не удаляется.

### Документация

| Документ | Отвечает на вопрос |
|---|---|
| README.md | Что это за продукт и как его собрать |
| PLAN.md | Что делаем, в каком порядке, где сейчас |
| docs/ARCHITECTURE.md | Как устроено |
| docs/ADR/ | Почему решили именно так |
| docs/USER_GUIDE.md | Как пользоваться |
| docs/QA_CHECKLIST.md | Что проверять руками |
| docs/FEEDBACK.md | Что заметили при ежедневном использовании |
| CHANGELOG.md | Что изменилось между версиями |
| CLAUDE.md | Как работать по плану в сессиях |
| CONTRIBUTING.md | Как оформлять код, тесты, ветки и коммиты |

### Производительность

- Бюджеты в разделе 8. Замер до и после каждой оптимизации. Регресс бюджета — дефект с приоритетом выше новой функции.
- Бенчмарки живут в `tests/DiskApp.Benchmarks`; результаты последнего полного прогона — в ARCHITECTURE.md с датой и конфигурацией машины.

### Dogfooding

- С v0.3 приложение используется ежедневно на рабочей машине. Замечания — в `docs/FEEDBACK.md`.
- Раз в неделю топ-5 замечаний попадает в ближайший подэтап.

---

## 7. Риски и смягчение

| Риск | Влияние | Смягчение |
|---|---|---|
| `FileSystemWatcher` теряет события (переполнение буфера, сеть, сон) | Расхождение индекса с диском | Debounce и обработка переполнения пересканированием; периодическая сверка; mark-and-sweep при полном сканировании |
| Облачные файлы «по запросу» (OneDrive) скачиваются при чтении | Трафик, место, медленная индексация | Проверка атрибутов `RECALL_ON_DATA_ACCESS` / `OFFLINE`; не читать содержимое и миниатюры таких файлов |
| Размер FTS-индекса на целых дисках (2+ млн файлов) | Сотни МБ, медленные вставки | FTS только по имени; лимиты; цифры из спайка S4 до решения |
| Mica и Acrylic нестабильны на части конфигураций | Визуальные артефакты | Fallback на сплошной фон; переключатель в настройках; проверка на Windows 10 |
| Антивирусы реагируют на глобальные хуки | Ложные срабатывания, удаление exe | `RegisterHotKey` вместо хуков; подпись кода |
| Порт loopback занят или Kestrel заблокирован политикой | Приложение не стартует | Динамический порт с повтором; понятное сообщение; (опц.) именованные каналы |
| Quick Switch зависит от внутренностей системных диалогов | Ломается после обновлений Windows | Флаг «экспериментально», таймауты, откат, UI Automation как альтернатива |
| Списки Avalonia на 10 тыс.+ элементов | Лаги прокрутки | Виртуализация, лимит результатов, подгрузка страницами |
| Kestrel внутри процесса увеличивает память и время старта | Бюджеты | Замер в спайке S3; параллельный старт; ReadyToRun |
| Ранжирование «не угадывает» | Раздражение, недоверие | Frecency, пресеты, golden-тесты, обратная связь из dogfooding |
| Avalonia Browser тяжёлый (10–20 МБ) | Долгая первая загрузка | Brotli, trimming, кэш; сценарий ограничен локальной сетью |
| Android ограничивает доступ к файловой системе | Нельзя индексировать телефон | Телефон — удалённый клиент к ПК; локальная индексация через SAF — позже |
| Нет подписи кода → предупреждение SmartScreen | Отток пользователей | Подпись (платно) или инструкция; публикация в winget |
| Один исполнитель и большой план | Растянутые сроки, потеря фокуса | Строгая очередность этапов; dogfooding с v0.3; функции за флагами; заморозка перед 1.0 |

---

## 8. Бюджеты производительности

Цели для v1.0 на машине с SSD и 16 ГБ памяти. Фиксируются с датой и конфигурацией после замера на этапе 10.

| Метрика | Цель | Как измеряем |
|---|---|---|
| Старт до появления иконки в трее | < 1 с тёплый, < 2 с холодный | Лог меток времени в `Program.Main` и при создании `TrayIcon` |
| Показ окна быстрого поиска по хоткею | < 100 мс | Метка от `WM_HOTKEY` до первого кадра |
| Поиск p95 на 1 млн записей | < 50 мс (сервис), < 60 мс (через API) | BenchmarkDotNet на синтетической БД; нагрузочный smoke |
| Обновление результатов после ввода | ≤ 100 мс | Метка от `TextChanged` до обновления списка |
| Память в простое | < 150 МБ | `dotnet-counters`, Диспетчер задач после 10 минут простоя |
| CPU в простое | ≈ 0 % | Диспетчер задач, 10 минут без изменений на дисках |
| Первичная индексация (SSD) | ≥ 2 000 файлов/с; 100 тыс. файлов ≤ 60 с | Бенчмарк сканера на сгенерированном дереве |
| Отражение изменения файла | ≤ 1 с | Тест watcher'а с меткой времени |
| Размер БД на 1 млн записей | ≤ 600 МБ | Спайк S4, бенчмарк этапа 3 |
| Размер установщика | ≤ 80 МБ | Артефакт релиза |

---

## 9. Глоссарий

| Термин | Значение |
|---|---|
| Корень (Root) | Папка или диск, который пользователь добавил в DiskApp для индексации |
| Запись (Entry) | Файл или папка в индексе |
| Индекс | База SQLite с записями, FTS-таблицей и статистикой |
| Сверка (reconcile) | Частичное пересканирование, которое сравнивает индекс с диском без полного обхода |
| Mark-and-sweep | Полное сканирование с пометкой записей текущим `scan_id` и удалением непомеченных |
| Frecency | Оценка, объединяющая частоту и давность использования файла |
| Быстрый поиск | Всплывающее окно по глобальной горячей клавише |
| Входящие (Inbox) | Папка по умолчанию для всего, что добавлено через DiskApp |
| Quick Switch | Перенаправление стандартного диалога «Открыть / Сохранить как» в папку, выбранную в DiskApp |
| Спайк | Короткий одноразовый эксперимент для проверки технического риска |
| Dogfooding | Ежедневное использование собственного продукта разработчиком |
| Страховочная копия | Автоматическая резервная копия данных приложения перед миграцией схемы, пересборкой индекса или восстановлением |
| Задание резервного копирования | Настроенная пользователем связка «источник → назначение» с режимом, расписанием и правилами хранения для файлов (этап 16.2) |
| Общий склад, папка-накопитель | Папка под присмотром DiskApp («Загрузки», «Рабочий стол»), куда файлы сбрасываются отовсюду; новые файлы в ней попадают в очередь разбора (этап 17) |
| Профильный склад | Именованная папка-назначение с профилем (что сюда относится) и закреплённым номером 1–9; может быть складом следующего уровня со своей сортировкой |
| Разбор, сессия разбора | Короткая клавиатурная сессия, в которой файлы из очереди по одному или пакетом раскладываются по папкам с подсказками и отменой |
| Подсказка назначения | Предложенная папка для файла с объяснением, откуда взялось предложение: правило, история решений, недавние или избранные назначения |
| Умный момент | Момент для напоминания о разборе, который планировщик выбирает по детектору занятости, лимитам частоты и статистике реакций; «Отложить» без срока означает «напомни в следующий умный момент» |

---

## 10. Журнал изменений плана

| Дата | Изменение |
|---|---|
| 2026-10-10 | План создан. Зафиксированы решения 1–16, открытые вопросы 1–11, этапы 0–15. Текущая позиция — этап 0.1 |
| 2026-10-10 | Ответы пользователя: IDE — Visual Studio 2026 Community, лицензия MIT, репозиторий GitHub пока не создаём (вопросы 1, 7, 11 закрыты). Решения 5 и 12 уточнены по фактическим версиям: Avalonia 12.x, FluentAvaloniaUI 3.x (собран под Avalonia 12.1), headless-тесты на xunit v3. Установлены .NET SDK 10.0.401, Git 2.55.0, шаблоны Avalonia 12.1.4; тестовая сборка Avalonia-приложения прошла |
| 2026-10-10 | Подэтап 0.1 завершён: Visual Studio Community 2026 18.10 (ManagedDesktop, NetWeb), Avalonia for Visual Studio 12.3.2.0, GitHub CLI 2.102.0, PowerShell 7.6.6, DB Browser for SQLite 3.13.1. Текущая позиция — подэтап 0.2 |
| 2026-10-10 | По запросу пользователя добавлен этап 16 «Модуль резервного копирования»: 16.1 данные приложения (SQLite и настройки) — v1.0, 16.2 файлы пользователя — v1.x. Добавлены решение 17 и вопрос 12, обновлены вехи, этапы 9.1 и 10.3, глоссарий, README |
| 2026-10-10 | По запросу пользователя добавлен этап 17 «Разбор входящих: папки-накопители и быстрая сортировка» (v0.4, сразу после этапа 8): накопители и сбор, сессия разбора с клавиатурой «одна клавиша на файл», объяснимые подсказки, правила автосортировки, триггеры напоминаний, журнал и отмена. Добавлены решение 18 и вопрос 13, обновлены вехи, этапы 8.5 и 9.1, глоссарий, README |
| 2026-10-10 | Этап 17 уточнён по аналогии пользователя «общий склад → профильные склады»: добавлен подэтап 17.2 «Профильные склады» (именованные назначения с профилем, закреплённые номера 1–9, каскад), остальные подэтапы перенумерованы (17.3–17.8), сессия разбора и подсказки переориентированы на склады; добавлено решение 19, уточнён вопрос 13, глоссарий и README |
| 2026-10-10 | Вопрос 13 закрыт подтверждением допущений. Подэтап 0.2 завершён: `git init` (ветка `main`), `.gitignore` и `.editorconfig` с разделами DiskApp, `global.json` (10.0.401), LICENSE MIT, CHANGELOG.md, CLAUDE.md, `docs/` (ARCHITECTURE, ADR README и 0001, QA_CHECKLIST, FEEDBACK). Первый коммит отложен до настройки имени и почты автора. Текущая позиция — подэтап 0.3 |
| 2026-10-10 | По запросу пользователя подэтап 17.6 переработан в «умный момент»: детектор занятости, планировщик напоминаний с умной отсрочкой и нарастанием, лимиты частоты, эскалация без вторжения, обучение по реакциям, API и тесты планировщика. Добавлены решение 20 и критерий готовности этапа 17; обновлены глоссарий, README и ARCHITECTURE |
| 2026-10-10 | Пользователь создал репозиторий GitHub IichiKohuro/DiskApp (вопрос 7 закрыт) и дал подпись автора. Локальная ветка `main` поставлена поверх начального коммита GitHub, LICENSE взят оттуда (правообладатель Iichi Kohuro), коммит `06eb56a` отправлен в `origin/main` |
| 2026-10-10 | Подэтап 0.3 завершён: `DiskApp.slnx`, `Directory.Build.props`, `Directory.Packages.props`, 7 проектов `src/`, 5 проектов `tests/`, архитектурный и дымовые тесты, пустое окно десктопа. Отклонения от списка пакетов: тесты переведены на Microsoft.Testing.Platform (`global.json`), coverlet.collector → Microsoft.Testing.Extensions.CodeCoverage 18.12.0 + TrxReport 2.5.1; добавлены AvaloniaUI.DiagnosticsSupport 2.2.3 (Debug) и Avalonia.Headless 12.1.4 вместо Avalonia.Headless.XUnit (несовместим с xunit v3 4.0.2); Avalonia.Themes.Fluent и NetArchTest.Rules не понадобились. Решение 12 уточнено. В `.editorconfig` `insert_final_newline = true` для `.cs`; в `tests/.editorconfig` снято требование суффикса `Async` для тестов. Текущая позиция — подэтап 0.4 |
| 2026-10-10 | Подэтап 0.4 завершён: `.editorconfig` ужесточён (IDE0005 и модификаторы доступа — ошибки сборки, `_camelCase` для статических полей, исправлен `type_parameters`), `GenerateDocumentationFile` в `Directory.Build.props`, новый проект `tests/DiskApp.Testing` с фикстурами `TemporaryDirectory` и `TemporaryDatabase` (шестой проект в `tests/`), CONTRIBUTING.md со ссылками из README, CLAUDE.md и PLAN. Хвосты 0.1 закрыты (подпись git, PATH). Текущая позиция — подэтап 0.5 |
