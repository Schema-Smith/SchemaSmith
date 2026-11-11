// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.SqlServer;

// A table rebuild drops every foreign key that references the table, because sp_rename carries an inbound key onto the
// table moved aside. Only keys this package declares came back, so a key held by another product's table, or one a DBA
// added, was gone for good; and a declared key that was untrusted or disabled came back trusted and enabled -- or failed
// on the orphan rows it had been created over, after the rebuild had committed. A rebuild now sets every inbound key
// aside and the foreign-key pass restores it in the state it was captured in.
[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.Self)]
public class RebuildTablePreservationTests : BaseTableQuenchTests
{
    private sealed class Run : IDisposable
    {
        public readonly SqlConnection Conn;
        public readonly string Token = $"Rbk{Guid.NewGuid().ToString("N")[..8]}";
        public string Parent => $"{Token}_P";

        public Run(string connectionString, string db)
        {
            Conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(connectionString);
            Conn.Open();
            Conn.ChangeDatabase(db);
        }

        public void Exec(string sql)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandTimeout = 300;
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        public string Scalar(string sql)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandTimeout = 300;
            cmd.CommandText = sql;
            var v = cmd.ExecuteScalar();
            return v == null || v == DBNull.Value ? null : v.ToString();
        }

        public void Parse(string json)
        {
            var (createTables, fillTables) = ForgeKindler.GetParseTableJsonPhases(Platform.SqlServer);
            using var cmd = Conn.CreateCommand();
            cmd.CommandTimeout = 300;
            cmd.CommandText = createTables;
            cmd.ExecuteNonQuery();
            cmd.CommandText = "DECLARE @v_SQL NVARCHAR(MAX) = ''\nSET NOCOUNT ON\n" + fillTables;
            cmd.Parameters.Add("@TableDefinitions", SqlDbType.NVarChar, -1).Value = json;
            cmd.Parameters.Add("@UpdateFillFactor", SqlDbType.Bit).Value = false;
            cmd.ExecuteNonQuery();
        }

        // The deploy's table and foreign-key passes in one session, as SchemaQuench runs them.
        public void Deploy(string json, string product, bool foreignKeys = true, bool dropRemovedTables = false, bool dropUnknownIndexes = false)
        {
            Parse(json);
            Exec("EXEC SchemaSmith.MissingTableAndColumnQuench @WhatIf = 0");
            Exec($"EXEC SchemaSmith.ModifiedTableQuench @ProductName = '{product}', @WhatIf = 0, @DropUnknownIndexes = {(dropUnknownIndexes ? 1 : 0)}, @DropTablesRemovedFromProduct = {(dropRemovedTables ? 1 : 0)}, @RebuildPolicyMode = 'ALWAYS'");
            Exec($"EXEC SchemaSmith.MissingIndexesAndConstraintsQuench '{product}', 0");
            if (foreignKeys) Exec($"EXEC SchemaSmith.ForeignKeyQuench @ProductName = '{product}', @WhatIf = 0");
        }

        public string ParentJson(string valType, string childTable = null, string childKey = null, string idType = "INT")
        {
            var parent = $$"""
  { "Schema": "[dbo]", "Name": "[{{Parent}}]",
    "Columns": [ { "Name": "[Id]", "DataType": "{{idType}}", "Nullable": false }, { "Name": "[Val]", "DataType": "{{valType}}", "Nullable": true } ],
    "Indexes": [ { "Name": "[PK_{{Parent}}]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" } ] }
""";
            var child = childTable == null ? "" : $$"""
,
  { "Schema": "[dbo]", "Name": "[{{childTable}}]",
    "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false }, { "Name": "[ParentId]", "DataType": "{{idType}}", "Nullable": true } ],
    "ForeignKeys": [ { "Name": "[{{childKey}}]", "Columns": "[ParentId]", "RelatedTableSchema": "[dbo]", "RelatedTable": "[{{Parent}}]", "RelatedColumns": "[Id]" } ] }
""";
            return "[" + parent + child + "]";
        }

        public string Key(string name, string column) => Scalar(
            $"SELECT CAST(CASE WHEN fk.referenced_object_id = OBJECT_ID('dbo.{Parent}') THEN 1 ELSE 0 END AS VARCHAR(1)) + '|' + " +
            $"CAST(fk.is_not_trusted AS VARCHAR(1)) + '|' + CAST(fk.is_disabled AS VARCHAR(1)) FROM sys.foreign_keys fk WHERE fk.[name] = '{name}'");

