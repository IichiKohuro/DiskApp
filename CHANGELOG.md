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

### Changed

- `.editorconfig`: неиспользуемые `using` и неявные модификаторы доступа — ошибки сборки; приватные статические поля — `_camelCase`; `GenerateDocumentationFile` включён для всех проектов.
