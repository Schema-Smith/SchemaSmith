// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.MySQL;

/// <summary>
/// H3 on MySQL and MariaDB: a type change that rounds or cuts values without an error, and a column whose generated status
/// changes, which the deploy drops and adds again. The demo servers run a strict sql_mode, so a narrowing that does not fit
/// refuses on its own; the non-strict answers are pinned on the classifier.
/// </summary>
public abstract class DestructiveChangeMySqlFamilyTests : DestructiveChangeSharedTests
{
    private const string Typed = "dc_typed";

    private void DeployTyped(string type, string value)
    {
        Exec($"DROP TABLE IF EXISTS `{MainDb}`.{Typed}");
        WritePackage("");
        WriteFile("Tables", $"{Typed}.json", TypedJson(type));
        Deploy();
        Exec($"INSERT INTO `{MainDb}`.{Typed} (v) VALUES ({value})");
    }

    private static string TypedJson(string type, string extra = "", string table = "", bool nullable = true) => $$"""
        { "Name": "{{Typed}}"{{table}},
          "Columns": [ { "Name": "id", "DataType": "INT", "Nullable": false, "AutoIncrement": true },
                       { "Name": "w", "DataType": "INT", "Nullable": true },
                       { "Name": "v", "DataType": "{{type}}", "Nullable": {{(nullable ? "true" : "false")}}{{extra}} } ],
          "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "id" } ] }
        """;

    private string ValueOfV() => Scalar($"SELECT CAST(v AS CHAR) FROM `{MainDb}`.{Typed}")?.ToString();

    private static Dictionary<string, string> Fail => new() { ["Target:DestructiveChangePolicy"] = "fail" };

    [TestCase("DECIMAL(18,4)", "1.2345", "DECIMAL(18,2)")]
    [TestCase("DATETIME(6)", "'2020-01-01 10:00:00.123456'", "DATE")]
    [TestCase("DOUBLE", "1.75", "INT")]
    [TestCase("BIGINT", "9007199254740993", "DOUBLE")]
    public void UnderFail_ATypeChangeThatLosesValuesIsRefusedAndTheValueKept(string from, string value, string to)
    {
        DeployTyped(from, value);
        var before = ValueOfV();
        WriteFile("Tables", $"{Typed}.json", TypedJson(to));
        try
        {
            var result = Deploy(expectSuccess: false, settings: Fail);
            Assert.Multiple(() =>
            {
                Assert.That(result.Failure?.Error, Does.Contain($"{MainDb}.{Typed}.v").And.Contain("without an error"), result.Failure?.Error);
                Assert.That(ValueOfV(), Is.EqualTo(before), "the value is untouched");
            });
        }
        finally
        {
            Exec($"DROP TABLE IF EXISTS `{MainDb}`.{Typed}");
        }
    }

    // A narrowing that a strict mode refuses for a value that does not fit loses nothing, so the gate lets it through and
    // the engine stops it; a widening loses nothing at all.
    [TestCase("INT", "5", "BIGINT")]
    [TestCase("VARCHAR(10)", "'abc'", "VARCHAR(50)")]
    [TestCase("DECIMAL(10,2)", "1.25", "DECIMAL(12,2)")]
    [TestCase("VARCHAR(10)", "'abc'", "VARCHAR(5)")]
    public void UnderFail_AChangeThatLosesNothingGoesAhead(string from, string value, string to)
    {
        DeployTyped(from, value);
        WriteFile("Tables", $"{Typed}.json", TypedJson(to));
        try
        {
            var result = Deploy(settings: Fail);
            Assert.That(result.Audit.Where(r => r.Action == "destructive"), Is.Empty);
        }
        finally
        {
            Exec($"DROP TABLE IF EXISTS `{MainDb}`.{Typed}");
        }
    }

    [TestCase("", ", \"GenerationExpression\": \"w + 1\", \"Generated\": \"STORED\"")]
    [TestCase(", \"GenerationExpression\": \"w + 40\", \"Generated\": \"STORED\"", "")]
    public void UnderFail_AColumnWhoseGeneratedStatusChangesIsRefused(string fromExtra, string toExtra)
    {
        Exec($"DROP TABLE IF EXISTS `{MainDb}`.{Typed}");
        WritePackage("");
        WriteFile("Tables", $"{Typed}.json", TypedJson("INT", fromExtra));
        Deploy();
        Exec(fromExtra == "" ? $"INSERT INTO `{MainDb}`.{Typed} (w, v) VALUES (1, 41)" : $"INSERT INTO `{MainDb}`.{Typed} (w) VALUES (1)");
        var before = ValueOfV();
        WriteFile("Tables", $"{Typed}.json", TypedJson("INT", toExtra));
        try
        {
            var result = Deploy(expectSuccess: false, settings: Fail);
            Assert.Multiple(() =>
            {
                Assert.That(result.Failure?.Error, Does.Contain($"{MainDb}.{Typed}.v").And.Contain("replaced"), result.Failure?.Error);
                Assert.That(ValueOfV(), Is.EqualTo(before), "the stored value is kept");
            });
        }
        finally
        {
            Exec($"DROP TABLE IF EXISTS `{MainDb}`.{Typed}");
        }
    }

