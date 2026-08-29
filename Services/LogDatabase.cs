using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Dapper;
using LogAggregator.Models;
using Microsoft.Data.Sqlite;

namespace LogAggregator.Services;

public enum SortColumn
{
    Source,
    UniversalTimestamp,
    OriginalTimestamp,
    Message
}

/// <summary>
/// SQLite-backed storage for parsed log rows. Replaces holding every LogBlock in memory - at
/// tens of millions of rows that was the entire memory problem this design exists to fix.
///
/// Writes are serialized through a semaphore because SQLite allows exactly one writer at a
/// time; parsing (the CPU-heavy part) still happens fully in parallel across files in
/// IngestionService, only the actual batched INSERT is serialized, and each batch is small
/// (~2000 rows) so contention is brief. Reads (count/page/export queries) use their own
/// connections and are not serialized - WAL mode lets readers proceed without waiting on the
/// writer.
///
/// Schema version 2 added LogTypeId/LogTypeName/LogTypeColor/LogTypeIcon columns (a row is now
/// produced by one LogType bound to one Source, not just a Source). Schema version 3 doesn't
/// change the columns themselves, but bumps anyway: LogTypeIcon's stored values changed meaning
/// from a Segoe MDL2 Assets font-glyph character to a vector icon key (see
/// Converters.IconGeometry), so existing rows' icon values are stale under the new system. This
/// is a from-scratch cutover for the app's current early-development stage: if the on-disk file
/// isn't already at the current schema version, the LogBlocks table is dropped and recreated
/// rather than migrated - existing log data is intentionally discarded (see Initialize()).
/// </summary>
public class LogDatabase
{
    private const int SchemaVersion = 3;

    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public LogDatabase(string? dbPath = null)
    {
        var path = dbPath ?? Path.Combine(AppContext.BaseDirectory, "logaggregator.db");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
    }