        public void Dispose()
        {
            Exec($@"
DECLARE @s NVARCHAR(MAX) = N'';
SELECT @s = @s + N'ALTER TABLE ' + QUOTENAME(OBJECT_SCHEMA_NAME(parent_object_id)) + N'.' + QUOTENAME(OBJECT_NAME(parent_object_id)) + N' DROP CONSTRAINT ' + QUOTENAME([name]) + N';'
  FROM sys.foreign_keys WHERE [name] LIKE '{Token}%';
EXEC (@s);
DECLARE @t NVARCHAR(MAX) = N'';
SELECT @t = @t + N'DROP TABLE ' + QUOTENAME(SCHEMA_NAME([schema_id])) + N'.' + QUOTENAME([name]) + N';' FROM sys.tables WHERE [name] LIKE '{Token}%';
EXEC (@t);
IF OBJECT_ID('SchemaSmith.RebuildSetAsideForeignKeys') IS NOT NULL DELETE FROM SchemaSmith.RebuildSetAsideForeignKeys WHERE [KeyName] LIKE '{Token}%';");
            Conn.Dispose();
        }
    }

    private Run Seeded()
    {
        var r = new Run(_connectionString, _mainDb);
        r.Deploy(r.ParentJson("NVARCHAR(50)"), _productName);
        r.Exec($"INSERT INTO dbo.[{r.Parent}] (Id, Val) VALUES (1, 'a'), (2, 'b')");
        return r;
    }

