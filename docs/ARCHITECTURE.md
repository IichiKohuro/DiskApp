# Архитектура DiskApp

Документ описывает, как устроено приложение: процессы, проекты, данные, конвейеры индексации и поиска, API, интерфейс и платформенные сервисы. Что делает продукт — в [README.md](../README.md); почему решили именно так — в [ADR](ADR/README.md); порядок работ и критерии — в [PLAN.md](../PLAN.md).

Статус: проектная документация с фактами этапа 0 (2026-10-10). Разделы 3, 9, 10, 14–16 описывают реализованное и замеренное; остальные — замысел, помеченный этапом плана, на котором появится реализация. По мере реализации документ уточняется цифрами и схемами.

## Содержание

1. [Контекст и процессы](#1-контекст-и-процессы)
2. [Проекты и зависимости](#2-проекты-и-зависимости)
3. [Запуск десктопа](#3-запуск-десктопа)
4. [Данные](#4-данные)
5. [Индексация](#5-индексация)
6. [Поиск](#6-поиск)
7. [API и события](#7-api-и-события)
8. [Файловые операции](#8-файловые-операции)
9. [Интерфейс](#9-интерфейс)
10. [Платформенные сервисы](#10-платформенные-сервисы)
11. [Разбор входящих](#11-разбор-входящих)
12. [Резервное копирование](#12-резервное-копирование)
13. [Безопасность](#13-безопасность)
14. [Наблюдаемость и диагностика](#14-наблюдаемость-и-диагностика)
15. [Сборка и поставка](#15-сборка-и-поставка)
16. [Бюджеты и измерения](#16-бюджеты-и-измерения)

## 1. Контекст и процессы

В версии 1.0 всё работает в одном процессе `DiskApp.Desktop`: интерфейс Avalonia, клиентская библиотека и локальный сервер ASP.NET Core с ядром. Сервер слушает только `127.0.0.1` на динамическом порту. Интерфейс не вызывает ядро напрямую — только через `DiskApp.Client` по loopback, тем же путём, что и будущие удалённые клиенты.

```mermaid
flowchart LR
    subgraph PC["Процесс DiskApp.Desktop"]
        direction LR
        UI["Avalonia UI<br/>трей · быстрый поиск · главное окно · разбор"]
        Client["DiskApp.Client<br/>REST + SignalR"]
        Server["DiskApp.Server<br/>Minimal API + SignalR + hosted services"]
        Core["DiskApp.Core<br/>домен · индексация · поиск · операции"]
        Infra["DiskApp.Infrastructure<br/>SQLite · FileSystemWatcher · Windows Shell"]
        UI --> Client -- "http://127.0.0.1:порт" --> Server --> Core --> Infra
    end
    Infra --> FS[("Папки и диски")]
    Infra --> DB[("index.db")]
    Android["DiskApp.Android (v2.x)"] -. "LAN · HTTPS · токен устройства" .-> Server
    Web["DiskApp.Browser (v2.x)"] -. "LAN · HTTPS" .-> Server
    Headless["DiskApp.Server.Host (опц.)<br/>служба Windows"] --- Server
```

Режимы работы сервера:

| Режим | Кто запускает | Привязка | Авторизация | Этап |
|---|---|---|---|---|
| Локальный | `DiskApp.Desktop` внутри процесса | `127.0.0.1:0` (динамический порт) | Нет (опционально токен сессии против других локальных процессов) | 4, 6 |
| Headless | `DiskApp.Server.Host` или `DiskApp.Desktop --headless` | По настройке | Как в удалённом | 4 (опц.), 13 |
| Удалённый | Тот же сервер с включённым доступом по сети | Выбранные интерфейсы LAN, фиксированный порт, HTTPS | Токены сопряжённых устройств, права на устройство | 13 |

Состояние после этапа 1: `DiskApp.Core` содержит домен, интерфейсы инфраструктуры, шину событий и модель настроек; `DiskApp.Infrastructure` — SQLite (схема, миграции, писатель, репозитории), пути и JSON-хранилище настроек; `DiskApp.Desktop` (оболочка, окна, Win32) и `DiskApp.UI` (страницы на макете `ISearchService`) работают с этапа 0. `Contracts`, `Server` и `Client` — пустые проекты с архитектурными и дымовыми тестами; сканер (этап 2) и поиск (этап 3) ещё не связывают их. Спайки S3 и S4 подтвердили, что сервер внутри процесса и FTS5 укладываются в бюджеты (раздел 16), поэтому схема выше остаётся целевой без изменений.

## 2. Проекты и зависимости

```mermaid
graph TD
    Desktop["DiskApp.Desktop<br/>точка входа, трей, хоткеи, IPC, автозапуск"]
    UI["DiskApp.UI<br/>ViewModels, Views, стили, локализация"]
    Client["DiskApp.Client<br/>IDiskAppApi, HubConnection"]
    Contracts["DiskApp.Contracts<br/>DTO, маршруты, IDiskAppHubClient"]
    Server["DiskApp.Server<br/>endpoints, хаб, hosted services"]
    Core["DiskApp.Core<br/>домен, сервисы, интерфейсы"]
    Infra["DiskApp.Infrastructure<br/>SQLite, ФС, Shell"]
    Desktop --> UI
    Desktop --> Server
    UI --> Client
    Client --> Contracts
    Server --> Core
    Server --> Infra
    Server --> Contracts
    Infra --> Core
```

Правила, которые проверяет архитектурный тест (этап 0.3):

- `Core` не ссылается ни на один проект решения и не знает об ASP.NET Core, Avalonia и SQLite.
- `UI` не ссылается на `Core` и `Infrastructure`: любой экран должен работать поверх `IDiskAppApi`, чтобы без изменений перейти на Android и в браузер.
- `Contracts` не ссылается ни на что: это общий язык сервера и клиентов.
- Платформенный код (P/Invoke, реестр, Shell) живёт в `Infrastructure` (сервисная сторона) и `Desktop` (сторона интерфейса: хоткеи, трей, IPC); `UI` остаётся кроссплатформенным.

Тест живёт в `tests/DiskApp.Core.Tests/Architecture` (и `tests/DiskApp.UI.Tests/Architecture` для правила интерфейса): он читает `ProjectReference` из всех `src/*.csproj` и сверяет их с таблицей слоёв README, а также проверяет фактические ссылки скомпилированных сборок (`GetReferencedAssemblies`). Новый проект в `src/` или новая ссылка между проектами требуют правки таблицы и теста.

Тесты: по проекту на каждый `src/` плюс `tests/DiskApp.Desktop.Tests` (одиночный экземпляр, протокол канала), общая библиотека фикстур `tests/DiskApp.Testing` (`TemporaryDirectory`, `TemporaryDatabase`; подключается ко всем тестовым проектам из `tests/Directory.Build.targets`) и `tests/DiskApp.Benchmarks`.

Один DI-контейнер на процесс: `WebApplicationBuilder.Services` получает сервисы ядра и инфраструктуры, десктоп добавляет туда же ViewModels и свои сервисы. Это упрощает время жизни объектов и тестирование. На этапе 0 контейнер строит `Program.BuildServices` из `ServiceCollection` (`AddDiskAppUi` для ViewModel и сервисов интерфейса, `DesktopShell` как `IAppCommands`); с этапа 4 его место займёт `WebApplicationBuilder`, а спайк S3 подтвердил, что интерфейс и хаб могут брать сервисы из одного `app.Services` (ADR-0004).

## 3. Запуск десктопа

Цель — иконка в трее менее чем за секунду при тёплом старте и показ окна поиска по хоткею менее чем за 100 мс. Для этого тяжёлое выполняется параллельно и лениво. Схема соответствует коду этапа 0 (`Program.Main`, `DesktopShell.Start`); в заметках — метки времени из журнала при тёплом старте (ADR-0003). Запуск Kestrel появится на этапе 4: он пойдёт в фоне сразу после захвата мьютекса, параллельно с Avalonia, а окно поиска подключит SignalR после готовности сервера.

```mermaid
sequenceDiagram
    autonumber
    participant Main as Program.Main
    participant Paths as AppPaths и Serilog
    participant Guard as SingleInstanceGuard
    participant Peer as Первый экземпляр
    participant App as Avalonia и DI
    participant Shell as DesktopShell
    participant Win32 as MessageWindow и RegisterHotKey
    Main->>Paths: папка данных (обычная или --portable), журнал в режиме shared
    Note over Main: вход в Main — 146 мс
    Main->>Guard: Mutex Local\DiskApp.{пользователь}.{сеанс}.Instance
    alt мьютекс занят — это второй экземпляр
        Main->>Peer: InstanceClient — аргументы одной строкой JSON по именованному каналу, таймаут 3 с
        Peer-->>Main: принято
        Note over Main: выход с кодом 0 (1, если канал не ответил) — ≈ 50 мс
    else первый экземпляр
        Main->>App: SetupWithLifetime (ShutdownMode OnExplicitShutdown), контейнер DI
        Note over App: Avalonia готова — 601 мс
        App->>Shell: Start(args)
        Shell->>Shell: создать QuickSearchWindow заранее и скрыть
        Shell->>Win32: message-only окно, RegisterHotKey по списку кандидатов (ADR-0006)
        Shell->>Shell: TrayIcon с NativeMenu, InstanceServer на канале
        Note over Shell: трей показан — 917 мс
        Shell->>Shell: ApplyArguments — главное окно, если не --minimized, затем --settings и --search
        Note over Shell: главное окно создаётся при первом обращении — 1 482 мс
    end
```

Регистрация горячей клавиши идёт по цепочке кандидатов; итог попадает в `HotkeyStatus`, который показывают главная страница, настройки и `InfoBar` главного окна:

```mermaid
flowchart LR
    Start([RegisterHotkey]) --> MW{Message-only окно создано?}
    MW -- нет --> Off["HotkeyStatus: не зарегистрирован<br/>поиск из трея и через --search"]
    MW -- да --> D[Ctrl+Alt+D]
    D -- занято, ошибка 1409 --> F[Ctrl+Alt+F]
    F -- занято --> S[Ctrl+Alt+Space]
    S -- занято --> Off
    D -- свободно --> OK["HotkeyStatus: действует Ctrl+Alt+D"]
    F -- свободно --> Fallback["действует запасное<br/>InfoBar «Запасное сочетание клавиш»"]
    S -- свободно --> Fallback
```

Остановка сейчас: «Выход» из меню трея → `DesktopShell.Exit`: окна закрываются (флаг `AllowClose`, иначе закрытие прячет окно в трей), снимаются хоткей, message-only окно, трей и сервер канала → `lifetime.Shutdown()`. С этапа 4 добавятся подтверждение при активных операциях, остановка подписок SignalR, `IHostApplicationLifetime.StopApplication()` и ожидание завершения писателя SQLite.

Параметры командной строки: работают `--minimized`, `--show`, `--search "q"`, `--settings`, `--portable` (`StartupArguments`); позже `--add <пути>` и URI `diskapp://open?path=…`, `diskapp://search?q=…` (этап 6.4), `--safe-mode` (этап 10), `--headless` (этап 13). Любой из них, полученный вторым экземпляром, пересылается первому; запуск без аргументов при работающем экземпляре выводит главное окно на передний план.

## 4. Данные

### Расположение

| Режим | Папка данных |
|---|---|
| Обычный | `%LocalAppData%\DiskApp\` |
| Портативный (файл `portable` рядом с exe или флаг `--portable`) | `.\data\` рядом с exe |

Внутри: `index.db` (+ `-wal`, `-shm`), `settings.json`, `backups\`, `cache\icons\`, `cache\thumbs\`, `logs\`. Пути считает `AppPaths` (`DiskApp.Infrastructure`, реализация `IAppPaths`): режим выбирается по файлу `portable` рядом с exe или аргументу `--portable`, папки журналов, кэша и резервных копий создаются при разрешении путей.

### Настройки

Этап 1.5. Модель — запись `AppSettings` в `DiskApp.Core.Settings` (горячие клавиши, внешний вид, индексация с правилами исключений, поиск, трей) с константой версии схемы. Хранилище — `JsonSettingsStore<T>` (`ISettingsStore<T>`) поверх System.Text.Json с source generation (`AppSettingsJsonContext`: camelCase, перечисления строками, отступы для правки руками). При чтении файл разбирается в `JsonObject`, по полю `schemaVersion` применяются миграции «с версии N» (словарь правок JSON), после чего версия поднимается до текущей и файл перезаписывается; отсутствующий файл даёт значения по умолчанию без записи; повреждённый откладывается в `settings.corrupt-<дата>.json`, берутся значения по умолчанию, итог возвращается в `SettingsLoadResult`. Запись атомарная: `settings.json.tmp` со сбросом на диск и `File.Move` с заменой. Подписчики `Subscribe` получают каждое сохранение; трансляция в SignalR (`OnSettingsChanged`) — этап 4.

Свойства настроек объявлены с `set`, а не `init`: генератор System.Text.Json строит объект инициализатором и затирает значения по умолчанию у полей, которых нет в файле. Договорённость: экземпляр из хранилища не меняется на месте, изменение — копия через `with` и `SaveAsync`.

### SQLite

Этап 1.3. `SqliteConnectionFactory` открывает соединения из пула Microsoft.Data.Sqlite и на каждом применяет PRAGMA: `journal_mode=WAL`, `synchronous=NORMAL`, `temp_store=MEMORY`, `cache_size=-65536` (64 МБ), `mmap_size=256 МБ`, `foreign_keys=ON`, `busy_timeout=5000`. Один писатель — `SqliteWriter`: выделенный поток с одним постоянным соединением читает задания из `Channel`; задание — синхронный делегат, его исключение возвращается вызывающему и не останавливает очередь. Чтение — из пула соединений параллельно, в WAL читатель видит снимок и не ждёт писателя (проверено тестом). `SqliteDatabase` при старте делает `PRAGMA quick_check`; повреждённый файл (или не-база) переименовывается в `index.corrupt-<дата>.db`, создаётся новый, итог `DatabaseInitialization.WasRecovered` сообщает оболочке, что индекс надо построить заново; предложение резервной копии — этап 16.1. Обслуживание: `OptimizeAsync` (`PRAGMA optimize` и усечение WAL), `VacuumAsync`; расписание при простое — этап 10.

Даты хранятся целым числом — тиками UTC (`DateTimeOffset.UtcTicks`, 100 нс от 0001-01-01; `SqliteTime`, ADR-0007): полная точность времени файловой системы, 8 байт вместо 28 у текста ISO 8601, порядок чисел совпадает с хронологическим. Прочитать в SQL: `datetime((x - 621355968000000000) / 10000000, 'unixepoch')`. Dapper настроен на `snake_case` → `PascalCase` и этот формат дат (`DapperConfiguration`).

Миграции — SQL-скрипты `V001__init.sql`, `V002__…` во встроенных ресурсах `DiskApp.Infrastructure` (`Sqlite/Migrations`), `SqliteMigrator` применяет недостающие по порядку, каждую в своей транзакции вместе с записью в `schema_version`; тест проверяет апгрейд с первой версии до текущей.

Репозитории (этап 1.4, `Sqlite/Repositories`) реализуют интерфейсы `DiskApp.Core.Repositories`: чтение в отдельном соединении на пуле потоков, запись через `SqliteWriter`. `SqliteFileIndexRepository.UpsertAsync` — три подготовленных запроса на строку в одной транзакции: пометить неизменённую строку текущим `scan_id` (совпали размер, время, атрибуты, имя), вставить новую (`ON CONFLICT DO NOTHING`), иначе обновить; родитель определяется подзапросом по `rel_path` родительской папки, поэтому папки в пакете идут раньше детей. Поддеревья удаляются и переименовываются диапазоном `rel_path >= 'путь\' AND rel_path < 'путь]'` по индексу `UNIQUE(root_id, rel_path)`; число удалённых считается отдельным `COUNT` в той же транзакции, потому что каскад по `parent_id` удаляет детей раньше, чем до них доходит `DELETE`.

### Схема (V001 реализована в этапе 1.3; таблицы этапов 16 и 17 отмечены и появятся позже)

```mermaid
erDiagram
    roots ||--o{ entries : contains
    entries ||--o| usage : has
    entries ||--o{ entry_tags : tagged
    tags ||--o{ entry_tags : used_in
    entries ||--o{ favorites : pinned_as
    collections ||--o{ collection_items : includes
    entries ||--o{ collection_items : member_of
    collectors ||--o{ triage_items : collects
    entries ||--o| triage_items : queued_as
    warehouses ||--o{ triage_decisions : receives
    triage_items ||--o{ triage_decisions : decided_by

    roots {
        integer id PK
        text path
        text label
        integer is_enabled
        integer include_hidden
        integer include_system
        integer max_depth
        integer last_scan_utc
        integer status
        text error_message
        integer created_utc
    }
    entries {
        integer id PK
        integer root_id FK
        integer parent_id FK
        text name
        text name_folded
        text ext
        integer is_dir
        integer size
        integer created_utc
        integer modified_utc
        integer attributes
        text rel_path
        integer kind
        integer scan_id
    }
    usage {
        integer entry_id PK
        integer open_count
        integer last_opened_utc
    }
    favorites {
        integer entry_id PK
        integer sort_order
        integer pinned_utc
    }
    tags {
        integer id PK
        text name
        text name_folded
        text color
    }
    entry_tags {
        integer entry_id FK
        integer tag_id FK
    }
    collections {
        integer id PK
        text name
        integer kind
        text query
    }
    collection_items {
        integer collection_id FK
        integer entry_id FK
        integer sort_order
    }
    collectors {
        integer id PK
        text path
        integer include_subdirs
        integer settle_seconds
        integer is_enabled
    }
    warehouses {
        integer id PK
        text name
        text path
        integer slot
        text profile_json
        integer parent_id FK
    }
    triage_items {
        integer id PK
        integer entry_id FK
        integer collector_id FK
        integer detected_utc
        text source_host
        integer state
    }
    triage_decisions {
        integer id PK
        integer item_id FK
        integer warehouse_id FK
        text target_path
        integer decided_utc
        integer by_rule
    }
```

Вне диаграммы: виртуальная таблица `entries_fts` (FTS5 только по `name`, `tokenize='trigram'`, external content на `entries`, синхронизируется триггерами на вставку, удаление и изменение имени), `query_history` (запрос, счётчик, время), `meta`, `scan_log`, `schema_version`, а также таблицы правил разбора `triage_rules` (этап 17.5), резервных копий `backups` и заданий `backup_jobs` (этап 16).

Индексы на `entries`: `UNIQUE(root_id, rel_path)`, `(root_id, parent_id)`, `name_folded` (имя в нижнем регистре по правилам .NET — `COLLATE NOCASE` не сворачивает кириллицу, ADR-0005), `ext`, `modified_utc`, `size`, `kind`, `(root_id, scan_id)` для mark-and-sweep. Внешние ключи с `ON DELETE CASCADE`: удаление корня убирает его записи, статистику, избранное, привязки тегов и элементы коллекций; `parent_id` каскадирует на поддерево как страховка от сирот.

## 5. Индексация

Этап 2, реализовано 2026-10-11. Логика обхода, сверки, склейки событий и оркестрации живёт в `DiskApp.Core/Indexing` и проверяется без ОС; тонкий слой над ОС — в `DiskApp.Infrastructure/FileSystem`: `FileSystemLister` (одна папка через `FileSystemEnumerable`, сведения о пути, идентичность папки), `FileSystemRootWatcher` (`FileSystemWatcher`), `DriveInfoProvider` (тип носителя и физический диск через IOCTL), `ThreadBackgroundPriority`. Три источника изменений — полное сканирование, сверка и события наблюдателя — сходятся в одном писателе.

```mermaid
flowchart LR
    subgraph Orchestrator["IndexerService"]
        Backlog["IndexTaskBacklog<br/>приоритеты, поглощение,<br/>1 задача на диск и корень"]
    end
    subgraph Full["Полное сканирование — ScanPipeline"]
        Scanner["DirectoryScanner<br/>стек, потоки по папкам верхнего уровня,<br/>исключения, ссылки, пауза"] --> Ch["Channel&lt;EntryBatch&gt;<br/>ёмкость 4, пакеты по 5 тыс."]
        Ch --> Upsert["UpsertDetailedAsync<br/>с номером сканирования"]
        Upsert --> Sweep["MarkSubtree для непрочитанных,<br/>Sweep остальных"]
    end
    subgraph Reconcile["Сверка — ReconcileRunner"]
        Diff["папка на диске ↔ дети в индексе<br/>пишутся только отличия"]
    end
    subgraph Watch["Слежение — RootChangeProcessor"]
        FSW["FileSystemRootWatcher<br/>на каждый корень"] --> Coal["ChangeCoalescer<br/>пауза 400 мс, не позже 900 мс"] --> Apply["ChangeApplier<br/>переименования, проверка путей"]
    end
    Backlog --> Full
    Backlog --> Reconcile
    Apply -- "новая папка, переполнение" --> Backlog
    Upsert --> W["SqliteWriter"]
    Sweep --> W
    Diff --> W
    Apply --> W
    W --> DB[("index.db")]
    Full --> Bus["IIndexEventBus<br/>IndexProgress, EntriesChanged, RootStatusChanged"]
    Reconcile --> Bus
    Apply --> Bus
```

**Сканер.** `DirectoryScanner` читает начальную папку сам, её подпапки делит между выделенными потоками с фоновым приоритетом (степень — по типу диска: SSD 4, сеть 2, HDD и съёмные 1). Каждый поток обходит свои папки целиком на собственном стеке и складывает записи в свои пакеты, поэтому родитель всегда попадает в канал раньше детей, и `parent_id` вычисляется подзапросом при вставке. `ExclusionMatcher` решает по glob-шаблонам имени и пути (`GlobPattern`: частые формы без регулярных выражений), скрытым и системным атрибутам, размеру и глубине. Junction и символические ссылки индексируются, но не обходятся; при `FollowReparsePoints` — обходятся с защитой от циклов по идентичности папки (`DescentGuard`: том и номер файла). Ошибка чтения папки записывается в `scan_log`, обход продолжается, а записи этой папки в индексе сохраняются (`MarkSubtreeAsync`).

**Метаданные папок.** NTFS хранит копию времени и атрибутов папки в записи родителя и обновляет её лениво — например, когда папку открывают для перечисления. Поэтому для папок (кроме ссылок) сведения берутся из самой папки отдельным запросом, как у наблюдателя; иначе каждое следующее сканирование «меняло» бы недавно тронутые папки. Для файлов данные перечисления точны.

**Полное сканирование** (`ScanPipeline`) — mark-and-sweep: новый номер сканирования (`NextScanIdAsync`), пакеты записываются с ним через `UpsertDetailedAsync` (неизменённые строки только помечаются), затем непомеченное удаляется. Недоступный корень не трогает индекс (исход `RootMissing`), исчезнувшее или исключённое поддерево удаляется. Прерванное сканирование ничего не удаляет. Используется при первом сканировании, после восстановления индекса, по «Пересканировать» и «Пересобрать».

**Сверка** (`ReconcileRunner`) — по разнице: обход папок, сравнение содержимого каждой с её детьми в индексе (`GetChildrenOfPathAsync`), запись только отличий; смена регистра имени — переименование с сохранением идентификатора. Читает столько же, сколько полное сканирование, но почти не пишет в WAL. Используется при запуске, при возвращении корня, по переполнению буфера наблюдателя, для новых папок от наблюдателя и по расписанию (раз в 6 ч, сетевые корни — вчетверо чаще).

**Слежение.** `RootChangeProcessor` складывает события `FileSystemWatcher` в канал, `ChangeCoalescer` копит их до паузы 400 мс (но не дольше 900 мс от первого события) и сводит повторы к множеству путей, сохраняя порядок переименований. `ChangeApplier` переносит переименованные поддеревья одним запросом (идентификаторы, статистика и избранное сохраняются), остальные пути проверяет на диске: есть — запись или обновление, нет — удаление поддерева. Запись появляется, только если её родитель уже в индексе, поэтому файлы в скрытых и исключённых папках не просачиваются через события. Новая папка ставит сверку своего поддерева (её могли перенести в корень с содержимым). Во время полного сканирования наблюдатель помечает свои записи его номером, а тронутые пути перепроверяются после него (`RootIndexState`). Пока индексация на паузе, события копятся; больше 10 тыс. путей — пакет заменяется сверкой корня.

**Оркестрация** (`IndexerService`). При старте — корни из репозитория, наблюдатели, полное сканирование или сверка каждого. Очередь `IndexTaskBacklog` упорядочивает задачи (сверка поддерева по событию > сверка корня > полное сканирование) и поглощает покрытые; одновременно выполняется не больше одной задачи на физический диск (`disk:N`) и на корень, до двух дисков сразу. Недоступный корень (`Offline`) теряет наблюдателя, но не индекс, и опрашивается раз в 30 с; сбой наблюдателя при живом корне — перезапуск со сверкой. Управление: `PauseAll`, `ResumeAll`, `RescanRoot`, `ReconcileRoot`, `RebuildAllAsync`, `RefreshRootsAsync`; состояние — `GetStatuses` и `GetAggregateState` для трея. Журнал — `LoggerMessage` через Microsoft.Extensions.Logging. Запуск в процессе десктопа — вместе с сервером на этапе 4.

Состояния корня: `Online → Scanning → Online`, `* → Paused → *` (пауза), `Online → Offline → Online` (с последующей сверкой), `* → Error` с сообщением (повтор — следующей задачей или вручную).

**Облачные заглушки** (`FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS`, `RECALL_ON_OPEN`, `OFFLINE`) — атрибуты сохраняются в записи (`FileEntry.IsCloudPlaceholder`); индексация содержимое не читает вовсе, предпросмотр и миниатюры (этапы 7 и 12) обязаны проверять признак.

## 6. Поиск

Этап 3. Конвейер запроса:

1. **Парсер** превращает строку в `SearchQuery`: термы, фразы, фильтры (`ext`, `type`, `in`, `root`, `size`, `modified`, `created`, `is`, `tag`, `fav`), отрицания, опционально регулярное выражение. Ошибки терпимы: незакрытая кавычка или неизвестный фильтр становятся текстом.
2. **Планировщик** выбирает стратегию по длине терма: от трёх символов — FTS5 `MATCH` по trigram; один-два символа — префикс по индексу колонки `name_folded` (имя в нижнем регистре по правилам .NET: `COLLATE NOCASE` в SQLite не сворачивает кириллицу, ADR-0005); пустой запрос — подборка недавних и избранных.
3. **SQL** с параметрами и `WHERE` по фильтрам отдаёт ограниченный набор кандидатов (порядка 5 000) без встроенного `rank`: bm25 стоит до сотен миллисекунд на частых триграммах и не отражает полезность для имён файлов (ADR-0005).
4. **Ранжирование** в памяти: тип совпадения (точное имя > начало имени > начало слова > подстрока > нечёткое), покрытие имени, frecency `Σ log(1 + opens) · decay(дни)`, избранное, штраф за глубину, свежесть изменения; веса из настроек с пресетами; детерминированный tie-break.
5. **Подсветка** — диапазоны совпадений в имени и пути.
6. **Страница** — keyset-пагинация по `(score, id)`, приблизительное общее количество, время выполнения.

Поиск по мере ввода: клиент отменяет предыдущий запрос через `CancellationToken`; сервер прерывает SQLite по отмене; при уточнении запроса (добавлен символ) кандидаты предыдущего ответа фильтруются в памяти, пока гарантирована полнота.

## 7. API и события

Этап 4. Minimal API, префикс `/api/v1`, группы endpoints: `roots`, `index`, `search`, `entries` (включая `icon`, `thumbnail`, `content`, действия `open`/`reveal`/…), `operations`, `upload`, `favorites`, `tags`, `collections`, `settings`, `app`; позже `collectors`, `warehouses`, `triage` (этап 17), `backups`, `backup-jobs` (этап 16), `devices` (этап 13). Полный перечень маршрутов — в PLAN.md, подэтапы 4.3, 16.1, 16.2, 17.8.

Контракты в `DiskApp.Contracts`: DTO с JSON source generation, перечисления строками, camelCase, маршруты константами. Ошибки — `ProblemDetails` с кодом домена. OpenAPI документ генерируется встроенными средствами, Scalar UI включён только в Development.

SignalR: один хаб `DiskAppHub : Hub<IDiskAppHubClient>`. События: `OnIndexProgress`, `OnRootStatusChanged`, `OnEntriesChanged` (пакетами, батчинг 250 мс), `OnOperationProgress`, `OnOperationCompleted`, `OnOperationNeedsDecision`, `OnSettingsChanged`; позже `OnTriageQueueChanged`, `OnTriageDecisionApplied`, `OnWarehousesChanged`, `OnBackupProgress`, `OnBackupCompleted`. При переподключении клиент получает текущий статус индекса и активные операции.

Клиент (`DiskApp.Client`): `IDiskAppApi` над `HttpClient` с устойчивостью (повторы только для идемпотентных GET), `DiskAppHubConnection` с автоматическим переподключением и ресинхронизацией. Платформенного кода в клиенте нет.

## 8. Файловые операции

Этап 4.5. `FileOperationService` владеет очередью операций (copy, move, rename, delete, create-folder, create-file, upload) с состояниями `Queued → Running → Completed | Failed | Cancelled` и паузой `WaitingDecision` для конфликтов в режиме Ask. Прогресс — файлы, байты, текущий файл, скорость, оценка времени — публикуется через шину и SignalR.

Правила: перемещение в пределах тома — `File.Move`, между томами — копирование с проверкой и удаление; удаление — только в корзину через `IFileOperation` с `FOF_ALLOWUNDO`, безвозвратное — отдельный явный флаг; конфликты — Ask / Skip / Overwrite / KeepBoth; undo для rename, move и create в рамках сессии; индекс обновляется сразу после операции, не дожидаясь watcher'а. Операции разбора входящих (этап 17) и заданий резервного копирования (этап 16.2) используют тот же сервис.

## 9. Интерфейс

Этапы 6–9. `DiskApp.UI` — общий Avalonia-проект: `ViewModels/` (CommunityToolkit.Mvvm), `Views/`, `Controls/`, `Services/` (навигация, диалоги, InfoBar, диспетчер), `Styles/` (дизайн-токены), `Localization/` (`.resx` RU и EN). `DiskApp.Desktop` добавляет окна и платформенное: трей, хоткеи, IPC, автозапуск. Класс приложения `App` (тема `FluentAvaloniaTheme`, ресурсы) тоже лежит в `DiskApp.UI`, чтобы его переиспользовали Android и Browser; десктоп создаёт `ClassicDesktopStyleApplicationLifetime` явно в `Program` и сам назначает окна, поэтому `App` не знает о `MainWindow`.

Окна:

| Окно | Назначение | Особенности |
|---|---|---|
| `QuickSearchWindow` | Быстрый поиск | Без рамки, поверх всех, Acrylic, скруглённые углы через DWM, создаётся заранее, скрывается по `Esc` и потере фокуса |
| `TriageWindow` | Сессия разбора входящих (этап 17) | Тот же стиль; склады кнопками с закреплёнными номерами |
| `MainWindow` | Главное окно | `NavigationView`, собственный заголовок, Mica, запоминает размер и монитор |

Стиль Windows 11: FluentAvaloniaUI (`PreferSystemTheme`, `PreferUserAccentColor`), `TransparencyLevelHint` Mica → Acrylic → сплошной цвет, радиусы 8 px у окон и 4 px у контролов, Segoe UI Variable с fallback, Segoe Fluent Icons / FluentIcons, анимации 150–250 мс с учётом системного «уменьшить движение». Виртуализация во всех списках, LRU-кэш растровых иконок и миниатюр.

Прототип интерфейса (2026-10-10) собран до ядра на макетных данных: `ISearchService` с реализацией `SampleSearchService` в `DiskApp.UI` заменяется обёрткой над `IDiskAppApi` в этапе 5, интерфейс при этом не меняется. Команды оболочки (`IAppCommands`: показать поиск, главное окно, страницу по ключу) реализует `DesktopShell` в `DiskApp.Desktop`, который владеет треем, хоткеем и окнами и разбирает аргументы запуска (`StartupArguments`). `MainWindow` наследует `FAAppWindow` FluentAvalonia (заголовок и системные кнопки в стиле Windows 11), Mica включает `Backdrop.ApplyMica` с fallback на сплошной цвет темы; `QuickSearchWindow` — окно без рамки с `ExperimentalAcrylicBorder` и скруглением через `DwmSetWindowAttribute`. Страницы подбираются по `DataTemplate` из `App.axaml`, без рефлексии; подсветка совпадений — контрол `HighlightTextBlock`.

Состав оболочки и интерфейса после этапа 0:

```mermaid
flowchart TB
    subgraph Desktop["DiskApp.Desktop"]
        Program["Program.Main<br/>AppPaths · Serilog · SingleInstanceGuard · DI"]
        Shell["DesktopShell : IAppCommands"]
        Tray["TrayIcon · NativeMenu"]
        Hotkey["MessageWindow · GlobalHotkey<br/>Platform/Windows"]
        Pipe["InstanceServer · InstanceClient<br/>именованный канал"]
        MainWin["MainWindow : FAAppWindow<br/>Mica (Backdrop)"]
        QSWin["QuickSearchWindow<br/>Acrylic · DWM-углы · показ у курсора"]
        Program --> Shell
        Shell --> Tray
        Shell --> Hotkey
        Shell --> Pipe
        Shell --> MainWin
        Shell --> QSWin
    end
    subgraph UI["DiskApp.UI"]
        MainVM["MainViewModel<br/>страницы · InfoBar · состояние индекса"]
        Pages["HomeViewModel · SettingsViewModel<br/>SearchPage · заглушки разделов"]
        QSVM["QuickSearchViewModel<br/>debounce 60 мс · клавиатура"]
        Search["ISearchService"]
        Sample["SampleSearchService<br/>макет до этапа 5"]
        Status["HotkeyStatus · IThemeService"]
        MainWin --> MainVM --> Pages
        QSWin --> QSVM
        Pages --> Search
        QSVM --> Search
        Search --> Sample
        Pages --> Status
    end
    Hotkey -. WM_HOTKEY .-> Shell
    Pipe -. аргументы второго экземпляра .-> Shell
    Shell -. регистрирует итог .-> Status
```

## 10. Платформенные сервисы

| Интерфейс (Core) | Windows-реализация | Где живёт | Этап |
|---|---|---|---|
| `IPlatformShell` | `ShellExecuteEx`, `SHOpenWithDialog`, `SHOpenFolderAndSelectItems`, `IFileOperation`, буфер обмена с `DataFormats.Files` | Infrastructure | 7 |
| `IIconProvider` | `SHGetFileInfo`, `IShellItemImageFactory`, кэш PNG на диске | Infrastructure | 7 |
| Горячая клавиша | `GlobalHotkey` + `MessageWindow`: `RegisterHotKey` с `MOD_NOREPEAT`, приём `WM_HOTKEY` собственным message-only окном (`CreateWindowEx` с `HWND_MESSAGE`); `Win32Properties` в Avalonia 12 нет. Цепочка сочетаний `Ctrl+Alt+D` → `F` → `Space` (ADR-0006), итог в `HotkeyStatus`; настраиваемое сочетание — этап 6.5 | Desktop | есть (0.6) |
| Трей | Avalonia `TrayIcon` + `NativeMenu` (Открыть, Быстрый поиск, Настройки, Выход); щелчок переключает окно поиска | Desktop | есть (0.6) |
| Одиночный экземпляр, IPC | `SingleInstanceGuard` (`Mutex` на пользователя и сеанс) + `InstanceServer`/`InstanceClient` (`NamedPipeServerStream`, `CurrentUserOnly`, строка JSON через source generator) | Desktop | есть (0.6) |
| Окна в стиле Windows 11 | `Backdrop.ApplyMica` (Mica с fallback), `WindowEffects` (`DwmSetWindowAttribute` для углов, `GetCursorPos` для показа у курсора) | Desktop | есть (0.6) |
| Автозапуск | `HKCU\…\CurrentVersion\Run` | Desktop | 6 |
| Интеграции оболочки | `shell:sendto`, `HKCU\Software\Classes\*\shell\DiskApp`, протокол `diskapp://` | Desktop | 8 |
| Quick Switch диалогов | Win32 (`#32770`, `WM_SETTEXT`) или UI Automation, за флагом | Desktop | 8 |
| Источник файла | Поток `Zone.Identifier` (`HostUrl`, `ReferrerUrl`) | Infrastructure | 17 |
| `IUserActivityMonitor` | `GetLastInputInfo`, `SHQueryUserNotificationState`, `GetForegroundWindow` + класс процесса, `CapabilityAccessManager\ConsentStore` (микрофон, камера), `WTSRegisterSessionNotification` (блокировка сеанса), `GetSystemPowerStatus` | Desktop | 17 |

Для Linux и macOS (этап 12.7) предусмотрены реализации на `xdg-open` / `open`, SharpHook для хоткеев и корзина по спецификации freedesktop / Finder; интерфейс и клиент не меняются.

## 11. Разбор входящих

Этап 17. Модель «общий склад → профильные склады»:

- **`Collector`** (общий склад) — папка под присмотром; новые файлы после «времени успокоения» попадают в очередь `triage_items` с источником и признаками.
- **`Warehouse`** (профильный склад) — именованное назначение с профилем (виды, расширения, ключевые слова, домены), закреплённым номером 1–9, шаблоном подпапки и, при каскаде, своим набором складов.
- **`TriageService`** считает подсказки (правила → профиль → история решений → недавние склады → остальные по номерам), исполняет решения через `FileOperationService`, пишет журнал решений с отменой и обучает счётчики признаков.
- **Правила** (`triage_rules`) — условия → действие → режим (автоматически или только предлагать); создаются из решений.
- **Напоминания** — планировщик «умного момента» (`TriageReminderScheduler`) на стороне интерфейса: поводы (новые файлы, порог, расписание, давность) ставят заявку; детектор занятости `IUserActivityMonitor` (простой ввода, `SHQueryUserNotificationState`, звонок по микрофону или камере, категория активного окна, блокировка сеанса) решает, хороший ли момент; умная отсрочка с нарастанием, лимиты частоты, эскалация бейдж → тихое уведомление → `InfoBar`, обучение по реакциям. Без модальных окон и звука.

Интерфейс: `TriageWindow` для сессии и страница «Входящие» как центр управления. Подсказки объяснимы: рядом с каждой — причина.

## 12. Резервное копирование

Этап 16. `BackupService` делает консистентный снимок `index.db` через SQLite Online Backup API (`SqliteConnection.BackupDatabase`) без остановки индексации, упаковывает его с `settings.json` и `manifest.json` (версии, контрольные суммы) в zip, при необходимости шифрует AES-256 с ключом из пароля пользователя. Расписание, несколько назначений, ретенция (N последних или GFS), страховочные копии перед миграцией, пересборкой и восстановлением, проверка целостности перед восстановлением, выборочное восстановление, JSON-экспорт метаданных.

Задания резервного копирования файлов (16.2) — модель «источник → назначение → режим (зеркало, версии, снимок) → расписание → хранение», инкрементальность по индексу, исполнение через `FileOperationService`, журнал запусков и восстановление байт-в-байт.

## 13. Безопасность

Локальный режим (1.0): сервер слушает только loopback; CORS выключен; за флагом — токен сессии в заголовке `X-DiskApp-Token`, известный только десктопу. Все пути из запросов нормализуются и проверяются на принадлежность разрешённым корням (запрет `..`, проверка выхода через символические ссылки). Лимиты размеров запросов и загрузок. В логах нет содержимого файлов и секретов.

Удалённый режим (этап 13): HTTPS с самоподписанным сертификатом и пиннингом отпечатка в клиентах; сопряжение по QR с одноразовым кодом; подписанные отзываемые токены устройств; права на устройство (только чтение, разрешённые корни, загрузка, удаление); rate limiting; журнал доступа; блокировка после неудачных попыток; модель угроз в `docs/SECURITY.md`.

## 14. Наблюдаемость и диагностика

Serilog пишет структурные логи в `logs\diskapp-<дата>.log`: ротация по дням, хранятся 14 файлов, режим `shared` (второй экземпляр пишет в тот же файл), уровень Debug (`Microsoft` — Information), `Log.Logger` отдаётся через DI. Метки `StartupTimer` (время от старта процесса) пишутся на входе в `Main`, после готовности Avalonia, при показе трея, открытии главного окна и первом кадре окна поиска — по ним снимаются замеры раздела 16. `Log.Fatal` ловит необработанное исключение в `Main`.

Позже (этапы 10 и 4): настраиваемый уровень и ограничение размера; глобальные обработчики исключений (AppDomain, TaskScheduler, Avalonia) логируют и показывают окно «Что-то пошло не так» с кнопкой «Собрать диагностический архив» (логи, настройки без секретов, версия, окружение); endpoint `/health`; режим `--safe-mode` отключает автоиндексацию и интеграции для разбора проблем. Телеметрии нет.

## 15. Сборка и поставка

Этапы 0 и 11. `.slnx`, `Directory.Build.props` (net10.0, nullable, warnings as errors, анализаторы `latest-recommended`, стиль из `.editorconfig` при сборке, `GenerateDocumentationFile` ради правила IDE0005, `VersionPrefix`), `Directory.Packages.props` (центральные версии). Тесты — xunit v3 на Microsoft.Testing.Platform (`global.json` → `test.runner`): отчёты TRX и покрытие дают расширения `Microsoft.Testing.Extensions.TrxReport` и `CodeCoverage`, а не VSTest-коллекторы.

CI — GitHub Actions на `windows-latest` (`.github/workflows/ci.yml`): SDK по `global.json`, кэш NuGet, `restore` → `build -c Release` → `test` с TRX и Cobertura (сводка покрытия ReportGenerator в job summary, артефакт `test-results`) → `dotnet format --verify-no-changes`; запускается на push в `main`, pull request и вручную. Ветка `main` защищена от принудительной отправки и удаления (`scripts/protect-main.ps1`); готовые ветки сливаются в неё локально fast-forward (CONTRIBUTING.md).

Публикация сейчас — `scripts/publish.ps1`: `dotnet publish` self-contained, single-file, ReadyToRun для `win-x64` (или `win-arm64`), результат `releases\diskapp_v{версия}_{стадия}[_singlefile].exe`; стадия (`alpha`, `beta`, `rc`, `release`) становится суффиксом SemVer к `VersionPrefix`. Такая сборка весит около 200 МБ из-за self-contained ASP.NET Core и Avalonia; бюджет установщика 80 МБ — задача этапа 11 (тримминг, версия из git через MinVer, установщик и дельта-обновления Velopack с GitHub Releases как источником, портативная сборка zip с файлом `portable`).

## 16. Бюджеты и измерения

Бюджеты производительности и способы измерения — в PLAN.md, раздел 8. Результаты фактических замеров (дата, конфигурация машины, цифры) записываются сюда по итогам этапов 0.6 (спайки), 3.6, 4.7 и 10.1.

Итоги спайков этапа 0.6 (код S3 и S4 — в `spikes/`, S1 и S2 проверены прототипом и тестами):

| Спайк | Что проверяли | Итог | ADR |
|---|---|---|---|
| S1 | Стиль Windows 11 в Avalonia 12 + FluentAvalonia 3 | `FAAppWindow`, Mica с fallback, Acrylic и DWM-углы у окна поиска, тема и акцент системы; показ окна поиска 82 мс в первый раз, 1 мс далее | [0002](ADR/0002-windows11-shell-in-avalonia.md) |
| S2 | Трей, горячая клавиша, один экземпляр | `TrayIcon` + `NativeMenu`, `RegisterHotKey` с message-only окном (в Avalonia 12 нет `Win32Properties`), `Mutex` + именованный канал; трей через 917 мс, второй экземпляр ≈ 50 мс | [0003](ADR/0003-tray-hotkey-single-instance.md) |
| S3 | Kestrel + Minimal API + SignalR внутри процесса | `CreateSlimBuilder`, порт из `app.Urls`, общий DI подтверждён; старт 138 мс и +17 МБ, loopback 0,1–0,2 мс | [0004](ADR/0004-kestrel-in-process.md) |
| S4 | SQLite FTS5 trigram на 1 млн записей | FTS только по имени, подстрока с 3 символов через `MATCH`, 1–2 символа — префикс по `name_folded`, без `LIKE` (регистр кириллицы) и без `bm25`; бюджет БД ужесточён до 250 МБ | [0005](ADR/0005-fts5-trigram-index-layout.md) |
| S5 (опц.) | `FileSystemWatcher` под нагрузкой | Отложен до этапа 2.3; риск переполнения буфера закрыт пересканированием | — |

Замеры:

| Дата | Конфигурация | Метрика | Бюджет | Результат |
|---|---|---|---|---|
| 2026-10-10 | Ноутбук разработчика: Windows 11, 22 CPU, SSD, .NET 10.0.12, Release framework-dependent, тёплый старт | Старт до иконки в трее | < 1 с | 917 мс (вход в `Main` 146 мс, Avalonia готова 601 мс) |
| 2026-10-10 | то же | Главное окно открыто | — (не на критическом пути) | 1 482 мс |
| 2026-10-10 | то же | Показ окна поиска до первого кадра | < 100 мс | 82 мс первый показ, 1 мс последующие |
| 2026-10-10 | то же | Второй экземпляр: `Main` → аргументы переданы → выход | — | ≈ 50 мс |
| 2026-10-10 | то же, спайк S3 | Старт Kestrel + SignalR (`CreateSlimBuilder`, холодный) | не задерживать трей; память < 150 МБ | 138 мс, рабочий набор +17 МБ |
| 2026-10-10 | то же, спайк S3 | Loopback: HTTP GET / SignalR Invoke / push сервер→клиент, медиана | доли мс | 0,12 / 0,16 / 0,17 мс |
| 2026-10-10 | то же, спайк S4, SQLite 3.53.3 | Вставка 1 млн записей, FTS по имени | ≥ 2 000 файлов/с (ограничивает сканер) | 7,7 с (129 тыс. строк/с), файл 161 МБ |
| 2026-10-10 | то же, спайк S4 | Вставка 1 млн записей, FTS по имени и пути | — (отклонено) | 16,5 с (60 тыс. строк/с), файл 334 МБ |
| 2026-10-10 | то же, спайк S4 | Размер БД на 1 млн записей, FTS по имени | ≤ 250 МБ | 161 МБ |
| 2026-10-10 | то же, спайк S4 | Поиск от 3 символов, trigram `MATCH` (`COUNT`) | < 50 мс | 0,6–5 мс при 9–35 тыс. совпадений; с `ORDER BY rank` 22–34 мс, на очень частой триграмме 532 мс — поэтому ранг свой |
| 2026-10-10 | то же, спайк S4 | Поиск по 2 символам (полный скан `LIKE`) | < 50 мс | 140–390 мс — вне бюджета, заменён префиксом по `name_folded` (ADR-0005) |
| 2026-10-10 | то же, Intel Core Ultra 7 155H, BenchmarkDotNet 0.15.8, Release, `tests/DiskApp.Benchmarks` | `SqliteFileIndexRepository.UpsertAsync`: 100 тыс. записей пакетами по 5 тыс. (этап 1.4) | ≤ 5 с | 4,07 с (24,6 тыс. строк/с; три запроса на строку плюс подзапрос родителя), 358 МБ выделений |
| 2026-10-10 | то же | `RenameSubtreeAsync`: поддерево на 50 тыс. записей (этап 1.4) | ≤ 200 мс | 184 мс — в бюджете с небольшим запасом; основная стоимость — перестроение индекса `rel_path` |
| 2026-10-11 | то же, SSD (`disk:0`), 4 потока обхода, `scan-budget` в `tests/DiskApp.Benchmarks`, тёплый кэш ФС | Первое полное сканирование 100 тыс. файлов в 3 100 папках с записью в индекс (этап 2.6) | ≤ 60 с | 4,5 с (≈ 23 тыс. записей/с; упирается в запись с FTS) |
| 2026-10-11 | то же | Пик рабочего набора во время первого сканирования | ≤ 100 МБ | +79 МБ |
| 2026-10-11 | то же | Сверка без изменений / повторное полное сканирование | — | 0,9 с / 0,85 с |
| 2026-10-11 | то же | Шесть повторных прогонов «сканирование + сверка»: дескрипторы и рабочий набор процесса | не расти | 335 дескрипторов и ~155 МБ на плато (кэш страниц SQLite 64 МБ на соединение и mmap файла индекса); бюджет памяти в простое (150 МБ) — проверить на этапе 10, при необходимости уменьшить `cache_size` и `mmap_size` |
| 2026-10-11 | то же, тест `RootChangeProcessorTests` | Отражение создания файла в индексе (пауза склейки 150 мс в тесте, 400 мс в приложении) | ≤ 1 с | меньше 1 с |
