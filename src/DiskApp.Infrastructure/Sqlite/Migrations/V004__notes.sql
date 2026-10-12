-- V004: заметки (PLAN.md, этап 23, ADR-0014). Текст — Markdown; заголовок — его первая строка, хранится для списка.
-- search_folded — заголовок и текст в нижнем регистре с «ё» → «е» (SearchText.Fold): поиск идёт instr по нему — заметок
-- сотни, полнотекстовый индекс не нужен, а 1–2 символа тоже находятся. Удалённая заметка лежит в корзине (deleted_utc).
-- Теги заметок — свой список, отдельно от tags и entry_tags.

CREATE TABLE notes (
    id            INTEGER PRIMARY KEY,
    title         TEXT    NOT NULL,
    body          TEXT    NOT NULL,
    search_folded TEXT    NOT NULL,
    is_pinned     INTEGER NOT NULL DEFAULT 0,
    created_utc   INTEGER NOT NULL,
    modified_utc  INTEGER NOT NULL,
    deleted_utc   INTEGER NULL
);

CREATE INDEX ix_notes_listing ON notes (deleted_utc, is_pinned, modified_utc);

CREATE TABLE note_tags (
    id          INTEGER PRIMARY KEY,
    name        TEXT NOT NULL,
    name_folded TEXT NOT NULL UNIQUE,
    color       TEXT NULL
);

CREATE TABLE note_tag_links (
    note_id INTEGER NOT NULL REFERENCES notes(id) ON DELETE CASCADE,
    tag_id  INTEGER NOT NULL REFERENCES note_tags(id) ON DELETE CASCADE,
    PRIMARY KEY (note_id, tag_id)
);

CREATE INDEX ix_note_tag_links_tag ON note_tag_links (tag_id);
