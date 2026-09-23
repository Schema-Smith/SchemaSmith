// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

#nullable enable
using System;
using System.Data;
using System.Linq;
using Schema.DataAccess;
using Schema.Domain;
using System.Threading;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// Shared base for specialized table quench tests providing common setup and helper methods.
/// Engine-agnostic across the MySQL/MariaDB family; concrete per-engine subclasses supply the
/// <see cref="Platform"/>, connection string, and main database via the abstract members below.
/// Follows the pattern established in SQL Server and PostgreSQL reference implementations.
/// </summary>
public abstract class BaseTableQuenchTests
{
    protected abstract Platform Platform { get; }
    protected abstract string MainConnectionString { get; }
    protected abstract string MainDbName { get; }

    protected readonly string _connectionString;
    protected readonly string _mainDb;
    protected readonly string _productName = "Quench Table Tests";

    protected BaseTableQuenchTests()
    {
        _connectionString = MainConnectionString;
        _mainDb = MainDbName;
    }

    /// <summary>
    /// Runs the TableQuench procedure with the given JSON table definitions.
    /// Handles deadlock retry logic for parallel test execution.
    /// </summary>
    /// <param name="cmd">The database command to use</param>
    /// <param name="json">JSON array of table definitions</param>
    /// <param name="indexOnly">If true, runs IndexOnlyQuench instead of full TableQuench</param>
    protected void RunTableQuenchProc(IDbCommand cmd, string json, bool indexOnly = false, bool dropTablesRemovedFromProduct = false, bool whatIf = false, string productName = "")
    {
        var prod = string.IsNullOrEmpty(productName) ? _productName : productName;
        cmd.CommandTimeout = 300;
        var escapedJson = json.Replace("'", "''");

        cmd.CommandText = indexOnly
            ? $"CALL SchemaSmith_IndexOnlyQuench('{prod}', '{_mainDb}', 0, 1, 1); CALL SchemaSmith_FixupIndexOwnership('{prod}');"
            : $"CALL SchemaSmith_TableQuench('{prod}', '{_mainDb}', '{escapedJson}', {(whatIf ? 1 : 0)}, 0, {(dropTablesRemovedFromProduct ? 1 : 0)});";

        // For index only mode, we need to first parse the table JSON
        if (indexOnly)
        {
            cmd.CommandText = $"CALL SchemaSmith_ParseTableJson('{_mainDb}', '{escapedJson}'); " +
                             $"CALL SchemaSmith_IndexOnlyQuench('{_productName}', '{_mainDb}', 0, 1, 1);";
        }

        // Was an inline copy of the same loop, narrower than it needed to be: it knew about deadlock and
        // lock-wait timeout and not about the concurrent-DDL skip (1684), which is how
        // RemovedFromProduct_DropsByDefault_WithUnknownIndexesOff failed a full run at two workers.
        RetryingTransientConcurrency(() => cmd.ExecuteNonQuery());
    }

    /// <summary>
    /// Runs the TableQuench procedure using the simplified three-call pattern.
    /// </summary>
    protected void RunTableQuenchSteps(IDbCommand cmd, string json)
    {
        cmd.CommandTimeout = 300;
        var escapedJson = json.Replace("'", "''");

        // Step 1: Parse JSON into temp tables
        cmd.CommandText = $"CALL SchemaSmith_ParseTableJson('{_mainDb}', '{escapedJson}')";
        ExecuteWithDeadlockRetry(cmd);

        // Step 2: Create missing tables and columns
        cmd.CommandText = $"CALL SchemaSmith_MissingTableAndColumnQuench('{_mainDb}', 0)";
        ExecuteWithDeadlockRetry(cmd);

        // Step 3: Modify existing tables. Index REMOVAL (out-of-band + removed-from-product) is
        // performed here, not in step 4 — the trailing 1, 1 are DropUnknownIndexes and
        // DropIndexesRemovedFromProduct, which used to be passed to
        // SchemaSmith_MissingIndexesAndConstraintsQuench below.
        cmd.CommandText = $"CALL SchemaSmith_ModifiedTableQuench('{_productName}', '{_mainDb}', 0, 0, 1, 1, 1, 1, 0, 1, 1)";
        ExecuteWithDeadlockRetry(cmd);

        // Step 4: Create missing indexes and constraints (add-only for indexes; still drops check
        // constraints removed from the product — the trailing 1).
        cmd.CommandText = $"CALL SchemaSmith_MissingIndexesAndConstraintsQuench('{_productName}', '{_mainDb}', 0, 1)";
        ExecuteWithDeadlockRetry(cmd);
    }

