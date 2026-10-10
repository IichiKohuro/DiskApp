-- V001: начальная схема индекса (docs/ARCHITECTURE.md, раздел 4; ADR-0005).
-- Даты — текст ISO 8601 в UTC (yyyy-MM-ddTHH:mm:ss.fffffffZ): сравнение строк совпадает с хронологическим.
-- name_folded — имя в нижнем регистре по правилам .NET: COLLATE NOCASE в SQLite не сворачивает кириллицу.

CREATE TABLE roots (
    id             INTEGER PRIMARY KEY,
    path           TEXT    NOT NULL UNIQUE,
    label          TEXT    NOT NULL,
    is_enabled     INTEGER NOT NULL DEFAULT 1,
    include_hidden INTEGER NOT NULL DEFAULT 0,
    include_system INTEGER NOT NULL DEFAULT 0,
    max_depth      INTEGER NULL,
    last_scan_utc  TEXT    NULL,
    status         INTEGER NOT NULL DEFAULT 0,
    error_message  TEXT    NULL,
    created_utc    TEXT    NOT NULL
);

CREATE TABLE entries (
    id           INTEGER PRIMARY KEY,
    root_id      INTEGER NOT NULL REFERENCES roots(id) ON DELETE CASCADE,
    parent_id    INTEGER NULL REFERENCES entries(id) ON DELETE CASCADE,
    name         TEXT    NOT NULL,
    name_folded  TEXT    NOT NULL,
    ext          TEXT    NOT NULL DEFAULT '',
    is_dir       INTEGER NOT NULL DEFAULT 0,
    size         INTEGER NOT NULL DEFAULT 0,
    created_utc  TEXT    NOT NULL,
    modified_utc TEXT    NOT NULL,
    attributes   INTEGER NOT NULL DEFAULT 0,
    rel_path     TEXT    NOT NULL,
    kind         INTEGER NOT NULL DEFAULT 0,
    scan_id      INTEGER NOT NULL DEFAULT 0,
    UNIQUE (root_id, rel_path)
);

CREATE INDEX ix_entries_parent      ON entries (root_id, parent_id);
CREATE INDEX ix_entries_name_folded ON entries (name_folded);
CREATE INDEX ix_entries_ext         ON entries (ext);
CREATE INDEX ix_entries_modified    ON entries (modified_utc);
CREATE INDEX ix_entries_size        ON entries (size);
CREATE INDEX ix_entries_kind        ON entries (kind);
CREATE INDEX ix_entries_scan        ON entries (root_id, scan_id);

-- FTS5 trigram только по имени (ADR-0005): external content на entries, синхронизация триггерами.
CREATE VIRTUAL TABLE entries_fts USING fts5(
    name,
    content = 'entries',
    content_rowid = 'id',
    tokenize = 'trigram'
);

CREATE TRIGGER entries_fts_ai AFTER INSERT ON entries BEGIN
    INSERT INTO entries_fts (rowid, name) VALUES (new.id, new.name);
END;

CREATE TRIGGER entries_fts_ad AFTER DELETE ON entries BEGIN
    INSERT INTO entries_fts (entries_fts, rowid, name) VALUES ('delete', old.id, old.name);
END;

CREATE TRIGGER entries_fts_au AFTER UPDATE OF name ON entries BEGIN
    INSERT INTO entries_fts (entries_fts, rowid, name) VALUES ('delete', old.id, old.name);
    INSERT INTO entries_fts (rowid, name) VALUES (new.id, new.name);
END;

CREATE TABLE usage (
    entry_id        INTEGER PRIMARY KEY REFERENCES entries(id) ON DELETE CASCADE,
    open_count      INTEGER NOT NULL DEFAULT 0,
    last_opened_utc TEXT    NULL
);

CREATE INDEX ix_usage_last_opened ON usage (last_opened_utc);

CREATE TABLE favorites (
    entry_id   INTEGER PRIMARY KEY REFERENCES entries(id) ON DELETE CASCADE,
    sort_order INTEGER NOT NULL,
    pinned_utc TEXT    NOT NULL
);

CREATE TABLE tags (
    id          INTEGER PRIMARY KEY,
    name        TEXT    NOT NULL,
    name_folded TEXT    NOT NULL UNIQUE,
    color       TEXT    NULL
);

CREATE TABLE entry_tags (
    entry_id INTEGER NOT NULL REFERENCES entries(id) ON DELETE CASCADE,
    tag_id   INTEGER NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
    PRIMARY KEY (entry_id, tag_id)
);

CREATE INDEX ix_entry_tags_tag ON entry_tags (tag_id);

CREATE TABLE collections (
    id          INTEGER PRIMARY KEY,
    name        TEXT    NOT NULL,
    kind        INTEGER NOT NULL DEFAULT 0,
    query       TEXT    NULL,
    created_utc TEXT    NOT NULL
);

CREATE TABLE collection_items (
    collection_id INTEGER NOT NULL REFERENCES collections(id) ON DELETE CASCADE,
    entry_id      INTEGER NOT NULL REFERENCES entries(id) ON DELETE CASCADE,
    sort_order    INTEGER NOT NULL,
    PRIMARY KEY (collection_id, entry_id)
);

CREATE TABLE query_history (
    query         TEXT    PRIMARY KEY,
    use_count     INTEGER NOT NULL DEFAULT 1,
    last_used_utc TEXT    NOT NULL
);

CREATE INDEX ix_query_history_last_used ON query_history (last_used_utc);

CREATE TABLE meta (
    key   TEXT PRIMARY KEY,
    value TEXT NULL
);

CREATE TABLE scan_log (
    id           INTEGER PRIMARY KEY,
    root_id      INTEGER NOT NULL REFERENCES roots(id) ON DELETE CASCADE,
    scan_id      INTEGER NOT NULL,
    occurred_utc TEXT    NOT NULL,
    level        INTEGER NOT NULL DEFAULT 0,
    path         TEXT    NULL,
    message      TEXT    NOT NULL
);

CREATE INDEX ix_scan_log_root ON scan_log (root_id, scan_id);
