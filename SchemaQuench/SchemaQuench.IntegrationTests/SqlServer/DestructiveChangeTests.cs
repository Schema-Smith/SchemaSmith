// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Linq;
using NUnit.Framework;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.SqlServer;

[Category("SqlServer")]
[Category("Integration")]
[NonParallelizable]
public class DestructiveChangeTests : DestructiveChangeSharedTests
{
    protected override Platform Platform => Platform.SqlServer;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => TestConnection("master");
    protected override string ConfigPrefix => "SqlServer";
    protected override string ProductName => "DestructiveChangeProduct";

    protected override string TableJson(string table, params string[] columns) => $$"""
        { "Schema": "[dbo]", "Name": "[{{table}}]",
          "Columns": [ { "Name": "[id]", "DataType": "INT IDENTITY(1, 1)", "Nullable": false }{{string.Concat(columns.Select(c => $", {{ \"Name\": \"[{c}]\", \"DataType\": \"INT\", \"Nullable\": true }}"))}} ],
          "Indexes": [ { "Name": "[PK_{{table}}]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[id]" } ] }
        """;
    protected override string TableFileName(string table) => $"dbo.{table}.json";
    protected override string Qualified(string table) => $"dbo.{table}";
    protected override string Insert(string table, string column, int value) => $"INSERT dbo.{table} ({column}) VALUES ({value})";
    protected override string Drop(string table) => $"DROP TABLE IF EXISTS dbo.{table}";
    protected override bool ColumnExists(string table, string column) => Count($"SELECT COUNT(*) FROM sys.columns WHERE [object_id] = OBJECT_ID('dbo.{table}') AND [name] = '{column}'") == 1;
    protected override long SumOf(string table, string column) => Convert.ToInt64(Scalar($"SELECT SUM({column}) FROM dbo.{table}"));
    protected override long RowCount(string table) => Count($"SELECT COUNT(*) FROM dbo.{table}");

    // H3 slice B: a type change that loses values silently, and a plain column replaced by a computed or identity one.

    private const string Typed = "dc_typed";

    private void DeployTyped(string type, string value)
    {
        Exec($"DROP TABLE IF EXISTS dbo.{Typed}");
        WritePackage("");
        WriteFile("Tables", $"dbo.{Typed}.json", TypedJson(type));
        Deploy();
        Exec($"INSERT dbo.{Typed} (v) VALUES ({value})");
    }

    private static string TypedJson(string type, string extra = "") => $$"""
        { "Schema": "[dbo]", "Name": "[{{Typed}}]",
          "Columns": [ { "Name": "[id]", "DataType": "INT IDENTITY(1, 1)", "Nullable": false },
                       { "Name": "[v]", "DataType": "{{type}}", "Nullable": true{{extra}} } ],
          "Indexes": [ { "Name": "[PK_{{Typed}}]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[id]" } ] }
        """;

    private string ValueOfV() => Scalar($"SELECT CONVERT(NVARCHAR(100), v, 121) FROM dbo.{Typed}")?.ToString();

    [TestCase("DECIMAL(18,4)", "1.2345", "DECIMAL(18,2)")]
    [TestCase("DATETIME2(7)", "'2020-01-01 10:00:00.1234567'", "DATE")]
    [TestCase("NVARCHAR(20)", "N'\u03A9mega'", "VARCHAR(20)")]
    [TestCase("FLOAT", "1.75", "INT")]
    public void UnderFail_ATypeChangeThatLosesValuesIsRefusedAndTheValueKept(string from, string value, string to)
    {
        DeployTyped(from, value);
        var before = ValueOfV();
        WriteFile("Tables", $"dbo.{Typed}.json", TypedJson(to));
        try
        {
            var result = Deploy(expectSuccess: false, settings: new System.Collections.Generic.Dictionary<string, string> { ["Target:DestructiveChangePolicy"] = "fail" });
            Assert.Multiple(() =>
            {
                Assert.That(result.Failure?.Error, Does.Contain($"dbo.{Typed}.v").And.Contain(to), result.Failure?.Error);
                Assert.That(ValueOfV(), Is.EqualTo(before), "the value is untouched");
            });
        }
        finally
        {
            Exec($"DROP TABLE IF EXISTS dbo.{Typed}");
        }
    }