    private void ExecuteWithDeadlockRetry(IDbCommand cmd) =>
        RetryingTransientConcurrency(() => cmd.ExecuteNonQuery());

    /// <summary>
    /// The transient failures two test workers provoke against one shared database. Deadlock and
    /// lock-wait timeout were always here; the other two were found by the full suite at two workers and
    /// are the same family — a catalog read or a temp-table operation caught mid-DDL by a sibling.
    /// <para>These are the test-side mirror of <c>DeadlockClassifier.IsRetryableContention</c>, which is
    /// what the PRODUCT retries. Matched on message text rather than the typed code because these helpers
    /// take an <see cref="IDbCommand"/> and stay engine-agnostic; the product, which knows its client,
    /// matches the codes.</para>
    /// </summary>
    protected static bool IsTransientConcurrency(Exception e) =>
        e.Message.ContainsIgnoringCase("Deadlock found when trying to get lock")
        || e.Message.ContainsIgnoringCase("Lock wait timeout exceeded")
        || e.Message.ContainsIgnoringCase("is being modified by concurrent DDL statement")   // 1684
        || e.Message.ContainsIgnoringCase("Failed to read auto-increment value");            // 1467

    /// <summary>
    /// Run <paramref name="action"/>, retrying only transient concurrency. Anything else is rethrown
    /// immediately: a retry loop that swallows real failures turns one clear error into ten slow ones.
    /// </summary>
    protected static void RetryingTransientConcurrency(Action action)
    {
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception e) when (attempt < 10 && IsTransientConcurrency(e))
            {
                Thread.Sleep(1000);
            }
        }
    }

    /// <summary>
    /// Gets the data type string for a column in a table.
    /// </summary>
    protected static string GetColumnDataType(IDbCommand cmd, string tableName, string columnName)
    {
        cmd.CommandText = $@"
SELECT UPPER(COLUMN_TYPE)
  FROM INFORMATION_SCHEMA.COLUMNS
 WHERE TABLE_SCHEMA = DATABASE()
   AND TABLE_NAME = '{tableName}'
   AND COLUMN_NAME = '{columnName}'";
        return StripIntDisplayWidth(cmd.ExecuteScalar()?.ToString()?.ToUpper()) ?? "UNKNOWN";
    }

    /// <summary>
    /// Gets the data type string for a column including schema (database) context.
    /// </summary>
    protected static string GetColumnDataType(IDbCommand cmd, string schemaName, string tableName, string columnName)
    {
        cmd.CommandText = $@"
SELECT UPPER(COLUMN_TYPE)
  FROM INFORMATION_SCHEMA.COLUMNS
 WHERE TABLE_SCHEMA = '{schemaName}'
   AND TABLE_NAME = '{tableName}'
   AND COLUMN_NAME = '{columnName}'";
        return StripIntDisplayWidth(cmd.ExecuteScalar()?.ToString()?.ToUpper()) ?? "UNKNOWN";
    }

    /// <summary>
    /// Whether the connected target honors DESC index key parts (reflected as COLLATION='D' in
    /// INFORMATION_SCHEMA.STATISTICS). MySQL stores them since 8.0 (5.7 parses-and-ignores → COLLATION='A');
    /// MariaDB ignored the DESC keyword until 10.8, so a MariaDB 10.2-10.7 target reports COLLATION='A'.
    /// </summary>
    protected bool TargetSupportsDescendingIndexes(IDbCommand cmd)
    {
        cmd.CommandText = "SELECT VERSION()";
        var version = cmd.ExecuteScalar()?.ToString() ?? "";
        var parts = version.Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor))
            return true;
        // MariaDB: 10.8+. MySQL: 8.0+ (VERSION() carries the "-MariaDB" suffix on the patch part only).
        return version.Contains("MariaDB")
            ? major > 10 || (major == 10 && minor >= 8)
            : major >= 8;
    }

    /// <summary>Detected MySQL-family comparable (major*100+minor, e.g. 507, 800, 1002, 1104) of the configured
    /// target — for gating feature-gap tests below a floor. Returns int.MaxValue if unparseable (treat as modern).</summary>
    protected int MySqlServerVersion()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT VERSION()";
        var version = cmd.ExecuteScalar()?.ToString() ?? "";
        var parts = version.Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor))
            return int.MaxValue;
        return major * 100 + minor;
    }

    /// <summary>Whether the target enforces CHECK constraints + exposes INFORMATION_SCHEMA.CHECK_CONSTRAINTS.
    /// MySQL: 8.0.16 (major >= 8 here). MariaDB (>= 10.2 floor), SQL Server, PostgreSQL: always.</summary>
    protected bool TargetSupportsCheckConstraints()
        => Platform != Platform.MySQL || MySqlServerVersion() >= 800;

    /// <summary>Whether SchemaSmith delivers table data on this target. Requires MySQL 8.0 (5.7 lacks JSON_TABLE
    /// AND a recursive-CTE fallback, so delivery is gated); MariaDB 10.2 works via recursive CTE; SS/PG always.</summary>
    protected bool TargetSupportsDataDelivery()
        => Platform != Platform.MySQL || MySqlServerVersion() >= 800;

    /// <summary>Whether the target supports invisible/ignored indexes. MySQL 8.0 (IS_VISIBLE); MariaDB 10.6
    /// (IGNORED). SQL Server / PostgreSQL: not applicable — these gates only fire on the MySQL family.</summary>
    protected bool TargetSupportsInvisibleIndexes()
        => Platform switch
        {
            Platform.MySQL => MySqlServerVersion() >= 800,
            Platform.MariaDb => MySqlServerVersion() >= 1006,
            _ => true
        };

    /// <summary>
    /// Strips the deprecated integer/YEAR display width so a column's reported type is engine-neutral:
    /// MariaDB reports `BIGINT(20)` / `INT(10) UNSIGNED` / `YEAR(4)` where MySQL 8.0.19+ reports
    /// `BIGINT` / `INT UNSIGNED` / `YEAR`. Mirrors the engine's SchemaSmith_StripIntDisplayWidth so
    /// test reads compare the same canonical form on both engines. Only a pure display width on an
    /// integer/YEAR keyword is stripped; DECIMAL(p,s), VARCHAR(n), BIT(n), ENUM(...) keep their parens.
    /// </summary>
    private static string? StripIntDisplayWidth(string? dataType)
    {
        if (dataType == null) return null;
        var paren = dataType.IndexOf('(');
        if (paren < 0) return dataType;
        var keyword = dataType[..paren].Trim().ToUpperInvariant();
        if (keyword is not ("TINYINT" or "SMALLINT" or "MEDIUMINT" or "INT" or "INTEGER" or "BIGINT" or "YEAR"))
            return dataType;
        var close = dataType.IndexOf(')', paren);
        if (close < 0) return dataType;
        var inside = dataType.Substring(paren + 1, close - paren - 1);
        if (inside.Length == 0 || !inside.All(char.IsDigit)) return dataType;
        return dataType[..paren] + dataType[(close + 1)..];
    }

    /// <summary>
    /// Checks if a column exists in a table.
    /// </summary>
    protected static bool ColumnExists(IDbCommand cmd, string tableName, string columnName)
    {
        cmd.CommandText = $@"
SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
 WHERE TABLE_SCHEMA = DATABASE()
   AND TABLE_NAME = '{tableName}'
   AND COLUMN_NAME = '{columnName}'";
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    /// <summary>
    /// Checks if an index exists on a table.
    /// </summary>
    protected static bool IndexExists(IDbCommand cmd, string tableName, string indexName)
    {
        cmd.CommandText = $@"
SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS
 WHERE TABLE_SCHEMA = DATABASE()
   AND TABLE_NAME = '{tableName}'
   AND INDEX_NAME = '{indexName}'";
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    /// <summary>
    /// Checks if a foreign key constraint exists.
    /// </summary>
    protected static bool ForeignKeyExists(IDbCommand cmd, string tableName, string constraintName)
    {
        cmd.CommandText = $@"
SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS
 WHERE TABLE_SCHEMA = DATABASE()
   AND TABLE_NAME = '{tableName}'
   AND CONSTRAINT_NAME = '{constraintName}'
   AND CONSTRAINT_TYPE = 'FOREIGN KEY'";
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    /// <summary>
    /// Checks if a check constraint exists.
    /// </summary>
    protected static bool CheckConstraintExists(IDbCommand cmd, string tableName, string constraintName)
    {
        cmd.CommandText = $@"
SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS
 WHERE TABLE_SCHEMA = DATABASE()
   AND TABLE_NAME = '{tableName}'
   AND CONSTRAINT_NAME = '{constraintName}'
   AND CONSTRAINT_TYPE = 'CHECK'";
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    /// <summary>
    /// Gets the column default value.
    /// </summary>
    protected static string? GetColumnDefault(IDbCommand cmd, string tableName, string columnName)
    {
        cmd.CommandText = $@"
SELECT COLUMN_DEFAULT FROM INFORMATION_SCHEMA.COLUMNS
 WHERE TABLE_SCHEMA = DATABASE()
   AND TABLE_NAME = '{tableName}'
   AND COLUMN_NAME = '{columnName}'";
        return cmd.ExecuteScalar()?.ToString();
    }

    /// <summary>
    /// Checks if a column is nullable.
    /// </summary>
    protected static bool IsColumnNullable(IDbCommand cmd, string tableName, string columnName)
    {
        cmd.CommandText = $@"
SELECT IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS
 WHERE TABLE_SCHEMA = DATABASE()
   AND TABLE_NAME = '{tableName}'
   AND COLUMN_NAME = '{columnName}'";
        return cmd.ExecuteScalar()?.ToString() == "YES";
    }

    /// <summary>
    /// Checks if a column is a generated column.
    /// </summary>
    protected static bool IsGeneratedColumn(IDbCommand cmd, string tableName, string columnName)
    {
        cmd.CommandText = $@"
SELECT EXTRA FROM INFORMATION_SCHEMA.COLUMNS
 WHERE TABLE_SCHEMA = DATABASE()
   AND TABLE_NAME = '{tableName}'
   AND COLUMN_NAME = '{columnName}'";
        var extra = cmd.ExecuteScalar()?.ToString() ?? "";
        return extra.Contains("GENERATED");
    }

    /// <summary>
    /// Gets the generation expression for a generated column.
    /// </summary>
    protected static string? GetGenerationExpression(IDbCommand cmd, string tableName, string columnName)
    {
        cmd.CommandText = $@"
SELECT GENERATION_EXPRESSION FROM INFORMATION_SCHEMA.COLUMNS
 WHERE TABLE_SCHEMA = DATABASE()
   AND TABLE_NAME = '{tableName}'
   AND COLUMN_NAME = '{columnName}'";
        return cmd.ExecuteScalar()?.ToString();
    }

    /// <summary>
    /// Gets the index columns for an index as a comma-separated string.
    /// </summary>
    protected static string GetIndexColumns(IDbCommand cmd, string tableName, string indexName)
    {
        cmd.CommandText = $@"
SELECT GROUP_CONCAT(COLUMN_NAME ORDER BY SEQ_IN_INDEX SEPARATOR ',')
  FROM INFORMATION_SCHEMA.STATISTICS
 WHERE TABLE_SCHEMA = DATABASE()
   AND TABLE_NAME = '{tableName}'
   AND INDEX_NAME = '{indexName}'";
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    /// <summary>
    /// Checks if an index is unique.
    /// </summary>
    protected static bool IsIndexUnique(IDbCommand cmd, string tableName, string indexName)
    {
        cmd.CommandText = $@"
SELECT NON_UNIQUE FROM INFORMATION_SCHEMA.STATISTICS
 WHERE TABLE_SCHEMA = DATABASE()
   AND TABLE_NAME = '{tableName}'
   AND INDEX_NAME = '{indexName}'
   AND SEQ_IN_INDEX = 1";
        var nonUnique = cmd.ExecuteScalar();
        return nonUnique != null && Convert.ToInt32(nonUnique) == 0;
    }
}