    public void Initialize()
    {
        using var connection = OpenConnection();
        connection.Open();

        // WAL + NORMAL synchronous: default SQLite durability (synchronous=FULL) fsyncs on
        // every transaction commit, which is very slow for bulk loads of millions of rows.
        // WAL + NORMAL is the standard, well-documented trade-off for bulk-write workloads:
        // still crash-safe against application crashes, just not against an OS-level power
        // loss mid-write - an acceptable trade for a local log viewer.
        connection.Execute("PRAGMA journal_mode=WAL;");
        connection.Execute("PRAGMA synchronous=NORMAL;");
        connection.Execute("PRAGMA busy_timeout=5000;");

        var currentVersion = connection.ExecuteScalar<long>("PRAGMA user_version;");
        if (currentVersion != SchemaVersion)
        {
            // Old (or no) schema - drop and recreate rather than migrate. LogTypes are a new
            // concept the old rows have no equivalent data for, and per the app's current
            // early-development stage, existing log data is fine to discard here.
            connection.Execute("DROP TABLE IF EXISTS LogBlocks;");
            connection.Execute($"PRAGMA user_version = {SchemaVersion};");
        }

        connection.Execute(@"
            CREATE TABLE IF NOT EXISTS LogBlocks (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SourceId TEXT NOT NULL,
                SourceName TEXT NOT NULL,
                SourceColor TEXT NOT NULL,
                LogTypeId TEXT NOT NULL,
                LogTypeName TEXT NOT NULL,
                LogTypeColor TEXT NOT NULL,
                LogTypeIcon TEXT NOT NULL,
                UniversalTimestamp TEXT NOT NULL,
                OriginalTimestamp TEXT NOT NULL,
                FullText TEXT NOT NULL,
                TimestampParseFailed INTEGER NOT NULL,
                SourceFilePath TEXT NOT NULL
            );");

        connection.Execute("CREATE INDEX IF NOT EXISTS idx_logblocks_source_ts ON LogBlocks(SourceId, UniversalTimestamp);");
        connection.Execute("CREATE INDEX IF NOT EXISTS idx_logblocks_ts ON LogBlocks(UniversalTimestamp);");
        connection.Execute("CREATE INDEX IF NOT EXISTS idx_logblocks_source_logtype ON LogBlocks(SourceId, LogTypeId);");
    }

    private SqliteConnection OpenConnection() => new(_connectionString);

    // ===================================================================
    // Writes
    // ===================================================================

    private const string InsertSql = @"
        INSERT INTO LogBlocks (SourceId, SourceName, SourceColor, LogTypeId, LogTypeName, LogTypeColor, LogTypeIcon, UniversalTimestamp, OriginalTimestamp, FullText, TimestampParseFailed, SourceFilePath)
        VALUES (@SourceId, @SourceName, @SourceColor, @LogTypeId, @LogTypeName, @LogTypeColor, @LogTypeIcon, @UniversalTimestamp, @OriginalTimestamp, @FullText, @TimestampParseFailed, @SourceFilePath);";

    /// <summary>Inserts a batch of blocks inside one transaction. Called repeatedly with small
    /// batches (~2000 rows) as ingestion parses through a file, so memory never holds more than
    /// one batch at a time regardless of how large the source file is.</summary>
    public void InsertBatch(IReadOnlyList<LogBlock> batch)
    {
        if (batch.Count == 0) return;

        _writeLock.Wait();
        try
        {
            using var connection = OpenConnection();
            connection.Open();
            using var transaction = connection.BeginTransaction();
            connection.Execute(InsertSql, batch, transaction: transaction);
            transaction.Commit();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void DeleteBlocksForSource(string sourceId)
    {
        _writeLock.Wait();
        try
        {
            using var connection = OpenConnection();
            connection.Open();
            connection.Execute("DELETE FROM LogBlocks WHERE SourceId = @sourceId;", new { sourceId });
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Deletes only the rows for one LogType binding on one source - used when a
    /// LogType is unbound/removed from a source (or re-synced) without touching that source's
    /// other LogTypes.</summary>
    public void DeleteBlocksForBinding(string sourceId, string logTypeId)
    {
        _writeLock.Wait();
        try
        {
            using var connection = OpenConnection();
            connection.Open();
            connection.Execute(
                "DELETE FROM LogBlocks WHERE SourceId = @sourceId AND LogTypeId = @logTypeId;",
                new { sourceId, logTypeId });
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Propagates a card color change onto every already-ingested row for that
    /// source, so existing rows re-tint immediately instead of only new rows picking it up.</summary>
    public void UpdateSourceColor(string sourceId, string newColorHex)
    {
        _writeLock.Wait();
        try
        {
            using var connection = OpenConnection();
            connection.Open();
            connection.Execute(
                "UPDATE LogBlocks SET SourceColor = @newColorHex WHERE SourceId = @sourceId;",
                new { sourceId, newColorHex });
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Propagates a source rename onto already-ingested rows' denormalized SourceName.</summary>
    public void UpdateSourceName(string sourceId, string newName)
    {
        _writeLock.Wait();
        try
        {
            using var connection = OpenConnection();
            connection.Open();
            connection.Execute(
                "UPDATE LogBlocks SET SourceName = @newName WHERE SourceId = @sourceId;",
                new { sourceId, newName });
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Propagates a LogType color edit (from the LogTypes window) onto every
    /// already-ingested row for that LogType, across every source it's bound to.</summary>
    public void UpdateLogTypeColor(string logTypeId, string newColorHex)
    {
        _writeLock.Wait();
        try
        {
            using var connection = OpenConnection();
            connection.Open();
            connection.Execute(
                "UPDATE LogBlocks SET LogTypeColor = @newColorHex WHERE LogTypeId = @logTypeId;",
                new { logTypeId, newColorHex });
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void UpdateLogTypeIcon(string logTypeId, string newIconGlyph)
    {
        _writeLock.Wait();
        try
        {
            using var connection = OpenConnection();
            connection.Open();
            connection.Execute(
                "UPDATE LogBlocks SET LogTypeIcon = @newIconGlyph WHERE LogTypeId = @logTypeId;",
                new { logTypeId, newIconGlyph });
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void UpdateLogTypeName(string logTypeId, string newName)
    {
        _writeLock.Wait();
        try
        {
            using var connection = OpenConnection();
            connection.Open();
            connection.Execute(
                "UPDATE LogBlocks SET LogTypeName = @newName WHERE LogTypeId = @logTypeId;",
                new { logTypeId, newName });
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ===================================================================
    // Reads
    // ===================================================================

    public long CountForSource(string sourceId)
    {
        using var connection = OpenConnection();
        connection.Open();
        return connection.ExecuteScalar<long>("SELECT COUNT(*) FROM LogBlocks WHERE SourceId = @sourceId;", new { sourceId });
    }

    public long CountForBinding(string sourceId, string logTypeId)
    {
        using var connection = OpenConnection();
        connection.Open();
        return connection.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM LogBlocks WHERE SourceId = @sourceId AND LogTypeId = @logTypeId;",
            new { sourceId, logTypeId });
    }

    public long CountMatching(IReadOnlyCollection<string> activeSourceIds, IReadOnlyList<string> orTerms, IReadOnlyList<string> andTerms, IReadOnlyList<string> exclusionTerms)
    {
        var (whereSql, parameters) = BuildFilterSql(activeSourceIds, orTerms, andTerms, exclusionTerms);
        using var connection = OpenConnection();
        connection.Open();
        return connection.ExecuteScalar<long>($"SELECT COUNT(*) FROM LogBlocks WHERE {whereSql};", parameters);
    }

    public List<LogBlock> QueryPage(
        IReadOnlyCollection<string> activeSourceIds,
        IReadOnlyList<string> orTerms, IReadOnlyList<string> andTerms, IReadOnlyList<string> exclusionTerms,
        SortColumn sortColumn, bool descending, int offset, int limit)
    {
        var (whereSql, parameters) = BuildFilterSql(activeSourceIds, orTerms, andTerms, exclusionTerms);
        parameters.Add("offset", offset);
        parameters.Add("limit", limit);

        var orderSql = $"{ColumnSql(sortColumn)} {(descending ? "DESC" : "ASC")}";
        var sql = $@"
            SELECT Id, SourceId, SourceName, SourceColor, LogTypeId, LogTypeName, LogTypeColor, LogTypeIcon, UniversalTimestamp, OriginalTimestamp, FullText, TimestampParseFailed, SourceFilePath
            FROM LogBlocks
            WHERE {whereSql}
            ORDER BY {orderSql}
            LIMIT @limit OFFSET @offset;";

        using var connection = OpenConnection();
        connection.Open();
        return connection.Query<LogBlock>(sql, parameters).AsList();
    }

    /// <summary>Unbuffered (streaming) query used for export - never materializes the full
    /// result set in memory, even for tens of millions of matching rows. Written as an
    /// iterator method so the connection is correctly disposed whether the caller enumerates
    /// to completion, breaks out early, or an exception is thrown partway through - a `using`
    /// around a plain returned IEnumerable would only get disposed on full completion.</summary>
    public IEnumerable<LogBlock> QueryAllMatchingUnbuffered(
        IReadOnlyCollection<string> activeSourceIds,
        IReadOnlyList<string> orTerms, IReadOnlyList<string> andTerms, IReadOnlyList<string> exclusionTerms,
        SortColumn sortColumn, bool descending)
    {
        var (whereSql, parameters) = BuildFilterSql(activeSourceIds, orTerms, andTerms, exclusionTerms);
        var orderSql = $"{ColumnSql(sortColumn)} {(descending ? "DESC" : "ASC")}";
        var sql = $@"
            SELECT Id, SourceId, SourceName, SourceColor, LogTypeId, LogTypeName, LogTypeColor, LogTypeIcon, UniversalTimestamp, OriginalTimestamp, FullText, TimestampParseFailed, SourceFilePath
            FROM LogBlocks
            WHERE {whereSql}
            ORDER BY {orderSql};";

        using var connection = OpenConnection();
        connection.Open();
        foreach (var block in connection.Query<LogBlock>(sql, parameters, buffered: false))
        {
            yield return block;
        }
    }

    private static string ColumnSql(SortColumn column) => column switch
    {
        SortColumn.Source => "SourceName",
        SortColumn.OriginalTimestamp => "OriginalTimestamp",
        SortColumn.Message => "FullText",
        _ => "UniversalTimestamp"
    };

    /// <summary>
    /// Builds the WHERE clause implementing Block_Passes = (OR_match || AND_match) &amp;&amp; !Exclusion_match,
    /// exactly matching the in-memory FilterService semantics this replaces: OR is vacuously
    /// false with zero terms, AND is vacuously true with zero terms (so "no filters" shows
    /// everything), and an exclusion term is ignored if it also appears in the OR or AND list.
    /// </summary>
    private static (string Sql, DynamicParameters Parameters) BuildFilterSql(
        IReadOnlyCollection<string> activeSourceIds,
        IReadOnlyList<string> orTerms, IReadOnlyList<string> andTerms, IReadOnlyList<string> exclusionTerms)
    {
        var parameters = new DynamicParameters();
        var clauses = new List<string>();

        // Always scope to active (checked) sources. An empty active list means "show nothing"
        // rather than "show everything" - the caller (MainViewModel) is expected to pass all
        // source ids when none are actively deselected.
        parameters.Add("activeSourceIds", activeSourceIds.ToList());
        clauses.Add("SourceId IN @activeSourceIds");

        string orSql = "0";
        if (orTerms.Count > 0)
        {
            var orParts = new List<string>();
            for (int i = 0; i < orTerms.Count; i++)
            {
                var name = $"or{i}";
                parameters.Add(name, "%" + EscapeLikeTerm(orTerms[i]) + "%");
                orParts.Add($"FullText LIKE @{name} ESCAPE '\\'");
            }
            orSql = "(" + string.Join(" OR ", orParts) + ")";
        }

        string andSql = "1";
        if (andTerms.Count > 0)
        {
            var andParts = new List<string>();
            for (int i = 0; i < andTerms.Count; i++)
            {
                var name = $"and{i}";
                parameters.Add(name, "%" + EscapeLikeTerm(andTerms[i]) + "%");
                andParts.Add($"FullText LIKE @{name} ESCAPE '\\'");
            }
            andSql = "(" + string.Join(" AND ", andParts) + ")";
        }

        clauses.Add($"({orSql} OR {andSql})");

        // Exclusion override: a term also present in OR/AND is neutralized (doesn't veto).
        var effectiveExclusions = exclusionTerms
            .Where(t => !orTerms.Contains(t, StringComparer.OrdinalIgnoreCase) &&
                        !andTerms.Contains(t, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (effectiveExclusions.Count > 0)
        {
            var exclParts = new List<string>();
            for (int i = 0; i < effectiveExclusions.Count; i++)
            {
                var name = $"excl{i}";
                parameters.Add(name, "%" + EscapeLikeTerm(effectiveExclusions[i]) + "%");
                exclParts.Add($"FullText LIKE @{name} ESCAPE '\\'");
            }
            clauses.Add("NOT (" + string.Join(" OR ", exclParts) + ")");
        }

        return (string.Join(" AND ", clauses), parameters);
    }

    private static string EscapeLikeTerm(string term) =>
        term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