    // STRICT_TRANS_TABLES is strict only for a transactional table: on MyISAM the engine clamps, cuts, zeroes and substitutes
    // instead of refusing, so a narrowing, a column made NOT NULL and a utf8 column moved to latin1 are all destructive there.
    [TestCase("VARCHAR(10)", "'abcdefg'", "VARCHAR(5)", "", true)]
    [TestCase("INT", "NULL", "INT", "", false)]
    [TestCase("VARCHAR(10)", "'Ωmega'", "VARCHAR(10)", ", \"CharacterSet\": \"latin1\"", true)]
    public void UnderFail_WithoutAStrictModeANarrowingIsRefusedAndTheValueKept(string from, string value, string to, string toExtra, bool toNullable)
    {
        const string myIsam = ", \"Engine\": \"MyISAM\"";
        Exec($"DROP TABLE IF EXISTS `{MainDb}`.{Typed}");
        WritePackage("");
        WriteFile("Tables", $"{Typed}.json", TypedJson(from, table: myIsam));
        Deploy();
        Exec($"INSERT INTO `{MainDb}`.{Typed} (v) VALUES ({value})");
        var before = ValueOfV();
        WriteFile("Tables", $"{Typed}.json", TypedJson(to, toExtra, myIsam, toNullable));
        try
        {
            var result = Deploy(expectSuccess: false, settings: Fail);
            Assert.Multiple(() =>
            {
                Assert.That(result.Failure?.Error, Does.Contain($"{MainDb}.{Typed}.v").And.Contain("without an error"), result.Failure?.Error);
                Assert.That(ValueOfV(), Is.EqualTo(before), "the value is untouched");
            });
        }
        finally
        {
            Exec($"DROP TABLE IF EXISTS `{MainDb}`.{Typed}");
        }
    }

    // The classifier itself, against what MySQL 8.0 and MariaDB 11.4 were seen to do with data that does not fit (H3 P2),
    // strict and not. The live side is spelled as the catalog spells it, the declared side as a package does.
    [TestCase("decimal(18,4)", "DECIMAL(18,2)", 1, "loses")]
    [TestCase("decimal(18,2)", "DECIMAL(10,2)", 1, "fails")]
    [TestCase("decimal(18,2)", "DECIMAL(10,2)", 0, "loses")]
    [TestCase("decimal(10,2)", "NUMERIC(12,2)", 0, "none")]
    [TestCase("decimal(10,2)", "INT", 1, "loses")]
    [TestCase("datetime(6)", "DATETIME", 1, "loses")]
    [TestCase("datetime", "DATE", 1, "loses")]
    [TestCase("date", "DATETIME", 0, "none")]
    [TestCase("datetime", "TIMESTAMP", 1, "fails")]
    [TestCase("timestamp(6)", "TIMESTAMP(0)", 1, "loses")]
    [TestCase("time(6)", "TIME", 1, "loses")]
    [TestCase("double", "INT", 1, "loses")]
    [TestCase("double", "FLOAT", 1, "loses")]
    [TestCase("float", "DOUBLE", 1, "none")]
    [TestCase("int(11)", "BIGINT", 1, "none")]
    [TestCase("bigint", "INT", 1, "fails")]
    [TestCase("bigint(20)", "INT", 0, "loses")]
    [TestCase("int", "TINYINT UNSIGNED", 1, "fails")]
    [TestCase("int unsigned", "BIGINT", 0, "none")]
    [TestCase("int unsigned", "INT", 0, "loses")]
    [TestCase("int", "DECIMAL(5,2)", 1, "fails")]
    [TestCase("bigint", "DOUBLE", 1, "loses")]
    [TestCase("int", "VARCHAR(2)", 0, "loses")]
    [TestCase("int", "VARCHAR(20)", 0, "none")]
    [TestCase("varchar(10)", "VARCHAR(5)", 1, "fails")]
    [TestCase("varchar(10)", "VARCHAR(5)", 0, "loses")]
    [TestCase("varchar(10)", "TEXT", 0, "none")]
    [TestCase("text", "TINYTEXT", 0, "loses")]
    [TestCase("varchar(10)", "INT", 1, "fails")]
    [TestCase("varchar(10)", "INT", 0, "loses")]
    [TestCase("char(10)", "VARCHAR(10)", 0, "none")]
    [TestCase("enum('a','b')", "ENUM('a')", 1, "fails")]
    [TestCase("enum('a','b')", "ENUM('a')", 0, "loses")]
    [TestCase("enum('a','b')", "ENUM('b','a','c')", 0, "none")]
    [TestCase("tinyint(1)", "BOOLEAN", 0, "none")]
    [TestCase("longtext", "JSON", 0, "fails")]
    public void TheClassifierMatchesWhatTheEngineDoes(string from, string to, int strict, string expected)
    {
        var answer = Scalar($"SELECT SchemaSmith_ColumnChangeLoss('{from.Replace("'", "''")}', '{to.Replace("'", "''")}', {strict})")?.ToString();
        Assert.That(answer switch { "0" => "none", "1" => "fails", "2" => "loses", _ => answer }, Is.EqualTo(expected));
    }
}
