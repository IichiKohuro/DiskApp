# Changelog

Все заметные изменения проекта фиксируются в этом файле.

Формат — [Keep a Changelog](https://keepachangelog.com/ru/1.1.0/), версионирование — [SemVer](https://semver.org/lang/ru/).
Пока проект не выпущен, записи копятся в разделе Unreleased; при выпуске версии раздел переименовывается в `[X.Y.Z] — ГГГГ-ММ-ДД`.

## [Unreleased]

### Added

- README.md: описание продукта, принципы, возможности, архитектура, стек, дорожная карта.
- PLAN.md: поэтапный план разработки с критериями готовности, решениями, рисками и бюджетами производительности.
- Каркас репозитория: `.gitignore`, `.editorconfig`, `global.json`, LICENSE (MIT), CLAUDE.md, CHANGELOG.md.
- `docs/`: ARCHITECTURE.md, ADR/0001 (архитектурный стиль), QA_CHECKLIST.md, FEEDBACK.md.
- Каркас решения: `DiskApp.slnx`, `Directory.Build.props`, `Directory.Packages.props`; проекты `src/` (Core, Infrastructure, Contracts, Server, Client, UI, Desktop) и `tests/` (Core, Infrastructure, Server, UI, Benchmarks); архитектурные тесты слоёв и дымовые тесты SQLite FTS5, Kestrel на loopback и headless-интерфейса; `DiskApp.Desktop` показывает пустое окно в теме FluentAvalonia.
- CONTRIBUTING.md: единица работы, ветки, Conventional Commits, стиль кода, шаблон теста, фикстуры, карта документации.
- `tests/DiskApp.Testing`: фикстуры `TemporaryDirectory` и `TemporaryDatabase` с гарантированной очисткой; подключаются ко всем тестовым проектам автоматически.
- Прототип интерфейса на макетных данных (этапы 6–7 вытянуты вперёд): главное окно в стиле Windows 11 (`FAAppWindow`, Mica, `NavigationView`, страницы «Главная», «Поиск», «Настройки» и заглушки разделов с описанием этапа), окно быстрого поиска (без рамки, Acrylic, скруглённые углы, подсветка совпадений, клавиатура, `Ctrl+1…9`), трей с меню, глобальная горячая клавиша `Ctrl+Alt+Space` с уведомлением о конфликте, аргументы запуска `--minimized`, `--show`, `--settings`, `--search`, `--portable`, журнал Serilog в `%LocalAppData%DiskAppogs`.
- `DiskApp.UI`: `ISearchService` с макетом `SampleSearchService`, `IThemeService`, `IAppCommands`, `StartupArguments`, контрол `HighlightTextBlock`, дизайн-токены и стили; 99 новых тестов (ViewModel, форматирование, пути, подсветка, headless-представления).
- Один экземпляр: `Mutex` и именованный канал, повторный запуск передаёт аргументы первому экземпляру; метки времени старта и показа окна поиска в журнале; файловый sink Serilog в режиме `shared`.
- Спайки S3 (Kestrel в процессе) и S4 (FTS5 trigram, 1 млн записей) в `spikes/` с результатами; ADR 0002–0005; тестовый проект `tests/DiskApp.Desktop.Tests`.
- README: раздел «Статус» с таблицей замеров этапа 0 против бюджетов и бейджи; ARCHITECTURE: схема запуска процесса по коду, цепочка регистрации хоткея, состав оболочки и интерфейса, итоги спайков. Ветка `main` на GitHub защищена от принудительной отправки и удаления.
- `DiskApp.Core`, этап 1: модель домена (`Root`, `FileEntry`, `FileKind` с таблицей расширений и переопределениями, `Tag`, `Favorite`, `UsageStat` с frecency, `Collection`, `ExclusionRule`), `PathNormalizer` (буква диска, разделители, `..`, UNC, префикс `\\?\`), `RootValidator`, `Result`/`DomainError`, модель запроса и результатов поиска, интерфейсы репозиториев и платформенных сервисов, шина событий `IndexEventBus`, модель настроек `AppSettings`.
- `DiskApp.Infrastructure`, этап 1: SQLite — схема V001 с FTS5 trigram по имени и `name_folded`, мигратор на встроенных скриптах, фабрика соединений с PRAGMA, единственный писатель `SqliteWriter`, `SqliteDatabase` с проверкой целостности и карантином повреждённого файла, репозитории записей, корней, статистики, избранного, тегов, коллекций и истории запросов; `AppPaths` (обычный и портативный режим); `JsonSettingsStore<T>` с миграциями, атомарной записью и подписками.
- Бенчмарки `tests/DiskApp.Benchmarks`: вставка 100 тыс. записей 4,07 с (бюджет 5 с), переименование поддерева на 50 тыс. записей 184 мс (бюджет 200 мс). Тестов 370.

### Changed

- `.editorconfig`: неиспользуемые `using` и неявные модификаторы доступа — ошибки сборки; приватные статические поля — `_camelCase`; `GenerateDocumentationFile` включён для всех проектов.
- Сочетание быстрого поиска по умолчанию — `Ctrl+Alt+D` с запасными `Ctrl+Alt+F` и `Ctrl+Alt+Space` (ADR-0006); действующее сочетание показывается в настройках и на главной.
- `scripts/publish.ps1` именует сборку `diskapp_v{версия}_{стадия}[_singlefile].exe`, стадия задаётся параметром `-Stage` и попадает в версию сборки.
