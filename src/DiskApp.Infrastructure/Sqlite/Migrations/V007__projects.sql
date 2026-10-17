-- V007: проекты (PLAN.md, этап 24, ADR-0017). Номер — свой («417»), уникален без учёта регистра (number_folded);
-- одна основная рабочая папка по полному пути (folder_path) — как авто-теги папок, путь переживает удаление записи
-- индекса и следует за переименованием папки. Заметка привязывается к проекту колонкой notes.project_id; удаление
-- проекта заметки не удаляет.

CREATE TABLE projects (
    id            INTEGER PRIMARY KEY,
    number        TEXT    NOT NULL,
    number_folded TEXT    NOT NULL UNIQUE,
    title         TEXT    NOT NULL,
    color         TEXT    NULL,
    folder_path   TEXT    NULL,
    created_utc   INTEGER NOT NULL,
    modified_utc  INTEGER NOT NULL
);

ALTER TABLE notes ADD COLUMN project_id INTEGER NULL REFERENCES projects(id) ON DELETE SET NULL;

CREATE INDEX ix_notes_project ON notes (project_id) WHERE project_id IS NOT NULL;
