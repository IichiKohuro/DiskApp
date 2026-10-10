# Спайки

Одноразовые проекты этапа 0.6 (PLAN.md): проверяют технический риск и дают цифры для ADR. В решение `DiskApp.slnx` не входят, анализаторы и `TreatWarningsAsErrors` в них отключены, код не предназначен для переиспользования. Хранятся в репозитории, чтобы замеры можно было повторить на другой машине.

| Спайк | Вопрос | Запуск | Результат |
|---|---|---|---|
| S1 — стиль Windows 11 | Mica, собственный заголовок, Acrylic, DWM-углы, тема, акцент, время показа окна | Проверен прототипом интерфейса, отдельного проекта нет | [ADR-0002](../docs/ADR/0002-windows11-shell-in-avalonia.md) |
| S2 — трей, хоткей, один экземпляр | `TrayIcon`, `RegisterHotKey`, `Mutex` + именованный канал, время старта | Проверен прототипом и `tests/DiskApp.Desktop.Tests` | [ADR-0003](../docs/ADR/0003-tray-hotkey-single-instance.md) |
| [S3.KestrelInProcess](S3.KestrelInProcess/) | Kestrel + Minimal API + SignalR в процессе: старт, память, задержки loopback, общий DI | `dotnet run -c Release` | `S3.KestrelInProcess/results.md`, [ADR-0004](../docs/ADR/0004-kestrel-in-process.md) |
| [S4.Fts5Trigram](S4.Fts5Trigram/) | FTS5 trigram на 1 млн записей: вставка, размер, поиск 2/3/5 символов, `LIKE`, кириллица | `dotnet run -c Release` (после `--` можно `rows=100000 dir=C:\tmp`) | `S4.Fts5Trigram/results.md`, [ADR-0005](../docs/ADR/0005-fts5-trigram-index-layout.md) |
| S5 (опц.) — `FileSystemWatcher` под нагрузкой | Переполнение буфера, переименование поддерева | Отложен до этапа 2.3 | — |

Файлы `results.md` перезаписываются при каждом запуске; цифры из ADR сняты 2026-10-10 на машине разработчика (Windows 11, 22 CPU, .NET 10.0.12).
