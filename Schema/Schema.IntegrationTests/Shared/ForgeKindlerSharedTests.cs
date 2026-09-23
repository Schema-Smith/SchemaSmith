// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.Data;
using System.Linq;
using Schema.DataAccess;
using Schema.Domain;

using NUnit.Framework;
using Schema.Utility;

namespace Schema.IntegrationTests.Shared;

/// <summary>
/// Shared ForgeKindler integration tests for the MySQL/MariaDb family. The MySQL and MariaDb
/// subclasses supply the platform + fixture accessors; every [Test] body here runs on both engines.
/// </summary>
public abstract class ForgeKindlerSharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string MainDb { get; }
    protected abstract string MainConnectionString { get; }

    // INFORMATION_SCHEMA.COLUMNS.COLUMN_DEFAULT reports an empty-string default differently per engine:
    // MySQL as the empty string, MariaDB as the literal two-character token `''`. Both mean "default ''".
    protected virtual string ExpectedEmptyStringColumnDefault => "";

    private IDbConnection _connection = null!;

    [SetUp]
    public void SetUp()
    {
        _connection = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(MainConnectionString);
        _connection.Open();
    }

    [TearDown]
    public void TearDown()
    {
        _connection?.Close();
        _connection?.Dispose();
    }

    [Test]
    public void KindleTheForge_CreatesCompletedMigrationScriptsTable()
    {
        // ForgeKindler is already deployed by FixtureSetup - verify the table exists in target database
        using var command = _connection.CreateCommand();

        command.CommandText = $@"
            SELECT TABLE_NAME
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = '{MainDb}'
            AND TABLE_NAME = 'SchemaSmith_CompletedMigrationScripts'";
        var result = command.ExecuteScalar();
        Assert.That(result, Is.Not.Null);
        Assert.That(result.ToString(), Is.EqualTo("SchemaSmith_CompletedMigrationScripts"));
    }

    [Test]
    public void KindleTheForge_CreatesGenerateTableJSONProcedure()
    {
        // ForgeKindler is already deployed by FixtureSetup - verify the procedure exists in target database
        using var command = _connection.CreateCommand();

        command.CommandText = $@"
            SELECT ROUTINE_NAME
            FROM INFORMATION_SCHEMA.ROUTINES
            WHERE ROUTINE_SCHEMA = '{MainDb}'
            AND ROUTINE_NAME = 'SchemaSmith_GenerateTableJSON'
            AND ROUTINE_TYPE = 'PROCEDURE'";
        var result = command.ExecuteScalar();
        Assert.That(result, Is.Not.Null);
        Assert.That(result.ToString(), Is.EqualTo("SchemaSmith_GenerateTableJSON"));
    }

    [Test]
    public void KindleTheForge_CanBeRunMultipleTimes()
    {
        // TestUser has SYSTEM_USER privilege via docker init script
        using var freshConnection = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(MainConnectionString);
        freshConnection.Open();
        using var command = freshConnection.CreateCommand();

        // Should not throw on multiple runs (idempotent)
        Assert.DoesNotThrow(() => ForgeKindler.KindleTheForge(command, Platform));
        Assert.DoesNotThrow(() => ForgeKindler.KindleTheForge(command, Platform));
    }

    [Test]
    public void KindleTheForge_CreatesCompletedMigrationScriptsSlotScopeIndex()
    {
        // Post-slice-8 cleanup (Commit B): a secondary index on
        // (ProductName, QuenchSlot, template_name, schema_name) covers GetCompletedEntriesBySlot
        // lookups. The PK leads with `Id` and uk_script leads with ScriptPath — both miss the
        // tracking-lookup pattern. The index must exist after KindleTheForge runs (which
        // happens in FixtureSetup).
        using var command = _connection.CreateCommand();
        command.CommandText = $@"
            SELECT COUNT(*)
            FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = '{MainDb}'
              AND TABLE_NAME = 'SchemaSmith_CompletedMigrationScripts'
              AND INDEX_NAME = 'ix_completedmigrationscripts_slot_scope'";
        var matchingRows = System.Convert.ToInt32(command.ExecuteScalar());

        // Multi-column index produces one row per column in INFORMATION_SCHEMA.STATISTICS;
        // 4 columns -> 4 rows. Counting "at least one" proves the index exists; the exact
        // column-set check follows.
        Assert.That(matchingRows, Is.GreaterThan(0),
            "Secondary index ix_completedmigrationscripts_slot_scope must be created by KindleTheForge.");

        command.CommandText = $@"
            SELECT COLUMN_NAME
            FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = '{MainDb}'
              AND TABLE_NAME = 'SchemaSmith_CompletedMigrationScripts'
              AND INDEX_NAME = 'ix_completedmigrationscripts_slot_scope'
            ORDER BY SEQ_IN_INDEX";
        var columns = new List<string>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                columns.Add(reader[0].ToString());

        Assert.That(columns, Is.EqualTo(new[] { "ProductName", "QuenchSlot", "template_name", "schema_name" }),
            "Index must cover (ProductName, QuenchSlot, template_name, schema_name) in order — leading columns are the GetCompletedEntriesBySlot filter.");
    }

    [Test]
    public void KindleTheForge_SlotScopeIndex_IsIdempotent()
    {
        // KindleTheForge runs the kindling script on every quench — the information_schema-
        // guarded PREPARE/EXECUTE block must keep the secondary index creation a no-op when
        // the index already exists (MySQL 8.0.x patch levels predate CREATE INDEX IF NOT EXISTS
        // so the procedural guard is what carries the idempotence here).
        using var freshConnection = DbConnectionFactory.ForPlatform(Platform)
            .GetDbConnection(MainConnectionString);
        freshConnection.Open();
        using var command = freshConnection.CreateCommand();

        // force: version-gated kindle would otherwise skip after the fixture's initial kindle
        Assert.DoesNotThrow(() => ForgeKindler.KindleTheForge(command, Platform, forceReKindle: true),
            "Repeated KindleTheForge must not throw — the secondary index DDL must be idempotent.");

        command.CommandText = $@"
            SELECT COUNT(DISTINCT INDEX_NAME)
            FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = '{MainDb}'
              AND TABLE_NAME = 'SchemaSmith_CompletedMigrationScripts'
              AND INDEX_NAME = 'ix_completedmigrationscripts_slot_scope'";
        var indexCount = System.Convert.ToInt32(command.ExecuteScalar());
        Assert.That(indexCount, Is.EqualTo(1),
            "Re-running KindleTheForge must not produce a duplicate secondary index.");
    }

    [Test]
    public void KindleTheForge_LegacyCompletedMigrationScripts_BootstrapAddsColumnsAndSecondaryIndex()
    {
        // BootstrapTableQuench refactor: pre-slice-2 / pre-Commit-B MySQL table is upgraded
        // in a single kindling pass via Bootstrap's information_schema-guarded ADD COLUMN +
        // ADD INDEX. Replaces the now-deleted Kindling_AlterCompletedMigrationScripts.sql.
        using var freshConnection = DbConnectionFactory.ForPlatform(Platform)
            .GetDbConnection(MainConnectionString);
        freshConnection.Open();
        using var command = freshConnection.CreateCommand();

        try
        {
            command.CommandText = "DROP TABLE IF EXISTS `SchemaSmith_CompletedMigrationScripts`";
            command.ExecuteNonQuery();
            command.CommandText = @"
                CREATE TABLE `SchemaSmith_CompletedMigrationScripts` (
                    `Id` INT AUTO_INCREMENT PRIMARY KEY,
                    `ProductName` VARCHAR(100) NOT NULL,
                    `QuenchSlot` VARCHAR(50) NOT NULL,
                    `ScriptPath` VARCHAR(500) NOT NULL,
                    `CompletedAt` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    UNIQUE KEY `uk_script` (`ProductName`, `QuenchSlot`, `ScriptPath`(255))
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci";
            command.ExecuteNonQuery();

            // force: version-gated kindle would otherwise skip after the fixture's initial kindle
            ForgeKindler.KindleTheForge(command, Platform, forceReKindle: true);

            // Verify column type (VARCHAR(255)), nullability (NOT NULL), default ('').
            // A regression producing a nullable column or missing default would silently break
            // PK extension semantics and GetCompletedEntriesBySlot filtering.
            command.CommandText = $@"
                SELECT COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, IS_NULLABLE, COLUMN_DEFAULT
                  FROM INFORMATION_SCHEMA.COLUMNS
                 WHERE TABLE_SCHEMA = '{MainDb}'
                   AND TABLE_NAME = 'SchemaSmith_CompletedMigrationScripts'
                   AND COLUMN_NAME IN ('template_name', 'schema_name')
                 ORDER BY COLUMN_NAME";
            using (var reader = command.ExecuteReader())
            {
                var rows = new List<(string Name, string Type, long MaxLen, string Nullable, string Default)>();
                while (reader.Read())
                {
                    rows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
                        reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
                }
                reader.Close();
                Assert.That(rows, Has.Count.EqualTo(2),
                    "Bootstrap must add both template_name and schema_name.");
                foreach (var row in rows)
                {
                    Assert.That(row.Type, Is.EqualTo("varchar"), $"{row.Name} type");
                    Assert.That(row.MaxLen, Is.EqualTo(255), $"{row.Name} max length");
                    Assert.That(row.Nullable, Is.EqualTo("NO"), $"{row.Name} must be NOT NULL");
                    Assert.That(row.Default, Is.Not.Null, $"{row.Name} must carry a default");
                    Assert.That(row.Default, Is.EqualTo(ExpectedEmptyStringColumnDefault),
                        $"{row.Name} default must be empty string (information_schema reports it as '' / \"''\")");
                }
            }

            // Verify the index exists AND has the expected column list in order.
            command.CommandText = $@"
                SELECT GROUP_CONCAT(COLUMN_NAME ORDER BY SEQ_IN_INDEX SEPARATOR ',')
                  FROM INFORMATION_SCHEMA.STATISTICS
                 WHERE TABLE_SCHEMA = '{MainDb}'
                   AND TABLE_NAME = 'SchemaSmith_CompletedMigrationScripts'
                   AND INDEX_NAME = 'ix_completedmigrationscripts_slot_scope'";
            var indexCols = command.ExecuteScalar() as string;
            Assert.That(indexCols, Is.EqualTo("ProductName,QuenchSlot,template_name,schema_name"),
                "Secondary index must lead with the GetCompletedEntriesBySlot filter columns in order.");
        }
        finally
        {
            command.CommandText = "DROP TABLE IF EXISTS `SchemaSmith_CompletedMigrationScripts`";
            command.ExecuteNonQuery();
            // force: version-gated kindle would otherwise skip after the fixture's initial kindle
            ForgeKindler.KindleTheForge(command, Platform, forceReKindle: true);
        }
    }

    [Test]
    public void KindleTheForge_LegacyCompletedAtColumn_BootstrapRenamesToQuenchDateAndDataSurvives()
    {
        // The MySQL/MariaDb tracking table shipped with a diverging column name (CompletedAt) where
        // SQL Server / PostgreSQL have always used QuenchDate -- shipped behaviour confirmed at v2.4.0,
        // fixed by teaching Bootstrap to honour a declarative OldName rather than a one-off migration
        // script. This is the real production consumer: Kindling_CompletedMigrationScripts.json now
        // carries OldName: "CompletedAt" on the QuenchDate column.
        using var freshConnection = DbConnectionFactory.ForPlatform(Platform)
            .GetDbConnection(MainConnectionString);
        freshConnection.Open();
        using var command = freshConnection.CreateCommand();

        try
        {
            command.CommandText = "DROP TABLE IF EXISTS `SchemaSmith_CompletedMigrationScripts`";
            command.ExecuteNonQuery();
            command.CommandText = @"
                CREATE TABLE `SchemaSmith_CompletedMigrationScripts` (
                    `Id` INT AUTO_INCREMENT PRIMARY KEY,
                    `ProductName` VARCHAR(100) NOT NULL,
                    `QuenchSlot` VARCHAR(50) NOT NULL,
                    `ScriptPath` VARCHAR(500) NOT NULL,
                    `CompletedAt` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    UNIQUE KEY `uk_script` (`ProductName`, `QuenchSlot`, `ScriptPath`(255))
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci";
            command.ExecuteNonQuery();
            command.CommandText = @"
                INSERT INTO `SchemaSmith_CompletedMigrationScripts` (`ProductName`, `QuenchSlot`, `ScriptPath`, `CompletedAt`)
                VALUES ('Demo', 'Before', 'Before Scripts/Migration_001.sql', '2020-01-02 03:04:05')";
            command.ExecuteNonQuery();

            // force: version-gated kindle would otherwise skip after the fixture's initial kindle
            ForgeKindler.KindleTheForge(command, Platform, forceReKindle: true);

            command.CommandText = $@"
                SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = '{MainDb}'
                  AND TABLE_NAME = 'SchemaSmith_CompletedMigrationScripts'
                  AND COLUMN_NAME = 'CompletedAt'";
            Assert.That(System.Convert.ToInt64(command.ExecuteScalar()), Is.EqualTo(0),
                "The legacy CompletedAt column must be gone after the rename.");

            command.CommandText = $@"
                SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = '{MainDb}'
                  AND TABLE_NAME = 'SchemaSmith_CompletedMigrationScripts'
                  AND COLUMN_NAME = 'QuenchDate'";
            Assert.That(System.Convert.ToInt64(command.ExecuteScalar()), Is.EqualTo(1),
                "QuenchDate must exist after the rename, matching SQL Server / PostgreSQL.");

            // The decisive assertion: a rename that dropped-and-re-added CompletedAt would pass the two
            // checks above while destroying the row's timestamp.
            command.CommandText = @"
                SELECT `QuenchDate` FROM `SchemaSmith_CompletedMigrationScripts`
                WHERE `ScriptPath` = 'Before Scripts/Migration_001.sql'";
            var quenchDate = command.ExecuteScalar();
            Assert.That(System.Convert.ToDateTime(quenchDate), Is.EqualTo(new System.DateTime(2020, 1, 2, 3, 4, 5)),
                "The pre-existing row's timestamp must survive the rename, not read back as the column's DEFAULT (now()).");
        }
        finally
        {
            command.CommandText = "DROP TABLE IF EXISTS `SchemaSmith_CompletedMigrationScripts`";
            command.ExecuteNonQuery();
            // force: version-gated kindle would otherwise skip after the fixture's initial kindle
            ForgeKindler.KindleTheForge(command, Platform, forceReKindle: true);
        }
    }

    [Test]
    public void KindleTheForge_FreshInstall_ProductOwnershipAndStatusMessagesExist()
    {
        // BootstrapTableQuench creates all three MySQL kindling tables on a fresh install.
        // Verify ProductOwnership and StatusMessages are present (CompletedMigrationScripts
        // is covered by KindleTheForge_CreatesCompletedMigrationScriptsTable above).
        using var command = _connection.CreateCommand();
        command.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = '{MainDb}'
              AND TABLE_NAME IN ('SchemaSmith_ProductOwnership', 'SchemaSmith_StatusMessages')";
        Assert.That(System.Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(2),
            "Both ProductOwnership and StatusMessages must be created on fresh install by Bootstrap.");
    }

    [Test]
    public void Kindle_FreshDatabase_WritesStamp()
    {
        using var command = _connection.CreateCommand();
        try
        {
            command.CommandText = "DROP TABLE IF EXISTS SchemaSmith_KindleStamp";
            command.ExecuteNonQuery();

            ForgeKindler.KindleTheForge(command, Platform); // absent stamp => kindles

            Assert.That(ForgeKindler.ReadStamp(command, Platform),
                Is.EqualTo(ForgeKindler.ComputeKindleStamp(Platform)),
                "A fresh kindle must write the current content stamp.");
        }
        finally
        {
            ForgeKindler.KindleTheForge(command, Platform, forceReKindle: true);
        }
    }

    [Test]
    public void Kindle_SecondRun_IsNoOp_StampUnchanged()
    {
        using var command = _connection.CreateCommand();
        ForgeKindler.KindleTheForge(command, Platform, forceReKindle: true); // ensure stamped
        command.CommandText = "SELECT UpdatedUtc FROM SchemaSmith_KindleStamp";
        var first = command.ExecuteScalar();

        ForgeKindler.KindleTheForge(command, Platform); // stamp matches => skip
        command.CommandText = "SELECT UpdatedUtc FROM SchemaSmith_KindleStamp";
        Assert.That(command.ExecuteScalar(), Is.EqualTo(first), "Skip path must not rewrite the stamp.");
    }

    [Test]
    public void Kindle_ForceReKindle_RewritesStamp()
    {
        using var command = _connection.CreateCommand();
        ForgeKindler.KindleTheForge(command, Platform, forceReKindle: true);
        command.CommandText = "SELECT COUNT(*) FROM information_schema.routines " +
                              "WHERE routine_schema = DATABASE() AND routine_name = 'SchemaSmith_TableQuench'";
        Assert.That(System.Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(1),
            "ForceReKindle must leave the helper procs present.");
    }

    [Test]
    public void Kindle_ReleasesGetLock_AfterCompletion()
    {
        // After a kindle, the named GET_LOCK must be released (finally path). IS_FREE_LOCK on the same
        // key must report 1 (free). This also exercises the real MySqlConnector GET_LOCK return path.
        using var command = _connection.CreateCommand();
        ForgeKindler.KindleTheForge(command, Platform, forceReKindle: true);
        // Probe with the same hashed key the production code uses (MySQL GET_LOCK names are capped
        // at 64 chars, so the key is SHA-256-hashed in C# rather than built from DATABASE()).
        command.CommandText = $"SELECT IS_FREE_LOCK('{ForgeKindler.GetMySqlKindleLockName(command)}')";
        Assert.That(System.Convert.ToInt64(command.ExecuteScalar()), Is.EqualTo(1),
            "Kindle must release its GET_LOCK when done.");
    }

    [TestCase("SchemaSmith_ProductOwnership", "ObjectSchema,ObjectName")]
    [TestCase("SchemaSmith_ExpressionMap", "ObjectSchema,ObjectTable,ObjectName")]
    public void KindleTheForge_LegacyCaseInsensitiveIdentifierColumns_BootstrapConvergesThemToBinary(
        string table, string identifierColumns)
    {
        // The UPGRADE half of the case-differing-identifier fix, and the half every EXISTING
        // installation depends on. Every other fixture creates a brand-new database, so these tables are
        // CREATED with utf8mb4_bin identifier columns and the convergence path is never entered -- which
        // means a regression to add-missing-columns-only would leave every deployed installation on the
        // case-INSENSITIVE key, where the second of two case-differing tables silently gets no ownership
        // row (unprotected, and invisible to drop-by-absence) while the suite stays green.
        //
        // So: hand-create the table in its pre-fix shape, force a kindle, and assert the collation moved.
        using var freshConnection = DbConnectionFactory.ForPlatform(Platform)
            .GetDbConnection(MainConnectionString);
        freshConnection.Open();
        using var command = freshConnection.CreateCommand();

        var columns = identifierColumns.Split(',');

        try
        {
            command.CommandText = $"DROP TABLE IF EXISTS `{table}`";
            command.ExecuteNonQuery();

            // The pre-fix shape: identifier columns on the TABLE's utf8mb4_unicode_ci default, which is
            // what a case-insensitive unique key is made of. Only the columns the fix converges are
            // declared here; Bootstrap adds the rest, which is the same upgrade a real installation gets.
            var identifierDdl = string.Join(", ", columns.Select(c => $"`{c}` VARCHAR(64) NOT NULL"));
            command.CommandText = $@"
                CREATE TABLE `{table}` (
                    `Id` INT AUTO_INCREMENT PRIMARY KEY,
                    {identifierDdl}
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci";
            command.ExecuteNonQuery();

            // Guards the premise. If the hand-created table somehow already had the target collation,
            // the assertion below would pass without Bootstrap having converged anything at all.
            Assert.That(CollationsOf(command, table, columns),
                Has.All.EqualTo("utf8mb4_unicode_ci"),
                "the legacy shape must start case-INSENSITIVE, or this test proves nothing");

            // force: the version-gated kindle would otherwise skip after the fixture's initial kindle
            ForgeKindler.KindleTheForge(command, Platform, forceReKindle: true);

            Assert.That(CollationsOf(command, table, columns), Has.All.EqualTo("utf8mb4_bin"),
                $"kindling an EXISTING {table} must converge its identifier columns to a case-sensitive "
                + "collation. Left case-insensitive, the unique key conflates two tables whose names "
                + "differ only by case and the second one silently goes unowned.");
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS `{table}`";
            command.ExecuteNonQuery();
            ForgeKindler.KindleTheForge(command, Platform, forceReKindle: true);
        }
    }

    private List<string> CollationsOf(IDbCommand command, string table, IEnumerable<string> columns)
    {
        var list = string.Join(",", columns.Select(c => $"'{c}'"));
        command.CommandText = $@"
            SELECT COLLATION_NAME
              FROM INFORMATION_SCHEMA.COLUMNS
             WHERE TABLE_SCHEMA = '{MainDb}'
               AND TABLE_NAME = '{table}'
               AND COLUMN_NAME IN ({list})
             ORDER BY COLUMN_NAME";
        var found = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) found.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
        reader.Close();
        Assert.That(found, Is.Not.Empty, $"no identifier columns found on {table}");
        return found;
    }
}
