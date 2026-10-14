-- V006: история файлов (PLAN.md, этап 20; решение 25, ADR-0016). Событие — что случилось (kind), кто сделал (source),
-- где файл после события и где был до него (полные пути), папка или файл, размер, операция DiskApp и подробности по виду.
-- path_key и old_path_key — пути для сравнения (в Windows без учёта регистра), names_folded — свёрнутые имя и прежнее
-- имя для поиска по имени. Содержимое файлов не хранится. Срок хранения и предел размера — настройки history, очищает
-- служба истории.

CREATE TABLE file_events (
    id           INTEGER PRIMARY KEY,
    time_utc     INTEGER NOT NULL,
    kind         INTEGER NOT NULL,
    source       INTEGER NOT NULL,
    path         TEXT    NOT NULL,
    old_path     TEXT    NULL,
    path_key     TEXT    NOT NULL,
    old_path_key TEXT    NULL,
    names_folded TEXT    NOT NULL,
    is_dir       INTEGER NOT NULL,
    size         INTEGER NOT NULL,
    operation_id TEXT    NULL,
    details      TEXT    NULL
);

CREATE INDEX ix_file_events_time ON file_events (time_utc);
CREATE INDEX ix_file_events_path ON file_events (path_key);
CREATE INDEX ix_file_events_old_path ON file_events (old_path_key) WHERE old_path_key IS NOT NULL;
