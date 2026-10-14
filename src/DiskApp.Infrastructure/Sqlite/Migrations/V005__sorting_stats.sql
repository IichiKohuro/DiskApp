-- V005: история решений разбора для подсказок рабочей папки (PLAN.md, этап 17.4; решение 18, ADR-0012). Строка — сколько
-- файлов с признаком имени (слово «w:акт», расширение «e:pdf», накопитель «i:путь») отправлено в папку: полный
-- нормализованный путь; пустой путь — «оставить здесь». score — вес с затуханием на момент updated_utc (полураспад 90 дней,
-- пересчитывает служба), count — сколько решений всего. Пути следуют за переименованием папок (SortingFolderTracker).

CREATE TABLE sorting_stats (
    feature     TEXT    NOT NULL,
    destination TEXT    NOT NULL,
    score       REAL    NOT NULL,
    count       INTEGER NOT NULL,
    updated_utc INTEGER NOT NULL,
    PRIMARY KEY (feature, destination)
) WITHOUT ROWID;
