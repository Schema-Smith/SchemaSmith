// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using NUnit.Framework;
using Schema.DataAccess;
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
}
