using System.Globalization;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;

using DiskApp.Core.Abstractions;
using DiskApp.Core.Domain;
using DiskApp.Infrastructure.Sqlite;
using DiskApp.Infrastructure.Sqlite.Repositories;

namespace DiskApp.Benchmarks.Sqlite;

/// <summary>
/// Бюджеты этапа 1.4 (PLAN.md): вставка 100 тыс. записей ≤ 5 с, переименование поддерева на 50 тыс. записей ≤ 200 мс.
/// Каждая итерация работает со свежей базой во временной папке; результаты — в docs/ARCHITECTURE.md, раздел 16.
/// </summary>
[SimpleJob(RunStrategy.Monitoring, launchCount: 1, warmupCount: 1, iterationCount: 5, invocationCount: 1)]
[MemoryDiagnoser]
public class FileIndexBenchmarks : IDisposable
{
    private const int InsertCount = 100_000;
    private const int RenameCount = 50_000;
    private const int BatchSize = 5_000;

    private string _folder = string.Empty;
    private SqliteDatabase? _database;
    private SqliteFileIndexRepository? _repository;
    private long _rootId;
    private FileEntry[] _insertEntries = [];
    private FileEntry[] _renameEntries = [];
    private bool _renamedForward;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _folder = Path.Combine(Path.GetTempPath(), "DiskApp.Benchmarks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        DisposeDatabase();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [IterationSetup(Target = nameof(Insert100k))]
    public void SetupInsert()
    {
        CreateDatabase();
        _insertEntries = GenerateTree("data", InsertCount);
    }

    [Benchmark(Description = "Вставка 100 тыс. записей пакетами по 5 тыс.")]
    public int Insert100k()
    {
        var inserted = 0;
        foreach (var batch in _insertEntries.Chunk(BatchSize))
        {
            inserted += _repository!.UpsertAsync(batch, 1).GetAwaiter().GetResult().Inserted;
        }

        return inserted;
    }

    [IterationSetup(Target = nameof(RenameSubtree50k))]
    public void SetupRename()
    {
        if (_database is null)
        {
            CreateDatabase();
            _renameEntries = GenerateTree("tree", RenameCount);
            foreach (var batch in _renameEntries.Chunk(BatchSize))
            {
                _repository!.UpsertAsync(batch, 1).GetAwaiter().GetResult();
            }

            _renamedForward = false;
        }
    }

    [Benchmark(Description = "Переименование поддерева на 50 тыс. записей")]
    public int RenameSubtree50k()
    {
        var (from, to) = _renamedForward ? ("tree2", "tree") : ("tree", "tree2");
        _renamedForward = !_renamedForward;
        return _repository!.RenameSubtreeAsync(_rootId, from, to).GetAwaiter().GetResult();
    }

    [IterationCleanup(Target = nameof(Insert100k))]
    public void CleanupInsert() => DisposeDatabase();

    /// <summary>
    /// Закрывает базу, если BenchmarkDotNet не дошёл до <see cref="GlobalCleanup"/>.
    /// </summary>
    public void Dispose()
    {
        DisposeDatabase();
        GC.SuppressFinalize(this);
    }

    private void CreateDatabase()
    {
        DisposeDatabase();
        var path = Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".db");
        _database = new SqliteDatabase(path);
        _database.InitializeAsync().GetAwaiter().GetResult();
        _repository = new SqliteFileIndexRepository(_database);
        var roots = new SqliteRootRepository(_database, SystemClock.Instance);
        _rootId = roots.AddAsync(new Root { Path = OperatingSystem.IsWindows() ? @"D:\Bench" : "/bench", Label = "Bench" }).GetAwaiter().GetResult().Id;
    }

    private void DisposeDatabase()
    {
        _database?.Dispose();
        _database = null;
        _repository = null;
    }

    /// <summary>
    /// Дерево: папка верхнего уровня, внутри по 20 подпапок с подпапками, файлы с разными расширениями; папки идут раньше детей.
    /// </summary>
    private FileEntry[] GenerateTree(string topFolder, int count)
    {
        var now = DateTimeOffset.UtcNow;
        var entries = new List<FileEntry>(count);
        string[] extensions = ["txt", "pdf", "jpg", "docx", "cs", "zip", "mp4", "xlsx"];
        var separator = Path.DirectorySeparatorChar;

        entries.Add(Make(topFolder, true, 0, now));
        var folders = new List<string>();
        for (var i = 0; i < 20 && entries.Count < count; i++)
        {
            var folder = $"{topFolder}{separator}folder{i:00}";
            entries.Add(Make(folder, true, 0, now));
            for (var j = 0; j < 10 && entries.Count < count; j++)
            {
                var sub = $"{folder}{separator}sub{j:00}";
                entries.Add(Make(sub, true, 0, now));
                folders.Add(sub);
            }
        }

        var file = 0;
        while (entries.Count < count)
        {
            var folder = folders[file % folders.Count];
            var extension = extensions[file % extensions.Length];
            entries.Add(Make($"{folder}{separator}file{file.ToString("D6", CultureInfo.InvariantCulture)}.{extension}", false, 1024 + file, now.AddSeconds(-file)));
            file++;
        }

        return [.. entries];

        FileEntry Make(string relPath, bool isDirectory, long size, DateTimeOffset modified)
            => FileEntry.Create(_rootId, null, relPath, isDirectory, size, modified, modified, isDirectory ? FileAttributes.Directory : FileAttributes.Normal);
    }
}