    [TestCase("INT", "5", "BIGINT")]
    [TestCase("VARCHAR(10)", "'abc'", "VARCHAR(50)")]
    [TestCase("DECIMAL(10,2)", "1.25", "DECIMAL(12,2)")]
    [TestCase("VARCHAR(10)", "'abc'", "NVARCHAR(10)")]
    public void UnderFail_AWideningTypeChangeGoesAhead(string from, string value, string to)
    {
        DeployTyped(from, value);
        WriteFile("Tables", $"dbo.{Typed}.json", TypedJson(to));
        try
        {
            var result = Deploy(settings: new System.Collections.Generic.Dictionary<string, string> { ["Target:DestructiveChangePolicy"] = "fail" });
            Assert.That(result.Audit.Where(r => r.Action == "destructive"), Is.Empty);
        }
        finally
        {
            Exec($"DROP TABLE IF EXISTS dbo.{Typed}");
        }
    }

    [Test]
    public void UnderFail_APlainColumnReplacedByAComputedOneIsRefused()
    {
        DeployTyped("INT", "41");
        WriteFile("Tables", $"dbo.{Typed}.json", TypedJson("INT", ", \"ComputedExpression\": \"[id] + 1\""));
        try
        {
            var result = Deploy(expectSuccess: false, settings: new System.Collections.Generic.Dictionary<string, string> { ["Target:DestructiveChangePolicy"] = "fail" });
            Assert.Multiple(() =>
            {
                Assert.That(result.Failure?.Error, Does.Contain($"dbo.{Typed}.v").And.Contain("replaced"), result.Failure?.Error);
                Assert.That(ValueOfV(), Is.EqualTo("41"), "the stored value is kept");
            });
        }
        finally
        {
            Exec($"DROP TABLE IF EXISTS dbo.{Typed}");
        }
    }

    // The classifier itself, against what SQL Server was seen to do with data that does not fit (H3 P2, 2022).
    [TestCase("DECIMAL(18,4)", "DECIMAL(18,2)", "loses")]
    [TestCase("DECIMAL(18,2)", "DECIMAL(10,2)", "fails")]
    [TestCase("DECIMAL(18,2)", "DECIMAL(20,4)", "none")]
    [TestCase("DECIMAL(10,2)", "INT", "loses")]
    [TestCase("DATETIME2(7)", "DATETIME2(0)", "loses")]
    [TestCase("DATETIME2(7)", "DATETIME", "loses")]
    [TestCase("DATETIME", "DATE", "loses")]
    [TestCase("DATE", "DATETIME2", "none")]
    [TestCase("TIME(7)", "TIME(0)", "loses")]
    [TestCase("DATETIMEOFFSET", "DATETIME2", "loses")]
    [TestCase("FLOAT", "INT", "loses")]
    [TestCase("FLOAT", "REAL", "loses")]
    [TestCase("REAL", "FLOAT", "none")]
    [TestCase("INT", "BIGINT", "none")]
    [TestCase("BIGINT", "INT", "fails")]
    [TestCase("INT", "DECIMAL(10,2)", "fails")]
    [TestCase("BIGINT", "FLOAT", "loses")]
    [TestCase("INT", "VARCHAR(2)", "loses")]
    [TestCase("INT", "VARCHAR(20)", "none")]
    [TestCase("VARCHAR(10)", "VARCHAR(5)", "fails")]
    [TestCase("VARCHAR(10)", "INT", "fails")]
    [TestCase("NVARCHAR(10)", "VARCHAR(10)", "loses")]
    [TestCase("VARCHAR(10)", "NVARCHAR(10)", "none")]
    [TestCase("VARCHAR(10)", "CHAR(10)", "none")]
    [TestCase("INT", "BIT", "loses")]
    [TestCase("MONEY", "INT", "loses")]
    [TestCase("VARBINARY(10)", "VARBINARY(2)", "fails")]
    [TestCase("VARCHAR(50)", "VARCHAR(MAX)", "none")]
    [TestCase("NVARCHAR(MAX)", "NVARCHAR(100)", "fails")]
    [TestCase("SYSNAME", "NVARCHAR(200)", "none")]
    [TestCase("INT", "INT IDENTITY(1, 1)", "none")]
    [TestCase("NVARCHAR(50)", "NVARCHAR(100) COLLATE Latin1_General_CI_AS", "none")]
    [TestCase("XML", "NVARCHAR(MAX)", "loses")]
    public void TheClassifierMatchesWhatTheEngineDoes(string from, string to, string expected)
    {
        Assert.That(Scalar($"SELECT SchemaSmith.fn_ColumnChangeLoss('{from}', '{to}')")?.ToString(), Is.EqualTo(expected));
    }
}