    // A deploy that declares a key a rebuild set aside found it missing and added it in its batch; another deploy's restore
    // put it back between the look and the ADD, and the batch failed on the name. The other session holds the child table
    // with a data lock, which the catalog read does not wait on and the ADD does, then adds the key itself and commits.
    [Test]
    public void ADeclaredKeyAnotherDeployRestoresFirst_DoesNotFailTheDeployThatDeclaresIt()
    {
        using var r = Seeded();
        var child = $"{r.Token}_C";
        var key = $"{r.Token}_fk_decl";
        r.Exec($"CREATE TABLE dbo.[{child}] (Id INT NOT NULL, ParentId INT NULL)");
        r.Exec($@"INSERT INTO SchemaSmith.RebuildSetAsideForeignKeys ([ChildSchema], [ChildTable], [KeyName], [ParentSchema], [ParentTable], [Definition], [WasTrusted], [WasEnabled], [Declared])
                  VALUES ('dbo', '{child}', '{key}', 'dbo', '{r.Parent}', 'FOREIGN KEY ([ParentId]) REFERENCES [dbo].[{r.Parent}] ([Id])', 1, 1, 1)");
        r.Parse($$"""
[ { "Schema": "[dbo]", "Name": "[{{child}}]",
    "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false }, { "Name": "[ParentId]", "DataType": "INT", "Nullable": true } ],
    "ForeignKeys": [ { "Name": "[{{key}}]", "Columns": "[ParentId]", "RelatedTableSchema": "[dbo]", "RelatedTable": "[{{r.Parent}}]", "RelatedColumns": "[Id]" } ] } ]
""");
        var deploySpid = r.Scalar("SELECT @@SPID");

        using var other = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        other.Open();
        other.ChangeDatabase(_mainDb);
        using var otherCmd = other.CreateCommand();
        otherCmd.CommandText = $"BEGIN TRAN; SELECT TOP (0) * FROM dbo.[{child}] WITH (TABLOCKX, HOLDLOCK);";
        otherCmd.ExecuteNonQuery();

        var deploy = Task.Run(() => r.Exec($"EXEC SchemaSmith.ForeignKeyQuench @ProductName = 'Declares_{r.Token}', @WhatIf = 0"));
        // Add the key only once the deploy's own ADD is waiting for the table: it has already looked, and found no key.
        otherCmd.CommandText = $"SELECT COUNT(*) FROM sys.dm_exec_requests WHERE session_id = {deploySpid} AND wait_type LIKE 'LCK_M_SCH_M%'";
        var waiting = System.Diagnostics.Stopwatch.StartNew();
        while (Convert.ToInt32(otherCmd.ExecuteScalar()) == 0 && !deploy.IsCompleted && waiting.Elapsed < TimeSpan.FromSeconds(60))
            Thread.Sleep(100);
        Assert.That(deploy.IsCompleted, Is.False, "setup: the deploy should be waiting at its ADD" + (deploy.Exception == null ? "" : ": " + deploy.Exception.GetBaseException().Message));
        otherCmd.CommandText = $"ALTER TABLE dbo.[{child}] ADD CONSTRAINT [{key}] FOREIGN KEY ([ParentId]) REFERENCES dbo.[{r.Parent}] ([Id]); COMMIT;";
        otherCmd.ExecuteNonQuery();

        // The deploy holds r.Conn until it finishes, so nothing reads through it before the wait.
        Exception failure = null;
        try { deploy.Wait(TimeSpan.FromMinutes(2)); }
        catch (AggregateException ex) { failure = ex; }
        Assert.That(failure, Is.Null, () => "the declared key is there, so there is nothing for this deploy to fail on: " + failure?.GetBaseException().Message);
        Assert.Multiple(() =>
        {
            Assert.That(r.Key(key, "ParentId"), Is.EqualTo("1|0|0"), "the key is on its table, trusted and enabled");
            Assert.That(r.Scalar($"SELECT COUNT(*) FROM SchemaSmith.RebuildSetAsideForeignKeys WHERE [KeyName] = '{key}'"), Is.EqualTo("0"), "a key back on its table is done with");
            Assert.That(r.Scalar($"SELECT COUNT(*) FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID AND ObjectName LIKE '%{key}%' AND ActionType = 'created'"),
                Is.EqualTo("0"), "this deploy did not create the key, so it does not report creating it");
        });
    }

    // Paul, 2026-11-10: a rebuild never loses an index the deploy keeps. One the package does not declare is refused by
    // name with DropUnknownIndexes off, before anything changes; with it on the deploy drops it anyway, and the rebuild runs.
    [Test]
    public void AnIndexThePackageDoesNotDeclare_RefusesTheRebuildByName_UnlessDropUnknownIndexes()
    {
        using var r = Seeded();
        r.Exec($"CREATE INDEX [{r.Token}_IX] ON dbo.[{r.Parent}] (Val)");
        var before = r.Scalar($"SELECT OBJECT_ID('dbo.{r.Parent}')");

        var ex = Assert.Throws<SqlException>(() => r.Deploy(r.ParentJson("NVARCHAR(60)"), _productName));
        Assert.Multiple(() =>
        {
            Assert.That(ex.Message, Does.Contain($"{r.Token}_IX").And.Contain("Table rebuild refused"));
            Assert.That(r.Scalar($"SELECT OBJECT_ID('dbo.{r.Parent}')"), Is.EqualTo(before), "the table was not rebuilt");
            Assert.That(r.Scalar($"SELECT COUNT(*) FROM sys.indexes WHERE [name] = '{r.Token}_IX'"), Is.EqualTo("1"), "and the index is still there");
        });

        r.Deploy(r.ParentJson("NVARCHAR(60)"), _productName, dropUnknownIndexes: true);
        Assert.That(r.Scalar($"SELECT OBJECT_ID('dbo.{r.Parent}')"), Is.Not.EqualTo(before), "with DropUnknownIndexes the rebuild runs");
    }

    [Test]
    public void AKeyTheProductDoesNotDeclare_SurvivesARebuildOfTheTableItReferences()
    {
        using var r = Seeded();
        r.Exec($"CREATE TABLE dbo.[{r.Token}_C] (Id INT NOT NULL, ParentId INT NULL CONSTRAINT [{r.Token}_FK] REFERENCES dbo.[{r.Parent}] (Id) ON DELETE CASCADE); " +
               $"INSERT INTO dbo.[{r.Token}_C] VALUES (10, 1)");

        r.Deploy(r.ParentJson("NVARCHAR(60)"), _productName);

        Assert.Multiple(() =>
        {
            Assert.That(r.Scalar($"SELECT COUNT(*) FROM sys.tables WHERE [name] = '{r.Parent}_SchemaSmithOld'"), Is.EqualTo("0"), "the table was rebuilt");
            Assert.That(r.Key($"{r.Token}_FK", "ParentId"), Is.EqualTo("1|0|0"), "the key is back, on the new table, trusted and enabled");
            Assert.That(r.Scalar($"SELECT delete_referential_action_desc FROM sys.foreign_keys WHERE [name] = '{r.Token}_FK'"), Is.EqualTo("CASCADE"), "with its actions");
            Assert.That(() => r.Exec($"INSERT INTO dbo.[{r.Token}_C] VALUES (11, 99)"), Throws.InstanceOf<SqlException>().With.Property("Number").EqualTo(547), "and enforcing");
            Assert.That(r.Scalar($"SELECT COUNT(*) FROM SchemaSmith.RebuildSetAsideForeignKeys WHERE [KeyName] LIKE '{r.Token}%'"), Is.EqualTo("0"), "nothing is left set aside");
        });
    }

    [Test]
    public void UntrustedAndDisabledKeys_KeepTheirState()
    {
        using var r = Seeded();
        r.Exec($"CREATE TABLE dbo.[{r.Token}_U] (Id INT NOT NULL, ParentId INT NULL); INSERT INTO dbo.[{r.Token}_U] VALUES (10, 99); " +
               $"ALTER TABLE dbo.[{r.Token}_U] WITH NOCHECK ADD CONSTRAINT [{r.Token}_FKU] FOREIGN KEY (ParentId) REFERENCES dbo.[{r.Parent}] (Id); " +
               $"CREATE TABLE dbo.[{r.Token}_D] (Id INT NOT NULL, ParentId INT NULL CONSTRAINT [{r.Token}_FKD] REFERENCES dbo.[{r.Parent}] (Id)); " +
               $"ALTER TABLE dbo.[{r.Token}_D] NOCHECK CONSTRAINT [{r.Token}_FKD]");

        r.Deploy(r.ParentJson("NVARCHAR(60)"), _productName);

        Assert.Multiple(() =>
        {
            Assert.That(r.Key($"{r.Token}_FKU", "ParentId"), Is.EqualTo("1|1|0"), "the key created over an orphan row comes back untrusted");
            Assert.That(r.Key($"{r.Token}_FKD", "ParentId"), Is.EqualTo("1|1|1"), "the disabled key comes back disabled");
        });
    }

    [Test]
    public void ADeclaredKeyLeftUntrusted_IsRestoredUntrusted_OverTheOrphanRowsItHad()
    {
        using var r = Seeded();
        var child = $"{r.Token}_C";
        var key = $"{r.Token}_FK";
        r.Deploy(r.ParentJson("NVARCHAR(50)", child, key), _productName);
        r.Exec($"ALTER TABLE dbo.[{child}] DROP CONSTRAINT [{key}]; INSERT INTO dbo.[{child}] VALUES (10, 99); " +
               $"ALTER TABLE dbo.[{child}] WITH NOCHECK ADD CONSTRAINT [{key}] FOREIGN KEY (ParentId) REFERENCES dbo.[{r.Parent}] (Id)");

        Assert.DoesNotThrow(() => r.Deploy(r.ParentJson("NVARCHAR(60)", child, key), _productName),
            "a declared key that omits Validated keeps the trust it had; re-added WITH CHECK it failed on the orphan row after the rebuild");
        Assert.That(r.Key(key, "ParentId"), Is.EqualTo("1|1|0"));
    }

    [Test]
    public void AKeySetAsideByARunThatStoppedBeforeTheForeignKeyPass_IsRestoredByTheNextRun()
    {
        using var r = Seeded();
        r.Exec($"CREATE TABLE dbo.[{r.Token}_C] (Id INT NOT NULL, ParentId INT NULL CONSTRAINT [{r.Token}_FK] REFERENCES dbo.[{r.Parent}] (Id))");

        // The run stops right after the rebuild: the table has no primary key yet, and the key is set aside.
        r.Parse(r.ParentJson("NVARCHAR(60)"));
        r.Exec($"EXEC SchemaSmith.RebuildTable @p_Schema = 'dbo', @p_Table = '{r.Parent}'");
        Assert.That(r.Scalar($"SELECT COUNT(*) FROM SchemaSmith.RebuildSetAsideForeignKeys WHERE [KeyName] = '{r.Token}_FK'"), Is.EqualTo("1"), "setup: the key is recorded");

        // Another product deploys before this one runs again. The key cannot come back yet, and that must not fail a
        // deploy that has nothing to do with it.
        using (var other = new Run(_connectionString, _mainDb))
        {
            Assert.DoesNotThrow(() => other.Deploy($$"""
[ { "Schema": "[dbo]", "Name": "[{{other.Token}}_U]", "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false } ] } ]
""", $"Unrelated_{r.Token}"));
        }
        Assert.That(r.Scalar($"SELECT COUNT(*) FROM SchemaSmith.RebuildSetAsideForeignKeys WHERE [KeyName] = '{r.Token}_FK'"), Is.EqualTo("1"), "and the key waits");

        r.Deploy(r.ParentJson("NVARCHAR(60)"), _productName);

        Assert.Multiple(() =>
        {
            Assert.That(r.Key($"{r.Token}_FK", "ParentId"), Is.EqualTo("1|0|0"));
            Assert.That(r.Scalar($"SELECT COUNT(*) FROM SchemaSmith.RebuildSetAsideForeignKeys WHERE [KeyName] = '{r.Token}_FK'"), Is.EqualTo("0"));
        });
    }

    [Test]
    public void AnUndeclaredKeyThatCouldNotBeRestored_RefusesTheRebuildByName_BeforeAnythingChanges()
    {
        using var r = Seeded();
        r.Exec($"CREATE TABLE dbo.[{r.Token}_C] (Id INT NOT NULL, ParentId INT NULL CONSTRAINT [{r.Token}_FK] REFERENCES dbo.[{r.Parent}] (Id))");
        var before = r.Scalar($"SELECT OBJECT_ID('dbo.{r.Parent}')");

        var ex = Assert.Catch(() => r.Deploy(r.ParentJson("NVARCHAR(60)", idType: "BIGINT"), _productName));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain($"{r.Token}_FK").And.Contain("the referenced columns to keep their current definition"),
                "an INT key cannot reference a BIGINT column, so the key could not be put back");
            Assert.That(r.Scalar($"SELECT OBJECT_ID('dbo.{r.Parent}')"), Is.EqualTo(before), "the table was not rebuilt");
            Assert.That(r.Scalar($"SELECT COUNT(*) FROM dbo.[{r.Parent}]"), Is.EqualTo("2"));
            Assert.That(r.Key($"{r.Token}_FK", "ParentId"), Is.EqualTo("1|0|0"), "and the key is where it was");
        });
    }

    [Test]
    public void AnUndeclaredKeyOnTheProductsOwnTable_IsDroppedAsTheInPlacePathWouldHaveDroppedIt()
    {
        using var r = Seeded();
        var child = $"{r.Token}_C";
        r.Deploy(r.ParentJson("NVARCHAR(50)", child, $"{r.Token}_FK"), _productName);
        r.Exec($"ALTER TABLE dbo.[{child}] ADD CONSTRAINT [{r.Token}_FK2] FOREIGN KEY (ParentId) REFERENCES dbo.[{r.Parent}] (Id)");

        r.Deploy(r.ParentJson("NVARCHAR(60)", child, $"{r.Token}_FK"), _productName);

        Assert.Multiple(() =>
        {
            Assert.That(r.Key($"{r.Token}_FK", "ParentId"), Is.EqualTo("1|0|0"), "the declared key is back");
            Assert.That(r.Scalar($"SELECT COUNT(*) FROM sys.foreign_keys WHERE [name] = '{r.Token}_FK2'"), Is.EqualTo("0"), "the undeclared one on the product's own table is dropped, as it would be without a rebuild");
            Assert.That(r.Scalar($"SELECT COUNT(*) FROM SchemaSmith.RebuildSetAsideForeignKeys WHERE [KeyName] LIKE '{r.Token}%'"), Is.EqualTo("0"));
        });
    }

    // A key the rest of this step drops anyway -- from a table it drops for leaving the package, or one the package does not
    // declare on a table it does -- is not one the rebuild has to put back, so it cannot refuse the rebuild.
    [Test]
    public void AKeyFromATableThisDeployDrops_DoesNotRefuseTheRebuild()
    {
        using var r = Seeded();
        var child = $"{r.Token}_C";
        r.Deploy(r.ParentJson("NVARCHAR(50)", child, $"{r.Token}_FK"), _productName);

        Assert.DoesNotThrow(() => r.Deploy(r.ParentJson("NVARCHAR(60)", idType: "BIGINT"), _productName, dropRemovedTables: true));

        Assert.Multiple(() =>
        {
            Assert.That(r.Scalar($"SELECT TYPE_NAME(system_type_id) FROM sys.columns WHERE [object_id] = OBJECT_ID('dbo.{r.Parent}') AND [name] = 'Id'"), Is.EqualTo("bigint"));
            Assert.That(r.Scalar($"SELECT OBJECT_ID('dbo.{child}')"), Is.Null, "the table that left the package is dropped");
            Assert.That(r.Scalar($"SELECT COUNT(*) FROM SchemaSmith.RebuildSetAsideForeignKeys WHERE [KeyName] LIKE '{r.Token}%'"), Is.EqualTo("0"), "and nothing waits to be put back");
        });
    }

    [Test]
    public void AnUndeclaredKeyOnTheProductsOwnTable_DoesNotRefuseARebuildThatChangesTheColumnItReferences()
    {
        using var r = Seeded();
        var child = $"{r.Token}_C";
        r.Deploy(r.ParentJson("NVARCHAR(50)", child, $"{r.Token}_FK"), _productName);
        r.Exec($"ALTER TABLE dbo.[{child}] ADD CONSTRAINT [{r.Token}_FK2] FOREIGN KEY (ParentId) REFERENCES dbo.[{r.Parent}] (Id)");

        Assert.DoesNotThrow(() => r.Deploy(r.ParentJson("NVARCHAR(60)", child, $"{r.Token}_FK", idType: "BIGINT"), _productName));

        Assert.Multiple(() =>
        {
            Assert.That(r.Key($"{r.Token}_FK", "ParentId"), Is.EqualTo("1|0|0"), "the declared key is back");
            Assert.That(r.Scalar($"SELECT COUNT(*) FROM sys.foreign_keys WHERE [name] = '{r.Token}_FK2'"), Is.EqualTo("0"), "the undeclared one is dropped");
            Assert.That(r.Scalar($"SELECT COUNT(*) FROM SchemaSmith.RebuildSetAsideForeignKeys WHERE [KeyName] LIKE '{r.Token}%'"), Is.EqualTo("0"));
        });
    }

    // The identity counter was captured before the rebuild's lock, so a row another session inserted in between was
    // copied across and the shadow then reseeded below it: the next insert collided with that row.
    [Test]
    public void ARowInsertedWhileTheRebuildWaitsForItsLock_DoesNotCollideWithTheNextIdentity()
    {
        using var r = new Run(_connectionString, _mainDb);
        string Json(string valType) => $$"""
[ { "Schema": "[dbo]", "Name": "[{{r.Parent}}]",
    "Columns": [ { "Name": "[Id]", "DataType": "INT IDENTITY(1,1)", "Nullable": false }, { "Name": "[Val]", "DataType": "{{valType}}", "Nullable": true } ],
    "Indexes": [ { "Name": "[PK_{{r.Parent}}]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" } ] } ]
""";
        r.Deploy(Json("NVARCHAR(50)"), _productName);
        r.Exec($"INSERT INTO dbo.[{r.Parent}] (Val) VALUES ('a'), ('b')");

        using var other = new SqlConnection(_connectionString);
        other.Open();
        other.ChangeDatabase(_mainDb);
        using var hold = other.CreateCommand();
        hold.CommandText = $"BEGIN TRANSACTION; UPDATE dbo.[{r.Parent}] WITH (TABLOCKX) SET Val = Val WHERE Id = 1;";
        hold.ExecuteNonQuery();

        var spid = r.Scalar("SELECT @@SPID");
        var rebuild = Task.Run(() => r.Deploy(Json("NVARCHAR(60)"), _productName));
        using var watch = new SqlConnection(_connectionString);
        watch.Open();
        using var w = watch.CreateCommand();
        w.CommandText = $"SELECT COUNT(*) FROM sys.dm_exec_requests WHERE session_id = {spid} AND blocking_session_id = {other.ServerProcessId}";
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while ((int)w.ExecuteScalar()! == 0)
        {
            Assert.That(rebuild.IsCompleted, Is.False, "the rebuild should wait for the other session's lock");
            Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "the rebuild never reached the lock");
            Thread.Sleep(50);
        }

        hold.CommandText = $"INSERT INTO dbo.[{r.Parent}] (Val) VALUES ('c'); COMMIT;";
        hold.ExecuteNonQuery();
        rebuild.Wait(TimeSpan.FromSeconds(120));

        Assert.That(() => r.Exec($"INSERT INTO dbo.[{r.Parent}] (Val) VALUES ('d')"), Throws.Nothing,
            "the counter carried across is the one the table had once it was locked");
        Assert.That(r.Scalar($"SELECT COUNT(*) FROM dbo.[{r.Parent}]"), Is.EqualTo("4"));
    }

    // ---- placement and compression ---------------------------------------------------------------------------------
    //
    // The shadow was created with no clauses, so a rebuilt table landed on the default filegroup, uncompressed. A declared
    // placement then failed every later deploy's "declared but deployed elsewhere" check, an undeclared one moved without a
    // word, and a declared compression was paid for twice.

    private const string FileGroupA = "FG_SchemaSmithTestA";
    private const string FileGroupB = "FG_SchemaSmithTestB";

    [OneTimeSetUp]
    public void EnsureFileGroupsExist()
    {
        using var r = new Run(_connectionString, _mainDb);
        foreach (var fg in new[] { FileGroupA, FileGroupB })
            r.Exec($@"
IF NOT EXISTS (SELECT 1 FROM sys.filegroups WHERE [name] = '{fg}')
BEGIN
  DECLARE @v_Path NVARCHAR(500) = (SELECT LEFT(physical_name, LEN(physical_name) - CHARINDEX('\', REVERSE(physical_name)) + 1) FROM sys.master_files WHERE database_id = DB_ID('{_mainDb}') AND file_id = 1);
  ALTER DATABASE [{_mainDb}] ADD FILEGROUP [{fg}];
  EXEC('ALTER DATABASE [{_mainDb}] ADD FILE (NAME = ''{fg}_1'', FILENAME = ''' + @v_Path + '{fg}_1.ndf'') TO FILEGROUP [{fg}]');
END");
    }

    private static string PlacedJson(string table, string valType, string tableClauses = "", string indexClauses = "") => $$"""
[ { "Schema": "[dbo]", "Name": "[{{table}}]"{{tableClauses}},
    "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false }, { "Name": "[Val]", "DataType": "{{valType}}", "Nullable": true } ],
    "Indexes": [ { "Name": "[PK_{{table}}]", "PrimaryKey": true, "Unique": true, "Clustered": false, "IndexColumns": "[Id]"{{indexClauses}} } ] } ]
""";

    private static string DataSpace(Run r, string table, int indexId = 0) => r.Scalar(
        $"SELECT ds.[name] FROM sys.indexes i JOIN sys.data_spaces ds ON ds.data_space_id = i.data_space_id WHERE i.[object_id] = OBJECT_ID('dbo.{table}') AND i.index_id = {indexId}");

    [Test]
    public void ADeclaredFileGroup_IsWhereTheRebuiltTableLands_AndTheNextDeploySucceeds()
    {
        using var r = new Run(_connectionString, _mainDb);
        var json = (string valType) => PlacedJson(r.Parent, valType, $", \"FileGroup\": \"[{FileGroupA}]\"", $", \"FileGroup\": \"[{FileGroupA}]\"");
        r.Deploy(json("NVARCHAR(50)"), _productName);
        var before = r.Scalar($"SELECT OBJECT_ID('dbo.{r.Parent}')");

        r.Deploy(json("NVARCHAR(60)"), _productName);

        Assert.Multiple(() =>
        {
            Assert.That(r.Scalar($"SELECT OBJECT_ID('dbo.{r.Parent}')"), Is.Not.EqualTo(before), "setup: the table was rebuilt");
            Assert.That(DataSpace(r, r.Parent), Is.EqualTo(FileGroupA), "the rebuilt heap is on its declared filegroup");
            Assert.That(() => r.Deploy(json("NVARCHAR(60)"), _productName), Throws.Nothing, "and the next deploy finds it there");
        });
    }

    [Test]
    public void AnUndeclaredPlacement_StaysWhereItWas()
    {
        using var r = new Run(_connectionString, _mainDb);
        r.Exec($"CREATE TABLE dbo.[{r.Parent}] (Id INT NOT NULL CONSTRAINT [PK_{r.Parent}] PRIMARY KEY NONCLUSTERED ON [{FileGroupB}], Val NVARCHAR(50) NULL) ON [{FileGroupB}]");
        r.Deploy(PlacedJson(r.Parent, "NVARCHAR(50)"), _productName);
        var before = r.Scalar($"SELECT OBJECT_ID('dbo.{r.Parent}')");

        r.Deploy(PlacedJson(r.Parent, "NVARCHAR(60)"), _productName);

        Assert.Multiple(() =>
        {
            Assert.That(r.Scalar($"SELECT OBJECT_ID('dbo.{r.Parent}')"), Is.Not.EqualTo(before), "setup: the table was rebuilt");
            Assert.That(DataSpace(r, r.Parent), Is.EqualTo(FileGroupB), "a placement the package leaves unmanaged is not the rebuild's to change");
        });
    }

    [Test]
    public void AnIndexOnAnotherFileGroupWithNoDeclaredFileGroup_RefusesTheRebuildByName()
    {
        using var r = new Run(_connectionString, _mainDb);
        r.Exec($"CREATE TABLE dbo.[{r.Parent}] (Id INT NOT NULL, Val NVARCHAR(50) NULL); " +
               $"ALTER TABLE dbo.[{r.Parent}] ADD CONSTRAINT [PK_{r.Parent}] PRIMARY KEY NONCLUSTERED (Id) ON [{FileGroupB}]");
        r.Deploy(PlacedJson(r.Parent, "NVARCHAR(50)"), _productName);
        var before = r.Scalar($"SELECT OBJECT_ID('dbo.{r.Parent}')");

        var ex = Assert.Catch(() => r.Deploy(PlacedJson(r.Parent, "NVARCHAR(60)"), _productName));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain($"PK_{r.Parent}").And.Contain(FileGroupB).And.Contain("does not declare its FileGroup"));
            Assert.That(r.Scalar($"SELECT OBJECT_ID('dbo.{r.Parent}')"), Is.EqualTo(before), "the table was not rebuilt");
            Assert.That(DataSpace(r, r.Parent, 2), Is.EqualTo(FileGroupB), "and the index is where it was");
        });
    }

    [Test]
    public void AHeapDeclaredCompressed_IsWrittenOnce()
    {
        using var r = new Run(_connectionString, _mainDb);
        var json = (string valType) => PlacedJson(r.Parent, valType, ", \"CompressionType\": \"PAGE\"");
        r.Deploy(json("NVARCHAR(50)"), _productName);
        r.Exec($"INSERT INTO dbo.[{r.Parent}] (Id, Val) VALUES (1, 'a')");
        var messages = new System.Collections.Generic.List<string>();
        r.Conn.InfoMessage += (_, e) => messages.Add(e.Message);

        r.Deploy(json("NVARCHAR(60)"), _productName);

        Assert.Multiple(() =>
        {
            Assert.That(messages, Has.Some.Contains($"Rebuilding table [dbo].[{r.Parent}]"), "setup: the table was rebuilt");
            Assert.That(r.Scalar($"SELECT data_compression_desc FROM sys.partitions WHERE [object_id] = OBJECT_ID('dbo.{r.Parent}') AND index_id = 0"), Is.EqualTo("PAGE"));
            Assert.That(messages, Has.None.Contains("Altering table compression"), "the shadow was created compressed, so nothing rewrites it again");
        });
    }

    [Test]
    public void ADeclaredFileStreamColumn_RefusesTheRebuild_BeforeAnythingChanges()
    {
        using var r = Seeded();
        r.Parse($$"""
[ { "Schema": "[dbo]", "Name": "[{{r.Parent}}]",
    "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false }, { "Name": "[Val]", "DataType": "NVARCHAR(50)", "Nullable": true },
                 { "Name": "[Doc]", "DataType": "VARBINARY(MAX)", "Nullable": true, "FileStream": true } ] } ]
""");
        var before = r.Scalar($"SELECT OBJECT_ID('dbo.{r.Parent}')");

        var ex = Assert.Catch(() => r.Exec($"EXEC SchemaSmith.RebuildTable @p_Schema = 'dbo', @p_Table = '{r.Parent}'"));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("FILESTREAM column"));
            Assert.That(r.Scalar($"SELECT OBJECT_ID('dbo.{r.Parent}')"), Is.EqualTo(before));
        });
    }
}
