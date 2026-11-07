// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.SqlClient;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.SqlServer;

/// <summary>
/// SQL Server sequences, declared rather than scripted. The scripted form is a guarded CREATE SEQUENCE -- SQL Server has
/// no CREATE OR ALTER SEQUENCE -- so once the sequence existed an edited INCREMENT, MAXVALUE, CYCLE or CACHE never landed,
/// and the run reported success. The declared form converges every attribute ALTER SEQUENCE can change, refuses what it
/// cannot (the data type; a range that would leave out the immutable start value or the current value), and never
/// touches the current value. Each test reads sys.sequences or draws values, so it asserts what the database does.
/// </summary>
[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
[Parallelizable(scope: ParallelScope.Self)]
public class SequenceTests
{
    private sealed class Run : IDisposable
    {
        public readonly SqlConnection Conn;
        public readonly string Schema = $"sq_{Guid.NewGuid():N}"[..14];
        public readonly List<string> Messages = new();

        public Run()
        {
            Conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(FixtureSetup.GetMainDbConnectionString());
            Conn.InfoMessage += (_, e) => Messages.Add(e.Message);
            Conn.Open();
            Exec($"CREATE SCHEMA [{Schema}]");
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
            return v == null || v == DBNull.Value ? null : Convert.ToString(v);
        }

        // One sequence named "s" in this run's schema, with the given JSON properties.
        public void Quench(string properties, bool whatIf = false)
        {
            var sequence = JObject.Parse($$"""{ "Schema": "{{Schema}}", "Name": "s"{{(properties == "" ? "" : ", " + properties)}} }""");
            var xml = ModelXmlSerializer.ToIngestXml(new JArray(sequence).ToString(), "Sequences", "Sequence");
            using var cmd = Conn.CreateCommand();
            cmd.CommandTimeout = 300;
            cmd.CommandText = "EXEC [SchemaSmith].[SequenceQuench] @ProductName = N'SequenceTests', @Sequences = @x, @WhatIf = @w";
            var x = cmd.CreateParameter(); x.ParameterName = "@x"; x.Value = xml; cmd.Parameters.Add(x);
            var w = cmd.CreateParameter(); w.ParameterName = "@w"; w.Value = whatIf; cmd.Parameters.Add(w);
            cmd.ExecuteNonQuery();
        }

        public string Attr(string column) => Scalar($"SELECT CAST({column} AS NVARCHAR(100)) FROM sys.sequences WHERE object_id = OBJECT_ID('[{Schema}].[s]')");
        public string Next() => Scalar($"SELECT CAST(NEXT VALUE FOR [{Schema}].[s] AS NVARCHAR(50))");
        public string Audits() => Scalar($"SELECT ISNULL(STRING_AGG(ActionType, ','), '') FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID AND ObjectName = '{Schema}.s'") ?? "";
        public void ClearAudits() => Exec("DELETE FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID");

        public void Dispose()
        {
            Exec($"""
                DECLARE @sql NVARCHAR(MAX) = N'';
                SELECT @sql += N'DROP SEQUENCE [{Schema}].' + QUOTENAME(name) + N';' FROM sys.sequences WHERE schema_id = SCHEMA_ID('{Schema}');
                SELECT @sql += N'DROP TYPE [{Schema}].' + QUOTENAME(name) + N';' FROM sys.types WHERE schema_id = SCHEMA_ID('{Schema}') AND is_user_defined = 1;
                EXEC (@sql);
                DROP SCHEMA [{Schema}];
                DELETE FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID;
                """);
            Conn.Dispose();
        }
    }

    [Test]
    public void ADeclaredSequence_IsCreatedWithEveryAttribute()
    {
        using var r = new Run();
        r.Quench("""
            "DataType": "decimal(38,0)", "Start": -5, "Increment": -1, "MinValue": -99999999999999999999999999999999999999, "MaxValue": 0,
            "Cache": 50, "Cycle": true
            """);

        Assert.Multiple(() =>
        {
            Assert.That(r.Scalar($"SELECT TYPE_NAME(system_type_id) + '(' + CAST(precision AS VARCHAR) + ')' FROM sys.sequences WHERE object_id = OBJECT_ID('[{r.Schema}].[s]')"), Is.EqualTo("decimal(38)"));
            Assert.That(r.Attr("start_value"), Is.EqualTo("-5"));
            Assert.That(r.Attr("increment"), Is.EqualTo("-1"));
            Assert.That(r.Attr("minimum_value"), Is.EqualTo("-99999999999999999999999999999999999999"));
            Assert.That(r.Attr("maximum_value"), Is.EqualTo("0"));
            Assert.That(r.Attr("cache_size"), Is.EqualTo("50"));
            Assert.That(r.Attr("is_cycling"), Is.EqualTo("1"));
            Assert.That(r.Audits(), Is.EqualTo("created"));
        });
    }

    [Test]
    public void AChangedIncrement_IsAppliedInPlace_AndTheNextValueFollowsOnFromTheLast()
    {
        using var r = new Run();
        r.Quench("\"DataType\": \"int\", \"Start\": 100");
        r.Next(); r.Next();
        var last = int.Parse(r.Next()!);

        r.Quench("\"DataType\": \"int\", \"Start\": 100, \"Increment\": 5");

        Assert.That(int.Parse(r.Next()!), Is.EqualTo(last + 5), "the current value was not reset to the start");
    }

    [Test]
    public void ChangedBoundsCycleAndCache_EachConverge()
    {
        using var r = new Run();
        r.Quench("\"DataType\": \"int\"");

        r.ClearAudits();
        r.Quench("\"DataType\": \"int\", \"MaxValue\": 1000, \"Cycle\": true, \"Cache\": 0");
        Assert.Multiple(() =>
        {
            Assert.That(r.Attr("maximum_value"), Is.EqualTo("1000"));
            Assert.That(r.Attr("is_cycling"), Is.EqualTo("1"));
            Assert.That(r.Attr("is_cached"), Is.EqualTo("0"));
            Assert.That(r.Audits(), Is.EqualTo("modified"), "one ALTER for the lot");
        });

        r.Quench("\"DataType\": \"int\", \"Cache\": 25");
        Assert.Multiple(() =>
        {
            Assert.That(r.Attr("maximum_value"), Is.EqualTo("2147483647"), "an omitted MaxValue is the type's");
            Assert.That(r.Attr("is_cycling"), Is.EqualTo("0"));
            Assert.That(r.Attr("cache_size"), Is.EqualTo("25"));
        });

        r.Quench("\"DataType\": \"int\"");
        Assert.That(r.Attr("ISNULL(CAST(cache_size AS VARCHAR), 'default') + '/' + CAST(is_cached AS VARCHAR)"), Is.EqualTo("default/1"), "an omitted Cache is the engine's default");
    }

    [TestCase("tinyint", "0")]
    [TestCase("int", "-2147483648")]
    [TestCase("decimal(38,0)", "-99999999999999999999999999999999999999")]
    public void AClearedMinValue_IsTheTypesMinimum(string type, string min)
    {
        using var r = new Run();
        r.Quench($"\"DataType\": \"{type}\", \"Start\": 1, \"MinValue\": 1");

        r.Quench($"\"DataType\": \"{type}\", \"Start\": 1");

        Assert.That(r.Attr("minimum_value"), Is.EqualTo(min));
    }

    [TestCase("INTEGER")]
    [TestCase("int")]
    [TestCase("numeric(10,0)")]
    [TestCase("decimal")]
    [TestCase("[alias]")]
    public void ARedeployInAnotherSpellingOfTheSameType_ChangesNothing(string spelling)
    {
        using var r = new Run();
        r.Exec($"CREATE TYPE [{r.Schema}].[MyInt] FROM int");
        var type = spelling == "[alias]" ? $"[{r.Schema}].[MyInt]" : spelling;
        var canonical = spelling switch { "numeric(10,0)" => "numeric(10)", "decimal" => "decimal(18)", _ => "int" };
        r.Quench($"\"DataType\": \"{canonical}\"");

        r.ClearAudits();
        r.Quench($"\"DataType\": \"{type}\"");

        Assert.That(r.Audits(), Is.Empty);
    }

    [Test]
    public void AChangedType_IsRefused_AndTheSequenceIsLeftAsItWas()
    {
        using var r = new Run();
        r.Quench("\"DataType\": \"decimal(18,0)\"");
        r.Next();
        var current = r.Attr("current_value");

        var ex = Assert.Catch<SqlException>(() => r.Quench("\"DataType\": \"decimal(19,0)\", \"Increment\": 5"));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain($"{r.Schema}.s").And.Contain("cannot change a sequence's data type"));
            Assert.That(r.Attr("precision"), Is.EqualTo("18"));
            Assert.That(r.Attr("increment"), Is.EqualTo("1"), "nothing else was altered either");
            Assert.That(r.Attr("current_value"), Is.EqualTo(current));
        });
    }

    [Test]
    public void ARangeThatLeavesOutTheStartValue_IsRefusedByName()
    {
        using var r = new Run();
        r.Quench("\"DataType\": \"int\", \"Start\": 10, \"MinValue\": 1");

        var ex = Assert.Catch<SqlException>(() => r.Quench("\"DataType\": \"int\", \"Start\": 10, \"MinValue\": 11"));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain($"{r.Schema}.s").And.Contain("leaves out its start value"));
            Assert.That(r.Attr("minimum_value"), Is.EqualTo("1"));
        });
    }

    [Test]
    public void AChangedStart_IsReported_NotApplied()
    {
        using var r = new Run();
        r.Quench("\"DataType\": \"int\", \"Start\": 1");

        r.ClearAudits();
        r.Messages.Clear();
        r.Quench("\"DataType\": \"int\", \"Start\": 500");

        Assert.Multiple(() =>
        {
            Assert.That(r.Attr("start_value"), Is.EqualTo("1"));
            Assert.That(r.Messages, Has.Some.Contains("Start applies only when the sequence is created"));
            Assert.That(r.Audits(), Is.Empty, "a change that will not happen is not reported as one");
        });
    }

    [Test]
    public void WhatIf_ChangesNothing_AndSaysWhatWouldChange()
    {
        using var r = new Run();
        r.Quench("\"DataType\": \"int\"", whatIf: true);
        Assert.Multiple(() =>
        {
            Assert.That(r.Attr("name"), Is.Null);
            Assert.That(r.Audits(), Is.EqualTo("wouldCreate"));
        });

        r.Quench("\"DataType\": \"int\"");
        r.ClearAudits();
        r.Quench("\"DataType\": \"int\", \"Increment\": 7", whatIf: true);
        Assert.Multiple(() =>
        {
            Assert.That(r.Attr("increment"), Is.EqualTo("1"));
            Assert.That(r.Audits(), Is.EqualTo("wouldModify"));
        });
    }

    [Test]
    public void ADescription_IsSet_Changed_LeftAloneWhenOmitted_AndRemovedByAnEmptyString()
    {
        using var r = new Run();
        string Description() => r.Scalar($"SELECT CAST(value AS NVARCHAR(400)) FROM sys.extended_properties WHERE class = 1 AND minor_id = 0 AND name = 'MS_Description' AND major_id = OBJECT_ID('[{r.Schema}].[s]')");

        r.Quench("\"Description\": \"first\"");
        Assert.That(Description(), Is.EqualTo("first"));
        r.Quench("\"Description\": \"second\"");
        Assert.That(Description(), Is.EqualTo("second"));
        r.Quench("");
        Assert.That(Description(), Is.EqualTo("second"));
        r.Quench("\"Description\": \"\"");
        Assert.That(Description(), Is.Null);
    }
}
