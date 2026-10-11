-- V002: авто-теги папок (PLAN.md, этап 19, ADR-0012). Тег принадлежит папке и действует на всё внутри неё — текущее и
-- новое; поэтому строка одна на папку и тег, а не на каждый файл. Папка хранится полным нормализованным путём, а не
-- записью индекса: авто-теги переживают пересборку индекса (она удаляет записи) и удаление и возврат корня. Путь следует
-- за переименованием папки (SortingFolderTracker); удаление тега убирает его и отсюда.

CREATE TABLE folder_tags (
    path   TEXT    NOT NULL,
    tag_id INTEGER NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
    PRIMARY KEY (path, tag_id)
);

CREATE INDEX ix_folder_tags_tag ON folder_tags (tag_id);
