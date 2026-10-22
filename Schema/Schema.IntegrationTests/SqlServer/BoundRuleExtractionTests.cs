// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using Microsoft.Data.SqlClient;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.SqlServer;

/// <summary>
/// A bound rule or default (sp_bindrule / sp_bindefault) is not modelled. Extraction used to write the bound default's
/// CREATE DEFAULT text as the column's Default, which failed the table's create; now both generators leave the column
/// without it and say so in a warning, and the extracted table deploys. Uses the suite's kindled database and
/// isolates by name.
/// </summary>
[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public class BoundRuleExtractionTests
{
    private const string Table = "BrBound";
    private const string Copy = "BrBoundCopy";
    private IDbConnection _connection;
    private readonly List<string> _messages = [];

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _connection = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        _connection.Open();
        ((SqlConnection)_connection).InfoMessage += (_, e) =>
        {
            foreach (SqlError error in e.Errors) _messages.Add(error.Message);
        };
        DropOwnObjects();
        Exec("CREATE RULE dbo.BrPositive AS @v > 0");
        Exec("CREATE DEFAULT dbo.BrZero AS 0");
        Exec($"CREATE TABLE dbo.{Table} ([Id] INT NOT NULL CONSTRAINT PK_{Table} PRIMARY KEY, [Qty] INT NULL, [Amt] INT NULL CONSTRAINT DF_{Table}_Amt DEFAULT (1))");
        Exec($"EXEC sp_bindrule 'dbo.BrPositive', 'dbo.{Table}.Qty'");
        Exec($"EXEC sp_bindefault 'dbo.BrZero', 'dbo.{Table}.Qty'");
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        if (_connection == null) return;
        try { DropOwnObjects(); }
        finally { _connection.Close(); _connection.Dispose(); }
    }

    [SetUp]
    public void SetUp() => _messages.Clear();

    private void DropOwnObjects() => Exec($@"IF OBJECT_ID('dbo.{Copy}', 'U') IS NOT NULL DROP TABLE dbo.{Copy};
                                            IF OBJECT_ID('dbo.{Table}', 'U') IS NOT NULL
                                            BEGIN
                                              EXEC sp_unbindrule 'dbo.{Table}.Qty';
                                              EXEC sp_unbindefault 'dbo.{Table}.Qty';
                                              DROP TABLE dbo.{Table};
                                            END
                                            IF OBJECT_ID('dbo.BrPositive', 'R') IS NOT NULL DROP RULE dbo.BrPositive;
                                            IF OBJECT_ID('dbo.BrZero', 'D') IS NOT NULL DROP DEFAULT dbo.BrZero;
                                            DELETE FROM SchemaSmith.ProductOwnership WHERE ProductName = 'BrTest';");

    private void Exec(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private string Extract(string generator)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = $"EXEC SchemaSmith.{generator} @p_Schema = 'dbo', @p_Table = '{Table}'";
        using var reader = cmd.ExecuteReader();
        var sb = new StringBuilder();
        while (reader.Read())
            if (!reader.IsDBNull(0)) sb.Append(reader.GetValue(0));
        return sb.ToString();
    }

    private IEnumerable<string> Warnings() => _messages.Where(m => m.StartsWith("WARNING: "));

    [Test]
    public void TheJsonTier_LeavesTheBoundRuleAndDefaultOut_AndWarnsForEach()
    {
        var json = Extract("GenerateTableJSON");
        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Not.Contain("CREATE DEFAULT"), "the bound default's text is not the column's Default:\n" + json);
            Assert.That(json, Does.Contain("\"Default\": \"1\""), "the column's own default constraint is unaffected:\n" + json);
            Assert.That(Warnings().ToList(), Is.EqualTo(new[]
            {
                "WARNING: Column [dbo].[BrBound].[Qty] has the bound default [dbo].[BrZero] (sp_bindefault), which is not modelled: the column is written without it.",
                "WARNING: Column [dbo].[BrBound].[Qty] has the bound rule [dbo].[BrPositive] (sp_bindrule), which is not modelled: the column is written without it.",
            }), "messages: " + string.Join(" | ", _messages));
        });
    }

    [Test]
    public void TheXmlTier_LeavesTheBoundDefaultOut_AndWarnsToo()
    {
        // Its own database: kindling the XML encoding replaces the helper procedures.
        var db = $"BrBoundXml_{Guid.NewGuid():N}"[..40];
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        conn.Open();
        var messages = new List<string>();
        ((SqlConnection)conn).InfoMessage += (_, e) =>
        {
            foreach (SqlError error in e.Errors) messages.Add(error.Message);
        };
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        try
        {
            conn.ChangeDatabase("master");
            Run(cmd, $"CREATE DATABASE [{db}]");
            conn.ChangeDatabase(db);
            ForgeKindler.KindleTheForge(cmd, Platform.SqlServer, forceReKindle: true, IngestEncoding.Xml, TargetVersionDetector.Detect(cmd, Platform.SqlServer).ServerComparable, "warn");
            Run(cmd, "CREATE RULE dbo.BrPositive AS @v > 0");
            Run(cmd, "CREATE DEFAULT dbo.BrZero AS 0");
            Run(cmd, $"CREATE TABLE dbo.{Table} ([Id] INT NOT NULL CONSTRAINT PK_{Table} PRIMARY KEY, [Qty] INT NULL, [Amt] INT NULL CONSTRAINT DF_{Table}_Amt DEFAULT (1))");
            Run(cmd, $"EXEC sp_bindrule 'dbo.BrPositive', 'dbo.{Table}.Qty'");
            Run(cmd, $"EXEC sp_bindefault 'dbo.BrZero', 'dbo.{Table}.Qty'");
            messages.Clear();

            cmd.CommandText = $"EXEC SchemaSmith.GenerateTableXml @p_Schema = 'dbo', @p_Table = '{Table}'";
            var sb = new StringBuilder();
            using (var reader = cmd.ExecuteReader())
                while (reader.Read())
                    if (!reader.IsDBNull(0)) sb.Append(reader.GetValue(0));
            var xml = sb.ToString();
            Assert.Multiple(() =>
            {
                Assert.That(xml, Does.Not.Contain("CREATE DEFAULT"), xml);
                Assert.That(xml, Does.Contain("<Default>1</Default>"), xml);
                Assert.That(messages.Count(m => m.StartsWith("WARNING: Column [dbo].[BrBound].[Qty] has the bound ")), Is.EqualTo(2), "messages: " + string.Join(" | ", messages));
            });
        }
        finally
        {
            conn.ChangeDatabase("master");
            TestDatabaseDrop.Drop(cmd, db);
        }
    }

    private static void Run(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Test]
    public void TheExtractedTable_Deploys_WithTheColumnButNoBoundDefault()
    {
        var json = Extract("GenerateTableJSON").Replace($"[{Table}]", $"[{Copy}]").Replace($"PK_{Table}", $"PK_{Copy}").Replace($"DF_{Table}_Amt", $"DF_{Copy}_Amt");
        if (!json.TrimStart().StartsWith("[")) json = "[" + json + "]";
        Exec($"EXEC SchemaSmith.TableQuench @ProductName = 'BrTest', @TableDefinitions = N'{json.Replace("'", "''")}', @DropTablesRemovedFromProduct = 0");

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $@"SELECT c.[name] + ':' + ISNULL(OBJECT_DEFINITION(c.default_object_id), '-') FROM sys.columns c WHERE c.[object_id] = OBJECT_ID('dbo.{Copy}') ORDER BY c.[name]";
        using var reader = cmd.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read()) columns.Add(reader.GetString(0));
        Assert.That(columns, Is.EqualTo(new[] { "Amt:((1))", "Id:-", "Qty:-" }));
    }
}
