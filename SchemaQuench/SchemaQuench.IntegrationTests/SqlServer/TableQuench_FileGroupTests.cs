// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Microsoft.Data.SqlClient;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Domain.SqlServer;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.SqlServer;

// Coverage for SQL Server filegroup placement (#filegroups): a table/index deliberately placed on a
// non-default filegroup previously had no domain property, no extraction, and no emit -- a redeploy of an
// extracted package silently put everything on PRIMARY. The design is names-only (never a physical path),
// errors loudly if the named filegroup does not exist (SchemaSmith does not create filegroups), and errors
// on a "move" (a declared placement differing from the deployed one) rather than silently rebuilding.
// Each test owns a UNIQUE product name so it is scoped to its own tables under parallel execution.
[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_FileGroupTests : BaseTableQuenchTests
{
    private const string FileGroupA = "FG_SchemaSmithTestA";
    private const string FileGroupB = "FG_SchemaSmithTestB";

    // Ensures both test filegroups exist on _mainDb, reusing the database's own data-file directory.
    // Idempotent (IF NOT EXISTS) so re-running the suite against an already-prepared database is a no-op --
    // same "create once, never drop" posture as the "histtest" schema in the temporal tests.
    [OneTimeSetUp]
    public void EnsureFileGroupsExist()
    {
        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        foreach (var fg in new[] { FileGroupA, FileGroupB })
        {
            cmd.CommandText = $@"
IF NOT EXISTS (SELECT 1 FROM sys.filegroups WHERE [name] = '{fg}')
BEGIN
  DECLARE @v_Path NVARCHAR(500) = (SELECT LEFT(physical_name, LEN(physical_name) - CHARINDEX('\', REVERSE(physical_name)) + 1) FROM sys.master_files WHERE database_id = DB_ID('{_mainDb}') AND file_id = 1);
  ALTER DATABASE [{_mainDb}] ADD FILEGROUP [{fg}];
  EXEC('ALTER DATABASE [{_mainDb}] ADD FILE (NAME = ''{fg}_1'', FILENAME = ''' + @v_Path + '{fg}_1.ndf'') TO FILEGROUP [{fg}]');
END";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    [Test]
    public void TableQuench_TableOnNonDefaultFileGroup_DeploysAndRoundTrips()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"FGTableProduct_{uid}";
        var table = $"FGTable_{uid}";

        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithTableFileGroup(table, FileGroupA), productName: product);

            Assert.That(LiveTableFileGroup(cmd, table), Is.EqualTo(FileGroupA), "the table's data must be deployed on the declared non-default filegroup");

            var extracted = GenerateTable(cmd, table);
            Assert.That(extracted.FileGroup, Is.EqualTo($"[{FileGroupA}]"), "extraction must round-trip the non-default filegroup");
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS [dbo].[{table}];";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    [Test]
    public void TableQuench_IndexOnDifferentFileGroupFromTable_Deploys()
    {
        // The case that motivates the feature: a table and one of its indexes deliberately split
        // across two different filegroups.
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"FGIndexProduct_{uid}";
        var table = $"FGIndexTable_{uid}";

        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithTableAndIndexOnDifferentFileGroups(table, FileGroupA, FileGroupB), productName: product);

            Assert.Multiple(() =>
            {
                Assert.That(LiveTableFileGroup(cmd, table), Is.EqualTo(FileGroupA), "the table's own data must be on FileGroupA");
                Assert.That(LiveIndexFileGroup(cmd, table, $"IX_{table}_Somedata"), Is.EqualTo(FileGroupB), "the index must be on the different, explicitly-declared FileGroupB");
            });
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS [dbo].[{table}];";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    [Test]
    public void TableQuench_DeclaredTableFileGroupDoesNotExist_ThrowsNamingIt()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"FGMissingProduct_{uid}";
        var table = $"FGMissingTable_{uid}";
        const string missingFileGroup = "FG_SchemaSmithTest_DoesNotExist";

        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            var ex = Assert.Throws<SqlException>(() => RunTableQuenchProc(cmd, WithTableFileGroup(table, missingFileGroup), productName: product));
            Assert.Multiple(() =>
            {
                Assert.That(ex!.Message, Does.Contain(table), "the error must name the offending table");
                Assert.That(ex.Message, Does.Contain(missingFileGroup), "the error must name the missing filegroup");
                Assert.That(ex.Message, Does.Contain("does not exist"), "the error must say the filegroup does not exist -- not a generic failure");
                Assert.That(ObjectExists(cmd, table), Is.False, "the table must NOT have been created when its declared filegroup is missing");
            });
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS [dbo].[{table}];";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    [Test]
    public void TableQuench_DeclaredIndexFileGroupDoesNotExist_ThrowsNamingIt()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"FGIdxMissingProduct_{uid}";
        var table = $"FGIdxMissingTable_{uid}";
        const string missingFileGroup = "FG_SchemaSmithTest_DoesNotExist";

        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            var ex = Assert.Throws<SqlException>(() => RunTableQuenchProc(cmd, WithTableAndIndexOnDifferentFileGroups(table, null, missingFileGroup), productName: product));
            Assert.Multiple(() =>
            {
                Assert.That(ex!.Message, Does.Contain($"IX_{table}_Somedata"), "the error must name the offending index");
                Assert.That(ex.Message, Does.Contain(missingFileGroup), "the error must name the missing filegroup");
                Assert.That(ex.Message, Does.Contain("does not exist"), "the error must say the filegroup does not exist");
            });
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS [dbo].[{table}];";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    [Test]
    public void TableQuench_DeclaredTableFileGroupDiffersFromDeployed_ThrowsNamingBoth()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"FGMoveProduct_{uid}";
        var table = $"FGMoveTable_{uid}";

        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            // Deploy on the default filegroup (no FileGroup declared) -- ordinary create.
            RunTableQuenchProc(cmd, WithTable(table), productName: product);
            Assert.That(ObjectExists(cmd, table), Is.True, "Setup: table should exist after the first quench.");

            // Redeploy the SAME table now declaring a different, real filegroup -- a move.
            var ex = Assert.Throws<SqlException>(() => RunTableQuenchProc(cmd, WithTableFileGroup(table, FileGroupA), productName: product));
            Assert.Multiple(() =>
            {
                Assert.That(ex!.Message, Does.Contain(table), "the error must name the table");
                Assert.That(ex.Message, Does.Contain(FileGroupA), "the error must name the declared filegroup");
                Assert.That(ex.Message, Does.Contain("PRIMARY"), "the error must name the currently-deployed filegroup");
                Assert.That(LiveTableFileGroup(cmd, table), Is.Null, "the table must NOT have been moved -- still on the default filegroup (PRIMARY)");
            });
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS [dbo].[{table}];";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    [Test]
    public void TableQuench_DeclaredIndexFileGroupDiffersFromDeployed_ThrowsNamingBoth()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"FGIdxMoveProduct_{uid}";
        var table = $"FGIdxMoveTable_{uid}";

        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            // The index starts life explicitly on FileGroupA.
            RunTableQuenchProc(cmd, WithTableAndIndexOnDifferentFileGroups(table, null, FileGroupA), productName: product);
            Assert.That(LiveIndexFileGroup(cmd, table, $"IX_{table}_Somedata"), Is.EqualTo(FileGroupA), "Setup: index should be on FileGroupA.");

            // Redeploy declaring the index on FileGroupB instead -- a move.
            var ex = Assert.Throws<SqlException>(() => RunTableQuenchProc(cmd, WithTableAndIndexOnDifferentFileGroups(table, null, FileGroupB), productName: product));
            Assert.Multiple(() =>
            {
                Assert.That(ex!.Message, Does.Contain($"IX_{table}_Somedata"), "the error must name the index");
                Assert.That(ex.Message, Does.Contain(FileGroupB), "the error must name the declared filegroup");
                Assert.That(ex.Message, Does.Contain(FileGroupA), "the error must name the currently-deployed filegroup");
                Assert.That(LiveIndexFileGroup(cmd, table, $"IX_{table}_Somedata"), Is.EqualTo(FileGroupA), "the index must NOT have been moved -- still on FileGroupA");
            });
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS [dbo].[{table}];";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    [Test]
    public void TableQuench_DeclaredIndexFileGroupDiffersFromDeployed_RefusesBeforeDroppingAnything()
    {
        // #511: the move check used to run after the foreign keys the package no longer declares were dropped, so a
        // refused deploy had already taken them. It now runs before the first drop.
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"FGIdxOrderProduct_{uid}";
        var table = $"FGIdxOrderTable_{uid}";

        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithTableAndIndexOnDifferentFileGroups(table, null, FileGroupA), productName: product);
            cmd.CommandText = $"ALTER TABLE [dbo].[{table}] ADD [ParentId] INT NULL; ALTER TABLE [dbo].[{table}] ADD CONSTRAINT [FK_{table}_Parent] FOREIGN KEY ([ParentId]) REFERENCES [dbo].[{table}] ([Id]);";
            cmd.ExecuteNonQuery();

            var ex = Assert.Throws<SqlException>(() => RunTableQuenchProc(cmd, WithTableAndIndexOnDifferentFileGroups(table, null, FileGroupB), productName: product));
            cmd.CommandText = $"SELECT CONVERT(VARCHAR(1), (SELECT COUNT(*) FROM sys.foreign_keys WHERE [name] = 'FK_{table}_Parent')) + ',' + CONVERT(VARCHAR(1), (SELECT COUNT(*) FROM sys.columns WHERE [object_id] = OBJECT_ID('dbo.{table}') AND [name] = 'ParentId'))";
            Assert.Multiple(() =>
            {
                Assert.That(ex!.Message, Does.Contain(FileGroupB), "the move is refused");
                Assert.That(cmd.ExecuteScalar(), Is.EqualTo("1,1"), "and the undeclared foreign key and column are still there: the refusal came first");
            });
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS [dbo].[{table}];";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    [Test]
    public void TableQuench_NoFileGroupDeclared_IsCompletelyUnaffected()
    {
        // Backward-compat guard: the state of every existing package. A table/index declaring no
        // FileGroup must deploy to the default filegroup exactly as it always has, redeploy as a true
        // no-op, and extract with no FileGroup key anywhere.
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"FGUnaffectedProduct_{uid}";
        var table = $"FGUnaffectedTable_{uid}";

        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithTable(table), productName: product);
            Assert.That(LiveTableFileGroup(cmd, table), Is.Null, "an ordinary table must deploy to the default filegroup (PRIMARY)");
            Assert.That(LiveIndexFileGroup(cmd, table, $"PK_{table}"), Is.Null, "an ordinary index must deploy to the default filegroup (PRIMARY)");

            // Redeploy the identical package -- must be a true no-op, not an error.
            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, WithTable(table), productName: product));

            var extracted = GenerateTable(cmd, table);
            Assert.Multiple(() =>
            {
                Assert.That(extracted.FileGroup, Is.Null);
                Assert.That(((SqlServerIndex)extracted.Indexes[0]).FileGroup, Is.Null);
            });
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS [dbo].[{table}];";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    // --IndexOnly coverage (#filegroups): SchemaSmith.IndexOnlyQuench/IndexOnlyXmlQuench are a separate,
    // fully duplicated entry point with their own inline parsing and their own copy of the index-creation
    // DDL -- they carried zero FileGroup handling at all, so a package deployed via --IndexOnly silently
    // placed every index on the default filegroup. Same contract as the main path above: deploys to a
    // declared non-default filegroup, errors naming both when it doesn't exist or differs from deployed,
    // and is a no-op when unset.
    [Test]
    public void IndexOnly_IndexOnNonDefaultFileGroup_DeploysThere()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"FGIdxOnlyProduct_{uid}";
        var table = $"FGIdxOnlyTable_{uid}";

        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            cmd.CommandText = $"CREATE TABLE [dbo].[{table}] (Id INT NOT NULL, Somedata VARCHAR(100) NULL);";
            cmd.ExecuteNonQuery();

            RunTableQuenchProc(cmd, WithIndexOnlyIndexFileGroup(table, FileGroupA), indexOnly: true, productName: product);

            Assert.That(LiveIndexFileGroup(cmd, table, $"IX_{table}_Somedata"), Is.EqualTo(FileGroupA),
                "an index deployed via --IndexOnly must land on its declared non-default filegroup, matching the main deploy path");
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS [dbo].[{table}];";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    [Test]
    public void IndexOnly_DeclaredIndexFileGroupDoesNotExist_ThrowsNamingIt()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"FGIdxOnlyMissingProduct_{uid}";
        var table = $"FGIdxOnlyMissingTable_{uid}";
        const string missingFileGroup = "FG_SchemaSmithTest_DoesNotExist";

        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            cmd.CommandText = $"CREATE TABLE [dbo].[{table}] (Id INT NOT NULL, Somedata VARCHAR(100) NULL);";
            cmd.ExecuteNonQuery();

            var ex = Assert.Throws<SqlException>(() => RunTableQuenchProc(cmd, WithIndexOnlyIndexFileGroup(table, missingFileGroup), indexOnly: true, productName: product));
            Assert.Multiple(() =>
            {
                Assert.That(ex!.Message, Does.Contain($"IX_{table}_Somedata"), "the error must name the offending index");
                Assert.That(ex.Message, Does.Contain(missingFileGroup), "the error must name the missing filegroup");
                Assert.That(ex.Message, Does.Contain("does not exist"), "the error must say the filegroup does not exist -- same contract as the main deploy path");
            });
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS [dbo].[{table}];";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    [Test]
    public void IndexOnly_NoFileGroupDeclared_IsCompletelyUnaffected()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"FGIdxOnlyUnaffectedProduct_{uid}";
        var table = $"FGIdxOnlyUnaffectedTable_{uid}";

        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            cmd.CommandText = $"CREATE TABLE [dbo].[{table}] (Id INT NOT NULL, Somedata VARCHAR(100) NULL);";
            cmd.ExecuteNonQuery();

            RunTableQuenchProc(cmd, WithIndexOnlyIndex(table), indexOnly: true, productName: product);
            Assert.That(LiveIndexFileGroup(cmd, table, $"IX_{table}_Somedata"), Is.Null,
                "an --IndexOnly index with no declared FileGroup must deploy to the database's default filegroup exactly as before");

            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, WithIndexOnlyIndex(table), indexOnly: true, productName: product),
                "a redeploy of the identical package via --IndexOnly must be a true no-op");
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS [dbo].[{table}];";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    private static string WithIndexOnlyIndexFileGroup(string table, string indexFileGroup) => $$"""
[
  {
    "Schema": "[dbo]",
    "Name": "[{{table}}]",
    "Indexes": [
      { "Name": "[IX_{{table}}_Somedata]", "IndexColumns": "[Somedata]", "FileGroup": "[{{indexFileGroup}}]" }
    ]
  }
]
""";

    private static string WithIndexOnlyIndex(string table) => $$"""
[
  {
    "Schema": "[dbo]",
    "Name": "[{{table}}]",
    "Indexes": [
      { "Name": "[IX_{{table}}_Somedata]", "IndexColumns": "[Somedata]" }
    ]
  }
]
""";

    private static string WithTable(string table) => $$"""
[
  {
    "Schema": "[dbo]",
    "Name": "[{{table}}]",
    "Columns": [
      { "Name": "[Id]",       "DataType": "INT",          "Nullable": false },
      { "Name": "[Somedata]", "DataType": "VARCHAR(100)", "Nullable": true }
    ],
    "Indexes": [ { "Name": "[PK_{{table}}]", "PrimaryKey": true, "IndexColumns": "[Id]" } ]
  }
]
""";

    // Every other filegroup test deploys exactly once, so none of them exercises the move guard against a
    // package that has already been deployed. All three cases below fail only on the SECOND deploy.

    // A table declares a filegroup; its PK declares nothing. An index created with no ON clause lands on
    // the TABLE's filegroup, not the database default -- and ParseTableJson deliberately does not default an
    // index's FileGroup from its table's. So the undeclared PK read as declared-PRIMARY against live-FG_A and
    // the redeploy failed, naming a filegroup the package never mentions. This is the shape of the fixture
    // WithTableFileGroup itself produces: the documented hand-authoring style.
    [Test]
    public void TableQuench_TableLevelFileGroupOnly_RedeploysCleanly()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"FGRedeployProduct_{uid}";
        var table = $"FGRedeployTable_{uid}";

        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithTableFileGroup(table, FileGroupA), productName: product);
            Assert.That(LiveIndexFileGroup(cmd, table, $"PK_{table}"), Is.EqualTo(FileGroupA),
                "Setup: an index with no ON clause follows its table onto the non-default filegroup.");

            Assert.DoesNotThrow(
                () => RunTableQuenchProc(cmd, WithTableFileGroup(table, FileGroupA), productName: product),
                "Redeploying the identical package must be a no-op, not a move-guard failure.");
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS [dbo].[{table}];";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    // A regression for users who never touch the feature: a table already sitting on a non-default filegroup
    // -- a DBA's placement -- whose package declares nothing. That deployed fine before filegroups existed,
    // and unset must keep meaning "SchemaSmith does not manage placement".
    [Test]
    public void TableQuench_UndeclaredTableAlreadyOnNonDefaultFileGroup_IsLeftAlone()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"FGUndeclaredProduct_{uid}";
        var table = $"FGUndeclaredTable_{uid}";

        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            // Placed by hand on a non-default filegroup, exactly as a DBA would have.
            cmd.CommandText = $@"CREATE TABLE [dbo].[{table}] ([Id] INT NOT NULL, [Somedata] VARCHAR(100) NULL,
                                 CONSTRAINT [PK_{table}] PRIMARY KEY CLUSTERED ([Id]) ON [{FileGroupA}]) ON [{FileGroupA}];";
            cmd.ExecuteNonQuery();

            Assert.DoesNotThrow(
                () => RunTableQuenchProc(cmd, WithTableNoFileGroup(table), productName: product),
                "A package that declares no filegroup must not fight a placement it never asked about.");
            Assert.That(LiveTableFileGroup(cmd, table), Is.EqualTo(FileGroupA),
                "The pre-existing placement must be left exactly where it was.");
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS [dbo].[{table}];";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    // The IndexOnly twin of the regression above, and the case an --IndexOnly package is FOR: a vendor
    // database whose tables someone else placed. The existing unaffected-test passes only because its table
    // sits on PRIMARY, so the undeclared index compared equal to the default by luck.
    [Test]
    public void IndexOnly_NoFileGroupDeclared_AgainstTableOnNonDefaultFileGroup_IsUnaffected()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"FGIdxOnlyOffDefaultProduct_{uid}";
        var table = $"FGIdxOnlyOffDefaultTable_{uid}";

        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            cmd.CommandText = $"CREATE TABLE [dbo].[{table}] (Id INT NOT NULL, Somedata VARCHAR(100) NULL) ON [{FileGroupA}];";
            cmd.ExecuteNonQuery();

            RunTableQuenchProc(cmd, WithIndexOnlyIndex(table), indexOnly: true, productName: product);
            Assert.That(LiveIndexFileGroup(cmd, table, $"IX_{table}_Somedata"), Is.EqualTo(FileGroupA),
                "An index created with no ON clause follows its table, not the database default.");

            Assert.DoesNotThrow(
                () => RunTableQuenchProc(cmd, WithIndexOnlyIndex(table), indexOnly: true, productName: product),
                "Declaring no filegroup must stay a no-op against a table someone else placed.");
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS [dbo].[{table}];";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    private static string WithTableNoFileGroup(string table) => $$"""
[
  {
    "Schema": "[dbo]",
    "Name": "[{{table}}]",
    "Columns": [
      { "Name": "[Id]",       "DataType": "INT",          "Nullable": false },
      { "Name": "[Somedata]", "DataType": "VARCHAR(100)", "Nullable": true }
    ],
    "Indexes": [ { "Name": "[PK_{{table}}]", "PrimaryKey": true, "Clustered": true, "IndexColumns": "[Id]" } ]
  }
]
""";

    private static string WithTableFileGroup(string table, string fileGroup) => $$"""
[
  {
    "Schema": "[dbo]",
    "Name": "[{{table}}]",
    "FileGroup": "[{{fileGroup}}]",
    "Columns": [
      { "Name": "[Id]",       "DataType": "INT",          "Nullable": false },
      { "Name": "[Somedata]", "DataType": "VARCHAR(100)", "Nullable": true }
    ],
    "Indexes": [ { "Name": "[PK_{{table}}]", "PrimaryKey": true, "IndexColumns": "[Id]" } ]
  }
]
""";

    // tableFileGroup may be null (table stays on the default filegroup); the index always declares one.
    // A JSON `null` for FileGroup parses identically to the key being absent (both become SQL NULL).
    private static string WithTableAndIndexOnDifferentFileGroups(string table, string tableFileGroup, string indexFileGroup)
    {
        var tableFileGroupJson = tableFileGroup is null ? "null" : $"\"[{tableFileGroup}]\"";
        return $$"""
[
  {
    "Schema": "[dbo]",
    "Name": "[{{table}}]",
    "FileGroup": {{tableFileGroupJson}},
    "Columns": [
      { "Name": "[Id]",       "DataType": "INT",          "Nullable": false },
      { "Name": "[Somedata]", "DataType": "VARCHAR(100)", "Nullable": true }
    ],
    "Indexes": [
      { "Name": "[PK_{{table}}]", "PrimaryKey": true, "IndexColumns": "[Id]" },
      { "Name": "[IX_{{table}}_Somedata]", "IndexColumns": "[Somedata]", "FileGroup": "[{{indexFileGroup}}]" }
    ]
  }
]
""";
    }

    private static bool ObjectExists(IDbCommand cmd, string tableName)
    {
        cmd.CommandText = $"SELECT CASE WHEN OBJECT_ID('dbo.{tableName}') IS NULL THEN 0 ELSE 1 END";
        return Convert.ToInt32(cmd.ExecuteScalar()) == 1;
    }

    // Live filegroup name for a table's own data (heap/clustered index, index_id 0/1) -- NULL when it is
    // on the database's default filegroup, mirroring the production emit-only-when-non-default contract.
    private static string LiveTableFileGroup(IDbCommand cmd, string tableName)
    {
        cmd.CommandText = $@"
SELECT fg.[name]
  FROM sys.indexes si WITH (NOLOCK)
  JOIN sys.filegroups fg WITH (NOLOCK) ON fg.data_space_id = si.data_space_id
 WHERE si.[object_id] = OBJECT_ID('dbo.{tableName}')
   AND si.index_id IN (0, 1)
   AND fg.is_default = 0";
        var result = cmd.ExecuteScalar();
        return result as string;
    }

    private static string LiveIndexFileGroup(IDbCommand cmd, string tableName, string indexName)
    {
        cmd.CommandText = $@"
SELECT fg.[name]
  FROM sys.indexes si WITH (NOLOCK)
  JOIN sys.filegroups fg WITH (NOLOCK) ON fg.data_space_id = si.data_space_id
 WHERE si.[object_id] = OBJECT_ID('dbo.{tableName}')
   AND si.[name] = '{indexName}'
   AND fg.is_default = 0";
        var result = cmd.ExecuteScalar();
        return result as string;
    }

    private static SqlServerTable GenerateTable(IDbCommand cmd, string table)
    {
        cmd.CommandText = $"EXEC [SchemaSmith].GenerateTableJSON @p_Schema = 'dbo', @p_Table = '{table}'";
        using var reader = TransientRetry.Run(() => cmd.ExecuteReader());
        var json = string.Empty;
        while (reader.Read()) json += $"{reader.GetString(0)}\r\n";
        return (SqlServerTable)PlatformDeserializer.DeserializeTable(json, Platform.SqlServer);
    }
}
