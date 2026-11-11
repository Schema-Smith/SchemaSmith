// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Delivery;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.SqlServer;

/// <summary>
/// Integration tests for MergeScriptHelper against SQL Server.
/// Uses dynamically created test databases via FixtureSetup.
/// Tests special data type handling: GEOMETRY, GEOGRAPHY, HIERARCHYID, DATETIMEOFFSET, VARBINARY, XML.
/// </summary>
[Category("SqlServer")]
[TestFixture]
[Category("Integration")]
public class MergeScriptHelperIntegrationTests
{
    private IDbConnection _connection = null!;

    [SetUp]
    public void SetUp()
    {
        _connection = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        _connection.Open();
    }

    [TearDown]
    public void TearDown()
    {
        _connection?.Close();
        _connection?.Dispose();
    }

    #region Key-only tables

    // A table whose only column is its identity has nothing to update; MergeUpdate built the update list from a null
    // column list and failed with a NullReferenceException before any script was written.
    [Test]
    public void BuildMergeScript_IdentityOnlyTable_WithMergeUpdate_DeliversItsRows()
    {
        using var command = _connection.CreateCommand();
        var tableName = $"_test_keyonly_{Guid.NewGuid():N}"[..40];
        try
        {
            command.CommandText = $"CREATE TABLE [dbo].[{tableName}] ([Id] INT IDENTITY(1,1) NOT NULL PRIMARY KEY)";
            command.ExecuteNonQuery();
            command.CommandText = $"INSERT INTO [dbo].[{tableName}] DEFAULT VALUES";
            command.ExecuteNonQuery();

            var script = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, command,
                "dbo", tableName, @"[{""Id"":1},{""Id"":2}]", "[Id]",
                mergeUpdate: true, mergeDelete: true, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null);
            command.CommandText = script;
            command.ExecuteNonQuery();

            command.CommandText = $"SELECT COUNT(*) FROM [dbo].[{tableName}]";
            Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(2));
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS [dbo].[{tableName}]";
            command.ExecuteNonQuery();
        }
    }

    #endregion

    #region GEOMETRY Tests

    [Test]
    public void BuildMergeScript_GeometryColumn_GeneratesValidScript()
    {
        using var command = _connection.CreateCommand();
        var tableName = $"_test_geom_{Guid.NewGuid():N}"[..40];

        try
        {
            command.CommandText = $@"
CREATE TABLE [dbo].[{tableName}] (
    [Id] INT PRIMARY KEY,
    [Shape] GEOMETRY NOT NULL
)";
            command.ExecuteNonQuery();

            var tableData = @"[{""Id"":1,""Shape"":""POINT (1 2)"",""Shape.STSrid"":0}]";
            var script = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, command,
                "dbo", tableName, tableData, "[Id]",
                mergeUpdate: true, mergeDelete: false, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null);

            Assert.That(script, Does.Contain("geometry::STGeomFromText"));
            Assert.That(script, Does.Contain("NVARCHAR(4000)"));
            Assert.That(script, Does.Contain(".STSrid]"));
            Assert.That(script, Does.Contain(".ToString()"));

            // Execute the script and verify data
            command.CommandText = script;
            command.ExecuteNonQuery();

            command.CommandText = $"SELECT [Shape].ToString(), [Shape].STSrid FROM [dbo].[{tableName}] WHERE [Id] = 1";
            using var reader = command.ExecuteReader();
            Assert.That(reader.Read(), Is.True);
            Assert.That(reader.GetString(0), Is.EqualTo("POINT (1 2)"));
            Assert.That(reader.GetInt32(1), Is.EqualTo(0));
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS [dbo].[{tableName}]";
            command.ExecuteNonQuery();
        }
    }

    // The merge key was the first unique index of any kind. A filtered one is not unique across the table, so rows
    // sharing its value were merged as one; and INCLUDE columns widened it, so a changed included value inserted a
    // duplicate.
    [TestCase("CREATE UNIQUE INDEX [{0}] ON [dbo].[{1}] (grp) WHERE v > 0")]
    [TestCase(null)]
    public void GetKeyColumns_TakesOnlyAWholeTableUniqueKey_AndTheUpsertKeepsEveryRow(string decoyIndex)
    {
        using var command = _connection.CreateCommand();
        var tableName = $"_test_key_{Guid.NewGuid():N}"[..40];
        try
        {
            command.CommandText = $@"CREATE TABLE [dbo].[{tableName}] (id INT NOT NULL, grp INT NOT NULL, v INT NOT NULL, name NVARCHAR(10) NOT NULL);
                {(decoyIndex == null ? "" : string.Format(decoyIndex, tableName[..30] + "_d", tableName) + ";")}
                CREATE UNIQUE INDEX [{tableName[..30]}_k] ON [dbo].[{tableName}] (id) INCLUDE (v);
                INSERT INTO [dbo].[{tableName}] VALUES (1, 1, 5, N'A'), (2, 1, -2, N'b');";
            command.ExecuteNonQuery();

            var keyColumns = MergeScriptHelper.GetKeyColumns(Platform.SqlServer, command, "dbo", tableName);
            Assert.That(keyColumns, Is.EqualTo("[id]"));

            const string tableData = @"[{""id"":1,""grp"":1,""v"":7,""name"":""A""},{""id"":2,""grp"":1,""v"":-2,""name"":""b""}]";
            command.CommandText = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, command, "dbo", tableName, tableData,
                keyColumns, mergeUpdate: true, mergeDelete: false, disableTriggers: false, tokenizeScripts: false, mergeFilter: null);
            command.ExecuteNonQuery();

            command.CommandText = $@"SELECT STRING_AGG(CONCAT(id, ':', grp, ':', v, ':', name), ',') WITHIN GROUP (ORDER BY id) FROM [dbo].[{tableName}]";
            Assert.That(command.ExecuteScalar()?.ToString(), Is.EqualTo("1:1:7:A,2:1:-2:b"));
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS [dbo].[{tableName}]";
            command.ExecuteNonQuery();
        }
    }

    [Test]
    public void BuildMergeScript_GeometryColumn_UpdateExistingRow()
    {
        using var command = _connection.CreateCommand();
        var tableName = $"_test_geom_upd_{Guid.NewGuid():N}"[..40];

        try
        {
            command.CommandText = $@"
CREATE TABLE [dbo].[{tableName}] (
    [Id] INT PRIMARY KEY,
    [Shape] GEOMETRY NOT NULL
);
INSERT INTO [dbo].[{tableName}] VALUES (1, geometry::STGeomFromText('POINT (0 0)', 0));";
            command.ExecuteNonQuery();

            var tableData = @"[{""Id"":1,""Shape"":""LINESTRING (0 0, 1 1, 2 2)"",""Shape.STSrid"":0}]";
            var script = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, command,
                "dbo", tableName, tableData, "[Id]",
                mergeUpdate: true, mergeDelete: false, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null);

            command.CommandText = script;
            command.ExecuteNonQuery();

            command.CommandText = $"SELECT [Shape].ToString() FROM [dbo].[{tableName}] WHERE [Id] = 1";
            var result = command.ExecuteScalar()?.ToString();
            Assert.That(result, Is.EqualTo("LINESTRING (0 0, 1 1, 2 2)"));
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS [dbo].[{tableName}]";
            command.ExecuteNonQuery();
        }
    }

    #endregion

    #region GEOGRAPHY Regression Tests

    [Test]
    public void BuildMergeScript_GeographyColumn_RoundTrip()
    {
        using var command = _connection.CreateCommand();
        var tableName = $"_test_geog_{Guid.NewGuid():N}"[..40];

        try
        {
            command.CommandText = $@"
CREATE TABLE [dbo].[{tableName}] (
    [Id] INT PRIMARY KEY,
    [Location] GEOGRAPHY NOT NULL
)";
            command.ExecuteNonQuery();

            var tableData = @"[{""Id"":1,""Location"":""POINT (-122.34 47.65)"",""Location.STSrid"":4326}]";
            var script = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, command,
                "dbo", tableName, tableData, "[Id]",
                mergeUpdate: true, mergeDelete: false, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null);

            Assert.That(script, Does.Contain("geography::STGeomFromText"));

            command.CommandText = script;
            command.ExecuteNonQuery();

            command.CommandText = $"SELECT [Location].ToString(), [Location].STSrid FROM [dbo].[{tableName}] WHERE [Id] = 1";
            using var reader = command.ExecuteReader();
            Assert.That(reader.Read(), Is.True);
            Assert.That(reader.GetString(0), Does.Contain("POINT"));
            Assert.That(reader.GetInt32(1), Is.EqualTo(4326));
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS [dbo].[{tableName}]";
            command.ExecuteNonQuery();
        }
    }

    #endregion

    // A NULL spatial value has no SRID, and STGeomFromText refuses a NULL one even for NULL text, so a delivered row with
    // a NULL geometry or geography failed the delivery -- as did a value from a package written before the SRID was.
    [TestCase("Json")]
    [TestCase("Xml")]
    public void BuildMergeScript_ANullSpatialValue_AndOneWithoutItsSrid_Deliver(string encoding)
    {
        using var command = _connection.CreateCommand();
        var tableName = $"_test_spnull_{Guid.NewGuid():N}"[..40];
        try
        {
            command.CommandText = $"CREATE TABLE [dbo].[{tableName}] ([Id] INT PRIMARY KEY, [Shape] GEOMETRY NULL, [Location] GEOGRAPHY NULL)";
            command.ExecuteNonQuery();

            var json = "[{\"Id\":1,\"Shape\":null,\"Location\":null},"
                       + "{\"Id\":2,\"Shape\":\"POINT (1 2)\",\"Location\":\"POINT (-122.34 47.65)\"},"
                       + "{\"Id\":3,\"Shape\":\"POINT (3 4)\",\"Shape.STSrid\":32633,\"Location\":\"POINT (1 1)\",\"Location.STSrid\":4269}]";
            var tableData = encoding == "Xml" ? MergeScriptHelper.JsonPayloadToXml(json) : json;
            command.CommandText = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, command, "dbo", tableName, tableData, "[Id]",
                mergeUpdate: true, mergeDelete: false, disableTriggers: false, tokenizeScripts: false, mergeFilter: null, contentEncoding: encoding);
            command.ExecuteNonQuery();

            command.CommandText = $"SELECT [Id], [Shape].STAsText(), [Shape].STSrid, [Location].STSrid FROM [dbo].[{tableName}] ORDER BY [Id]";
            var rows = new List<string>();
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    rows.Add(string.Join("|", Enumerable.Range(0, 4).Select(k => reader.IsDBNull(k) ? "null" : reader.GetValue(k).ToString())));
            Assert.That(rows, Is.EqualTo(new[] { "1|null|null|null", "2|POINT (1 2)|0|4326", "3|POINT (3 4)|32633|4269" }),
                "a NULL arrives NULL; a value without its SRID takes the type's default (0, 4326); one with it keeps it");
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS [dbo].[{tableName}]";
            command.ExecuteNonQuery();
        }
    }

    #region HIERARCHYID Tests

    [Test]
    [TestCase("/1/")]
    [TestCase("/1/2/3/")]
    [TestCase("/1/2/3/4/5/")]
    public void BuildMergeScript_HierarchyIdColumn_RoundTrip(string hierarchyPath)
    {
        using var command = _connection.CreateCommand();
        var tableName = $"_test_hier_{Guid.NewGuid():N}"[..40];

        try
        {
            command.CommandText = $@"
CREATE TABLE [dbo].[{tableName}] (
    [Id] INT PRIMARY KEY,
    [OrgNode] HIERARCHYID NOT NULL
)";
            command.ExecuteNonQuery();

            var tableData = $@"[{{""Id"":1,""OrgNode"":""{hierarchyPath}""}}]";
            var script = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, command,
                "dbo", tableName, tableData, "[Id]",
                mergeUpdate: true, mergeDelete: false, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null);

            // HIERARCHYID mapped to NVARCHAR(4000) in JSON column definitions
            Assert.That(script, Does.Contain("NVARCHAR(4000)"));

            command.CommandText = script;
            command.ExecuteNonQuery();

            command.CommandText = $"SELECT [OrgNode].ToString() FROM [dbo].[{tableName}] WHERE [Id] = 1";
            var result = command.ExecuteScalar()?.ToString();
            Assert.That(result, Is.EqualTo(hierarchyPath));
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS [dbo].[{tableName}]";
            command.ExecuteNonQuery();
        }
    }

    #endregion

    #region DATETIMEOFFSET Tests

    [Test]
    public void BuildMergeScript_DateTimeOffsetColumn_RoundTripPreservesOffset()
    {
        using var command = _connection.CreateCommand();
        var tableName = $"_test_dto_{Guid.NewGuid():N}"[..40];

        try
        {
            command.CommandText = $@"
CREATE TABLE [dbo].[{tableName}] (
    [Id] INT PRIMARY KEY,
    [EventTime] DATETIMEOFFSET NOT NULL
)";
            command.ExecuteNonQuery();

            var tableData = @"[{""Id"":1,""EventTime"":""2024-06-15T10:30:00.0000000+05:30""}]";
            var script = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, command,
                "dbo", tableName, tableData, "[Id]",
                mergeUpdate: true, mergeDelete: false, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null);

            // DATETIMEOFFSET mapped to NVARCHAR(50) in JSON column definitions
            Assert.That(script, Does.Contain("NVARCHAR(50)"));
            // D[ prefix triggers CAST comparison
            Assert.That(script, Does.Contain("CAST(Target.[EventTime] AS NVARCHAR(50))"));

            command.CommandText = script;
            command.ExecuteNonQuery();

            command.CommandText = $"SELECT CAST([EventTime] AS NVARCHAR(50)) FROM [dbo].[{tableName}] WHERE [Id] = 1";
            var result = command.ExecuteScalar()?.ToString();
            Assert.That(result, Does.Contain("+05:30"));
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS [dbo].[{tableName}]";
            command.ExecuteNonQuery();
        }
    }

    [Test]
    public void BuildMergeScript_DateTimeOffsetColumn_UpdateChangedValue()
    {
        using var command = _connection.CreateCommand();
        var tableName = $"_test_dto_upd_{Guid.NewGuid():N}"[..40];

        try
        {
            command.CommandText = $@"
CREATE TABLE [dbo].[{tableName}] (
    [Id] INT PRIMARY KEY,
    [EventTime] DATETIMEOFFSET NOT NULL
);
INSERT INTO [dbo].[{tableName}] VALUES (1, '2024-01-01T00:00:00+00:00');";
            command.ExecuteNonQuery();

            var tableData = @"[{""Id"":1,""EventTime"":""2024-06-15T10:30:00.0000000-07:00""}]";
            var script = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, command,
                "dbo", tableName, tableData, "[Id]",
                mergeUpdate: true, mergeDelete: false, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null);

            command.CommandText = script;
            command.ExecuteNonQuery();

            command.CommandText = $"SELECT CAST([EventTime] AS NVARCHAR(50)) FROM [dbo].[{tableName}] WHERE [Id] = 1";
            var result = command.ExecuteScalar()?.ToString();
            Assert.That(result, Does.Contain("-07:00"));
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS [dbo].[{tableName}]";
            command.ExecuteNonQuery();
        }
    }

    #endregion

    #region VARBINARY Tests

    [Test]
    public void BuildMergeScript_VarbinaryColumn_RoundTrip()
    {
        using var command = _connection.CreateCommand();
        var tableName = $"_test_vbin_{Guid.NewGuid():N}"[..40];

        try
        {
            command.CommandText = $@"
CREATE TABLE [dbo].[{tableName}] (
    [Id] INT PRIMARY KEY,
    [Data] VARBINARY(100) NOT NULL
);
INSERT INTO [dbo].[{tableName}] VALUES (1, 0x48454C4C4F);";
            command.ExecuteNonQuery();

            // Extract data using FOR JSON AUTO (hex encoding)
            command.CommandText = $"SELECT CAST((SELECT [Id], [Data] FROM [dbo].[{tableName}] FOR JSON AUTO) AS NVARCHAR(MAX))";
            var tableData = command.ExecuteScalar()?.ToString();

            var script = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, command,
                "dbo", tableName, tableData, "[Id]",
                mergeUpdate: true, mergeDelete: true, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null);

            // Delete existing and re-insert via merge
            command.CommandText = $"DELETE FROM [dbo].[{tableName}]";
            command.ExecuteNonQuery();

            command.CommandText = script;
            command.ExecuteNonQuery();

            command.CommandText = $"SELECT CONVERT(VARCHAR(20), [Data], 1) FROM [dbo].[{tableName}] WHERE [Id] = 1";
            var result = command.ExecuteScalar()?.ToString();
            Assert.That(result, Is.EqualTo("0x48454C4C4F").IgnoreCase);
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS [dbo].[{tableName}]";
            command.ExecuteNonQuery();
        }
    }

    #endregion

    #region Quoted Identifier Tests

    [Test]
    public void BuildMergeScript_TableNameWithSingleQuote_GeneratesValidScript()
    {
        using var command = _connection.CreateCommand();
        var tableName = $"zz't_{Guid.NewGuid():N}";

        try
        {
            command.CommandText = $@"
CREATE TABLE [dbo].[{tableName.Replace("]", "]]")}] (
    [Id] INT PRIMARY KEY,
    [Name] NVARCHAR(50) NOT NULL
)";
            command.ExecuteNonQuery();

            var tableData = @"[{""Id"":1,""Name"":""Alpha""}]";
            var script = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, command,
                "dbo", tableName, tableData, "[Id]",
                mergeUpdate: true, mergeDelete: false, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null);

            Assert.That(script, Is.Not.Null);
            Assert.That(script, Does.Contain("MERGE INTO"));
            Assert.That(script, Does.Contain("[Name]"));

            command.CommandText = script;
            command.ExecuteNonQuery();

            command.CommandText = $"SELECT [Name] FROM [dbo].[{tableName.Replace("]", "]]")}] WHERE [Id] = 1";
            Assert.That(command.ExecuteScalar()?.ToString(), Is.EqualTo("Alpha"));
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS [dbo].[{tableName.Replace("]", "]]")}]";
            command.ExecuteNonQuery();
        }
    }

    // A ledger or memory-optimized table is delivered through a #temp copy of the rows, and a #temp table's column names
    // follow the SERVER's collation. So on a case-sensitive server, a key column named in another case than the table's
    // failed against the copy even though the case-insensitive database itself accepted it. Reds only on such a server.
    [Test]
    public void BuildMergeScript_LedgerTable_KeyColumnNamedInAnotherCase_DeliversOnACaseInsensitiveDatabase()
    {
        using var master = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(
            new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(FixtureSetup.GetMainDbConnectionString()) { InitialCatalog = "master" }.ConnectionString);
        master.Open();
        using var masterCmd = master.CreateCommand();
        masterCmd.CommandText = "SELECT CAST(PARSENAME(CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR(50)), 4) AS INT)";
        if (Convert.ToInt32(masterCmd.ExecuteScalar()) < 16) Assert.Ignore("Ledger tables need SQL Server 2022.");

        var db = $"MergeCi_Test_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}"[..37];
        masterCmd.CommandText = $"CREATE DATABASE [{db}] COLLATE Latin1_General_CI_AS";
        masterCmd.ExecuteNonQuery();
        try
        {
            using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(
                new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(FixtureSetup.GetMainDbConnectionString()) { InitialCatalog = db }.ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE dbo.L (Id INT NOT NULL PRIMARY KEY, V NVARCHAR(20) NULL) WITH (LEDGER = ON (APPEND_ONLY = ON)); " +
                              "INSERT dbo.L (Id, V) VALUES (1, 'a');";
            cmd.ExecuteNonQuery();

            cmd.CommandText = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, cmd, "dbo", "L",
                @"[{""Id"":1,""V"":""a""},{""Id"":2,""V"":""c""}]", "[id]",
                mergeUpdate: true, mergeDelete: false, disableTriggers: false, tokenizeScripts: false, mergeFilter: null);
            cmd.ExecuteNonQuery();

            cmd.CommandText = "SELECT STRING_AGG(CONCAT(Id, ':', V), ',') WITHIN GROUP (ORDER BY Id) FROM dbo.L";
            Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("1:a,2:c"));
        }
        finally
        {
            Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
            TestDatabaseDrop.Drop(masterCmd, db);
        }
    }

    // Rows that wait on a foreign key are delivered by a second, insert-only pass. SQL Server refuses MERGE on a
    // memory-optimized table, so that pass failed on a table the first pass delivers.
    [TestCase("memory-optimized")]
    [TestCase("append-only ledger")]
    public void DeferredPass_InsertsTheMissingRows_OnATableMergeCannotTarget(string kind)
    {
        using var master = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(
            new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(FixtureSetup.GetMainDbConnectionString()) { InitialCatalog = "master" }.ConnectionString);
        master.Open();
        using var masterCmd = master.CreateCommand();
        masterCmd.CommandText = "SELECT CAST(PARSENAME(CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR(50)), 4) AS INT), CONVERT(INT, ISNULL(SERVERPROPERTY('IsXTPSupported'), 0))";
        using (var reader = masterCmd.ExecuteReader())
        {
            reader.Read();
            if (kind == "append-only ledger" && reader.GetInt32(0) < 16) Assert.Ignore("Ledger tables need SQL Server 2022.");
            if (kind == "memory-optimized" && (reader.GetInt32(0) < 13 || reader.GetInt32(1) != 1))
                Assert.Ignore("Memory-optimized tables with foreign keys need SQL Server 2016 and an edition that supports them.");
        }

        var db = $"DeferMo_Test_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}"[..37];
        masterCmd.CommandText = $"CREATE DATABASE [{db}]";
        masterCmd.ExecuteNonQuery();
        try
        {
            if (kind == "memory-optimized")
            {
                masterCmd.CommandText = $@"
DECLARE @v_Path NVARCHAR(500) = (SELECT LEFT(physical_name, LEN(physical_name) - CHARINDEX('\', REVERSE(physical_name)) + 1) FROM sys.master_files WHERE database_id = DB_ID('{db}') AND file_id = 1);
ALTER DATABASE [{db}] ADD FILEGROUP [MOD_fg] CONTAINS MEMORY_OPTIMIZED_DATA;
EXEC('ALTER DATABASE [{db}] ADD FILE (NAME = ''MOD_container'', FILENAME = ''' + @v_Path + '{db}_MOD'') TO FILEGROUP [MOD_fg]');";
                masterCmd.ExecuteNonQuery();
            }
            using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(
                new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(FixtureSetup.GetMainDbConnectionString()) { InitialCatalog = db }.ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            var options = kind == "memory-optimized" ? "WITH (MEMORY_OPTIMIZED = ON, DURABILITY = SCHEMA_AND_DATA)" : "WITH (LEDGER = ON (APPEND_ONLY = ON))";
            var key = kind == "memory-optimized" ? "PRIMARY KEY NONCLUSTERED" : "PRIMARY KEY";
            cmd.CommandText = $"CREATE TABLE dbo.T (Id INT NOT NULL {key}, ParentId INT NULL, V NVARCHAR(20) NULL) {options}; " +
                              "INSERT dbo.T (Id, ParentId, V) VALUES (1, NULL, N'a');";
            cmd.ExecuteNonQuery();

            cmd.CommandText = DeferredMergeBuilder.Build(new MergeScriptHelperAdapter(Platform.SqlServer), cmd, "SqlServer", "dbo", "T",
                @"[{""Id"":1,""ParentId"":null,""V"":""a""},{""Id"":2,""ParentId"":1,""V"":""b""}]", "[Id]",
                disableTriggers: false, deferredColumns: ["ParentId"]);
            cmd.ExecuteNonQuery();

            cmd.CommandText = "SELECT STRING_AGG(CONCAT(Id, ':', COALESCE(CAST(ParentId AS NVARCHAR(10)), '-'), ':', V), ',') WITHIN GROUP (ORDER BY Id) FROM dbo.T";
            Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("1:-:a,2:-:b"),
                "the missing row arrives with its waiting key left NULL for the second pass; the existing row is untouched");
        }
        finally
        {
            Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
            TestDatabaseDrop.Drop(masterCmd, db);
        }
    }

    #endregion

    #region XML Tests

    [Test]
    public void BuildMergeScript_XmlColumn_RoundTrip()
    {
        using var command = _connection.CreateCommand();
        var tableName = $"_test_xml_{Guid.NewGuid():N}"[..40];

        try
        {
            command.CommandText = $@"
CREATE TABLE [dbo].[{tableName}] (
    [Id] INT PRIMARY KEY,
    [Config] XML NOT NULL
)";
            command.ExecuteNonQuery();

            var tableData = @"[{""Id"":1,""Config"":""<root><item key=\""a\"" value=\""1\"" \/><\/root>""}]";
            var script = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, command,
                "dbo", tableName, tableData, "[Id]",
                mergeUpdate: true, mergeDelete: false, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null);

            // XML uses X[ prefix with NVARCHAR(MAX) cast comparison
            Assert.That(script, Does.Contain("CAST(Target.[Config] AS NVARCHAR(MAX))"));

            command.CommandText = script;
            command.ExecuteNonQuery();

            command.CommandText = $"SELECT CAST([Config] AS NVARCHAR(MAX)) FROM [dbo].[{tableName}] WHERE [Id] = 1";
            var result = command.ExecuteScalar()?.ToString();
            Assert.That(result, Does.Contain("<root>"));
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS [dbo].[{tableName}]";
            command.ExecuteNonQuery();
        }
    }

    #endregion

    #region TIME / DATETIME2 Precision Tests (C1-0f — is the C1-0a defect reachable here?)

    // MergeScriptHelper's JSON/XML parse-type builders omit a precision qualifier for TIME (they only
    // add one for DATETIME2) — the same shape as the drift-comparison defect C1-0a fixed. These three
    // tests are the empirical answer to "is it reachable here too": a TIME(3)/DATETIME2(3) delivery must
    // round-trip its sub-second value exactly through both the JSON (OPENJSON) and XML (.value()) shred
    // paths. All three pass because SQL Server treats an unqualified TIME/DATETIME2 as precision 7 — the
    // shred widens rather than narrows, and the real destination column governs the final rounding.

    [Test]
    public void BuildMergeScript_TimeColumnWithPrecision_JsonEncoding_RoundTripsSubSecondPrecision()
    {
        using var command = _connection.CreateCommand();
        var tableName = $"_test_time_json_{Guid.NewGuid():N}"[..40];

        try
        {
            command.CommandText = $@"
CREATE TABLE [dbo].[{tableName}] (
    [Id] INT PRIMARY KEY,
    [T] TIME(3) NOT NULL
)";
            command.ExecuteNonQuery();

            var tableData = @"[{""Id"":1,""T"":""13:45:30.123""}]";
            var script = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, command,
                "dbo", tableName, tableData, "[Id]",
                mergeUpdate: true, mergeDelete: false, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null);

            command.CommandText = script;
            command.ExecuteNonQuery();

            command.CommandText = $"SELECT CAST([T] AS VARCHAR(20)) FROM [dbo].[{tableName}] WHERE [Id] = 1";
            var result = command.ExecuteScalar()?.ToString();
            Assert.That(result, Is.EqualTo("13:45:30.123"),
                "A TIME(3) delivery must preserve its declared millisecond precision exactly.");
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS [dbo].[{tableName}]";
            command.ExecuteNonQuery();
        }
    }

    // The XML shred cast each value to the column's declared type, which .value() refuses when it is an alias type -- and
    // named one outside dbo twice over (s.s.type) -- so an XML delivery into an alias-typed column failed. It casts to the
    // base type now; OPENJSON accepted the alias all along, and is covered so that stays true.
    [TestCase("Json")]
    [TestCase("Xml")]
    public void BuildMergeScript_AnAliasTypedColumn_DeliversItsValue(string encoding)
    {
        using var command = _connection.CreateCommand();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var schema = $"_test_alias_{suffix}";
        var dboType = $"_test_alias_code_{suffix}";
        var tableName = $"_test_alias_tbl_{suffix}";

        try
        {
            command.CommandText = $"CREATE SCHEMA [{schema}]";
            command.ExecuteNonQuery();
            command.CommandText = $"CREATE TYPE [{schema}].[Code] FROM NVARCHAR(10) NULL; CREATE TYPE [dbo].[{dboType}] FROM VARCHAR(5) NULL";
            command.ExecuteNonQuery();
            command.CommandText = $"CREATE TABLE [{schema}].[{tableName}] ([Id] INT PRIMARY KEY, [C] [{schema}].[Code] NULL, [D] [dbo].[{dboType}] NULL)";
            command.ExecuteNonQuery();

            var data = encoding == "Xml"
                ? "<rows><row><c n=\"Id\">1</c><c n=\"C\">abc</c><c n=\"D\">xy</c></row></rows>"
                : "[{\"Id\":1,\"C\":\"abc\",\"D\":\"xy\"}]";
            var script = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, command,
                schema, tableName, data, "[Id]",
                mergeUpdate: true, mergeDelete: false, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null, contentEncoding: encoding);
            command.CommandText = script;
            command.ExecuteNonQuery();

            command.CommandText = $"SELECT [C] + '|' + [D] FROM [{schema}].[{tableName}] WHERE [Id] = 1";
            Assert.That(command.ExecuteScalar()?.ToString(), Is.EqualTo("abc|xy"));
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS [{schema}].[{tableName}]; " +
                                  $"IF TYPE_ID('{schema}.Code') IS NOT NULL DROP TYPE [{schema}].[Code]; " +
                                  $"IF TYPE_ID('dbo.{dboType}') IS NOT NULL DROP TYPE [dbo].[{dboType}]; " +
                                  $"IF SCHEMA_ID('{schema}') IS NOT NULL DROP SCHEMA [{schema}]";
            command.ExecuteNonQuery();
        }
    }

    [Test]
    public void BuildMergeScript_TimeColumnWithPrecision_XmlEncoding_RoundTripsSubSecondPrecision()
    {
        using var command = _connection.CreateCommand();
        var tableName = $"_test_time_xml_{Guid.NewGuid():N}"[..40];

        try
        {
            command.CommandText = $@"
CREATE TABLE [dbo].[{tableName}] (
    [Id] INT PRIMARY KEY,
    [T] TIME(3) NOT NULL
)";
            command.ExecuteNonQuery();

            var tableData = "<rows><row><c n=\"Id\">1</c><c n=\"T\">13:45:30.123</c></row></rows>";
            var script = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, command,
                "dbo", tableName, tableData, "[Id]",
                mergeUpdate: true, mergeDelete: false, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null, contentEncoding: "Xml");

            command.CommandText = script;
            command.ExecuteNonQuery();

            command.CommandText = $"SELECT CAST([T] AS VARCHAR(20)) FROM [dbo].[{tableName}] WHERE [Id] = 1";
            var result = command.ExecuteScalar()?.ToString();
            Assert.That(result, Is.EqualTo("13:45:30.123"),
                "The XML (.value()) shred path must preserve TIME(3) precision exactly, same as the JSON path.");
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS [dbo].[{tableName}]";
            command.ExecuteNonQuery();
        }
    }

    [Test]
    public void BuildMergeScript_DateTime2ColumnWithPrecision_RoundTripsSubSecondPrecision()
    {
        using var command = _connection.CreateCommand();
        var tableName = $"_test_dt2_{Guid.NewGuid():N}"[..40];

        try
        {
            command.CommandText = $@"
CREATE TABLE [dbo].[{tableName}] (
    [Id] INT PRIMARY KEY,
    [T] DATETIME2(3) NOT NULL
)";
            command.ExecuteNonQuery();

            var tableData = @"[{""Id"":1,""T"":""2024-06-15T10:30:00.123""}]";
            var script = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, command,
                "dbo", tableName, tableData, "[Id]",
                mergeUpdate: true, mergeDelete: false, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null);

            command.CommandText = script;
            command.ExecuteNonQuery();

            command.CommandText = $"SELECT CONVERT(VARCHAR(30), [T], 121) FROM [dbo].[{tableName}] WHERE [Id] = 1";
            var result = command.ExecuteScalar()?.ToString();
            Assert.That(result, Is.EqualTo("2024-06-15 10:30:00.123"),
                "DATETIME2(3) already carries an explicit precision case — this pins that it stays correct.");
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS [dbo].[{tableName}]";
            command.ExecuteNonQuery();
        }
    }

    #endregion

    // #567: an object or array value read through OPENJSON into a plain column was NULL, at exit 0, and below level 130 the
    // XML path wrote it indented. Both now deliver the value's compact JSON text; a string still arrives as the string.
    [TestCase("Json")]
    [TestCase("Xml")]
    public void BuildMergeScript_AnObjectOrArrayValue_ArrivesAsCompactJsonText(string encoding)
    {
        using var command = _connection.CreateCommand();
        var tableName = $"_test_jsonval_{Guid.NewGuid():N}"[..40];
        try
        {
            command.CommandText = $"CREATE TABLE [dbo].[{tableName}] ([Id] INT PRIMARY KEY, [Doc] NVARCHAR(MAX) NULL, [Note] VARCHAR(200) NULL)";
            command.ExecuteNonQuery();

            // Laid out the way a hand-written content file is, with whitespace between tokens and inside a string.
            var payload = "[\n  { \"Id\": 1, \"Doc\": { \"a\": 1, \"b\": [ 1, 2.50 ] }, \"Note\": [ 1, 2 ] },\n"
                          + "  { \"Id\": 2, \"Doc\": \"plain  text\", \"Note\": \"x\" },\n"
                          + "  { \"Id\": 3, \"Doc\": null, \"Note\": null }\n]";
            var tableData = encoding == "Xml" ? MergeScriptHelper.JsonPayloadToXml(payload) : payload;

            // Twice: the second run goes through the update comparison with the rows already in place.
            for (var run = 0; run < 2; run++)
            {
                command.CommandText = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, command, "dbo", tableName, tableData, "[Id]",
                    mergeUpdate: true, mergeDelete: false, disableTriggers: false, tokenizeScripts: false, mergeFilter: null,
                    contentEncoding: encoding);
                command.ExecuteNonQuery();
            }

            string Read(int id, string column)
            {
                command.CommandText = $"SELECT [{column}] FROM [dbo].[{tableName}] WHERE [Id] = {id}";
                var value = command.ExecuteScalar();
                return value is DBNull or null ? null : value.ToString();
            }

            Assert.Multiple(() =>
            {
                Assert.That(Read(1, "Doc"), Is.EqualTo("{\"a\":1,\"b\":[1,2.50]}"), "an object, compact, its number as written");
                Assert.That(Read(1, "Note"), Is.EqualTo("[1,2]"), "an array into a non-MAX column");
                Assert.That(Read(2, "Doc"), Is.EqualTo("plain  text"), "a string arrives as the string, spaces kept");
                Assert.That(Read(2, "Note"), Is.EqualTo("x"));
                Assert.That(Read(3, "Doc"), Is.Null);
                Assert.That(Read(3, "Note"), Is.Null);
            });
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS [dbo].[{tableName}]";
            command.ExecuteNonQuery();
        }
    }
}
