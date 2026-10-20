// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using Npgsql;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

/// <summary>
/// OnlineApply on PostgreSQL. On an existing table an index is built CONCURRENTLY, a key is built as its unique index
/// CONCURRENTLY and attached USING INDEX, a check or foreign key is added NOT VALID and validated on its own, and an
/// index the package no longer declares is dropped CONCURRENTLY: all after the procedure, by PostgreSqlOnlineWork, since
/// none of it can run inside a transaction block. Each test runs the deploy the way SchemaQuench does (the procedure,
/// then the queue) and asserts the end state and the change row's Mode.
/// </summary>
[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.All)]
public class TableQuench_OnlineApplyTests : BaseTableQuenchTests
{
    private sealed class Session : IDisposable
    {
        public readonly NpgsqlConnection Connection;
        public readonly IDbCommand Command;
        public readonly List<string> Notices = [];
        public readonly List<string> WorkLog = [];

        public Session(string connectionString, string database)
        {
            Connection = (NpgsqlConnection)DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(connectionString);
            Connection.Notice += (_, e) => Notices.Add(e.Notice.MessageText);
            Connection.Open();
            Connection.ChangeDatabase(database);
            Command = Connection.CreateCommand();
            Command.CommandTimeout = 300;
        }

        public void Sql(string sql)
        {
            Command.CommandText = sql;
            Command.ExecuteNonQuery();
        }

        public object Scalar(string sql)
        {
            Command.CommandText = sql;
            return Command.ExecuteScalar();
        }

        public void Dispose()
        {
            Command.Dispose();
            Connection.Dispose();
        }
    }

    private Session Open() => new(_connectionString, _mainDb);

    // The deploy as SchemaQuench runs it: the procedure, then the queue it left. The queue runs after each table pass
    // in the product; one run after TableQuench sees the same statements in the same order.
    private void Deploy(Session s, string json, string online, bool whatIf = false, bool indexOnly = false, bool dropUnknownIndexes = false)
    {
        s.Command.CommandText = indexOnly
            ? $"CALL \"SchemaSmith\".\"IndexOnlyQuench\"(p_ProductName := '{_productName}', p_TableDefinitions := '{json.Replace("'", "''")}', p_WhatIf := {(whatIf ? "true" : "false")}, p_DropUnknownIndexes := {(dropUnknownIndexes ? "true" : "false")}, p_OnlineApply := '{online}')"
            : $"CALL \"SchemaSmith\".\"TableQuench\"(p_ProductName := '{_productName}', p_TableDefinitions := '{json.Replace("'", "''")}', p_WhatIf := {(whatIf ? "true" : "false")}, p_DropTablesRemovedFromProduct := false, p_DropUnknownIndexes := {(dropUnknownIndexes ? "true" : "false")}, p_OnlineApply := '{online}')";
        var tries = 0;
        var done = false;
        while (!done)
        {
            try
            {
                s.Command.ExecuteNonQuery();
                done = true;
            }
            catch (PostgresException e) when (++tries < 10 && e.SqlState == "40P01")
            {
                Thread.Sleep(1000);
            }
        }
        PostgreSqlOnlineWork.Run(s.Command, s.WorkLog.Add, _ => { });
    }

    // The Mode on this session's latest change row for the object, "<none>" when there is no row, "NULL" when it has none.
    private static string Mode(Session s, string objectName, string action)
    {
        var mode = s.Scalar($"SELECT COALESCE(\"Mode\", 'NULL') FROM \"SchemaSmith\".\"ChangeAudit\" WHERE \"SessionId\" = pg_backend_pid() AND \"ObjectName\" = '{objectName}' AND \"ActionType\" = '{action}' ORDER BY \"Id\" DESC LIMIT 1");
        return mode?.ToString() ?? "<none>";
    }

    private static string IndexValid(Session s, string index) =>
        s.Scalar($"SELECT idx.indisvalid::text FROM pg_class i JOIN pg_index idx ON idx.indexrelid = i.oid WHERE i.relname = '{index}'")?.ToString() ?? "<missing>";

    private static string ConstraintDef(Session s, string constraint) =>
        s.Scalar($"SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = '{constraint}'")?.ToString() ?? "<missing>";

    private static bool QueueLeftBehind(Session s) => (bool)s.Scalar("SELECT to_regclass('pg_temp.temp_deferred_online_work') IS NOT NULL");

    private static string Table(string name, string columns, string extras = "", string tableOnline = null) =>
        $$"""{ "Schema": "public", "Name": "{{name}}"{{(tableOnline == null ? "" : $", \"OnlineApply\": \"{tableOnline}\"")}}, "Columns": [{{columns}}]{{extras}} }""";

    private const string IdAndA = """{ "Name": "id", "DataType": "integer", "Nullable": false }, { "Name": "a", "DataType": "integer", "Nullable": true }, { "Name": "c", "DataType": "text", "Nullable": true }""";

    private static void Seed(Session s, string table) =>
        s.Sql($"DROP TABLE IF EXISTS public.{table} CASCADE; CREATE TABLE public.{table} (id integer NOT NULL, a integer, c text); INSERT INTO public.{table} SELECT g, g, 'x' FROM generate_series(1, 200) g");

    [Test]
    public void Prefer_BuildsAMissingIndexConcurrently_OnAnExistingTable()
    {
        using var s = Open();
        try
        {
            Seed(s, "onl_ix");
            Deploy(s, Table("onl_ix", IdAndA, """, "Indexes": [{ "Name": "ix_onl_ix_a", "IndexColumns": "a" }]"""), "PREFER");
            Assert.Multiple(() =>
            {
                Assert.That(IndexValid(s, "ix_onl_ix_a"), Is.EqualTo("true"));
                Assert.That(Mode(s, "public.onl_ix.ix_onl_ix_a", "created"), Is.EqualTo("online"));
                Assert.That(s.Notices, Has.Some.Contains("ix_onl_ix_a (online, after this pass)"));
                Assert.That(s.WorkLog, Has.Some.Contains("Building Index public.onl_ix.ix_onl_ix_a concurrently"));
                Assert.That(QueueLeftBehind(s), Is.False, "the queue is gone once it has run");
            });
        }
        finally { s.Sql("DROP TABLE IF EXISTS public.onl_ix CASCADE"); }
    }

    [Test]
    public void Prefer_AttachesKeysBuiltConcurrently_WithTheirDeclaredOptions()
    {
        using var s = Open();
        try
        {
            Seed(s, "onl_key");
            Deploy(s, Table("onl_key", IdAndA, """
                , "Indexes": [
                    { "Name": "pk_onl_key", "PrimaryKey": true, "IndexColumns": "id", "IncludeColumns": "c" },
                    { "Name": "uq_onl_key_a", "UniqueConstraint": true, "IndexColumns": "a", "FillFactor": 80, "Deferrable": true, "InitiallyDeferred": true } ]
                """), "PREFER");
            Assert.Multiple(() =>
            {
                Assert.That(ConstraintDef(s, "pk_onl_key"), Is.EqualTo("PRIMARY KEY (id) INCLUDE (c)"));
                Assert.That(ConstraintDef(s, "uq_onl_key_a"), Is.EqualTo("UNIQUE (a) DEFERRABLE INITIALLY DEFERRED"));
                Assert.That(s.Scalar("SELECT pg_get_indexdef('public.uq_onl_key_a'::regclass)")?.ToString(), Does.Contain("fillfactor='80'"));
                Assert.That(Mode(s, "public.onl_key.pk_onl_key", "created"), Is.EqualTo("online"));
                Assert.That(Mode(s, "public.onl_key.uq_onl_key_a", "created"), Is.EqualTo("online"));
            });
            // Converged: a second deploy finds nothing to build.
            s.WorkLog.Clear();
            Deploy(s, Table("onl_key", IdAndA, """
                , "Indexes": [
                    { "Name": "pk_onl_key", "PrimaryKey": true, "IndexColumns": "id", "IncludeColumns": "c" },
                    { "Name": "uq_onl_key_a", "UniqueConstraint": true, "IndexColumns": "a", "FillFactor": 80, "Deferrable": true, "InitiallyDeferred": true } ]
                """), "PREFER");
            Assert.That(s.WorkLog, Is.Empty, "the keys it attached compare equal to their declarations");
        }
        finally { s.Sql("DROP TABLE IF EXISTS public.onl_key CASCADE"); }
    }

    [Test]
    public void Off_EmitsThePlainStatements_AndRecordsNoMode()
    {
        using var s = Open();
        try
        {
            Seed(s, "onl_off");
            Deploy(s, Table("onl_off", IdAndA, """, "Indexes": [{ "Name": "ix_onl_off_a", "IndexColumns": "a" }]"""), "OFF", whatIf: true);
            Assert.That(string.Join("\n", s.Notices), Does.Not.Contain("CONCURRENTLY"));
            Deploy(s, Table("onl_off", IdAndA, """, "Indexes": [{ "Name": "ix_onl_off_a", "IndexColumns": "a" }]"""), "OFF");
            Assert.Multiple(() =>
            {
                Assert.That(IndexValid(s, "ix_onl_off_a"), Is.EqualTo("true"));
                Assert.That(Mode(s, "public.onl_off.ix_onl_off_a", "created"), Is.EqualTo("NULL"));
                Assert.That(s.WorkLog, Is.Empty);
            });
        }
        finally { s.Sql("DROP TABLE IF EXISTS public.onl_off CASCADE"); }
    }

    [Test]
    public void Prefer_OnATableThisDeployCreates_UsesThePlainStatements()
    {
        using var s = Open();
        try
        {
            s.Sql("DROP TABLE IF EXISTS public.onl_new CASCADE");
            Deploy(s, Table("onl_new", IdAndA, """, "Indexes": [{ "Name": "ix_onl_new_a", "IndexColumns": "a" }]"""), "PREFER");
            Assert.Multiple(() =>
            {
                Assert.That(IndexValid(s, "ix_onl_new_a"), Is.EqualTo("true"));
                Assert.That(Mode(s, "public.onl_new.ix_onl_new_a", "created"), Is.EqualTo("NULL"), "an empty table gains nothing from a concurrent build");
                Assert.That(s.WorkLog, Is.Empty);
            });
        }
        finally { s.Sql("DROP TABLE IF EXISTS public.onl_new CASCADE"); }
    }

    [TestCase("OFF", "Prefer", "online")]
    [TestCase("PREFER", "Off", "NULL")]
    public void ATablesOwnOnlineApply_OverridesTheDeploys(string deployValue, string tableValue, string expectedMode)
    {
        var table = $"onl_ovr_{deployValue.ToLowerInvariant()}";
        using var s = Open();
        try
        {
            Seed(s, table);
            Deploy(s, Table(table, IdAndA, $$""", "Indexes": [{ "Name": "ix_{{table}}_a", "IndexColumns": "a" }]""", tableValue), deployValue);
            Assert.Multiple(() =>
            {
                Assert.That(IndexValid(s, $"ix_{table}_a"), Is.EqualTo("true"));
                Assert.That(Mode(s, $"public.{table}.ix_{table}_a", "created"), Is.EqualTo(expectedMode));
            });
        }
        finally { s.Sql($"DROP TABLE IF EXISTS public.{table} CASCADE"); }
    }

    private static void SeedPartitioned(Session s, string table) =>
        s.Sql($"DROP TABLE IF EXISTS public.{table} CASCADE; CREATE TABLE public.{table} (id integer NOT NULL, a integer, c text) PARTITION BY RANGE (id); " +
              $"CREATE TABLE public.{table}_p1 PARTITION OF public.{table} FOR VALUES FROM (0) TO (1000); INSERT INTO public.{table} SELECT g, g, 'x' FROM generate_series(1, 200) g");

    [Test]
    public void Prefer_OnAPartitionedTable_BuildsThePlainIndex_AndSaysWhy()
    {
        using var s = Open();
        try
        {
            SeedPartitioned(s, "onl_part");
            Deploy(s, Table("onl_part", IdAndA, """, "Indexes": [{ "Name": "ix_onl_part_a", "IndexColumns": "a" }]"""), "PREFER");
            Assert.Multiple(() =>
            {
                Assert.That(s.Scalar("SELECT count(*) FROM pg_class WHERE relname = 'ix_onl_part_a'"), Is.EqualTo(1L));
                Assert.That(Mode(s, "public.onl_part.ix_onl_part_a", "created"), Is.EqualTo("unavailable"));
                Assert.That(s.Notices, Has.Some.Contains("online unavailable: a partitioned table cannot be indexed concurrently"));
            });
        }
        finally { s.Sql("DROP TABLE IF EXISTS public.onl_part CASCADE"); }
    }

    [Test]
    public void Require_RefusesABuildThatCannotRunOnline_NamingIt_AndChangesNothing()
    {
        using var s = Open();
        try
        {
            SeedPartitioned(s, "onl_req");
            s.Sql("CREATE INDEX ix_onl_req_unknown ON public.onl_req (c)");
            // The deploy would also drop the unknown index and widen a; the refusal takes both back.
            var json = Table("onl_req", """{ "Name": "id", "DataType": "integer", "Nullable": false }, { "Name": "a", "DataType": "bigint", "Nullable": true }, { "Name": "c", "DataType": "text", "Nullable": true }""",
                """, "Indexes": [{ "Name": "ix_onl_req_a", "IndexColumns": "a" }]""");
            var refusal = Assert.Throws<PostgresException>(() => Deploy(s, json, "REQUIRE", dropUnknownIndexes: true));
            Assert.Multiple(() =>
            {
                Assert.That(refusal!.MessageText, Does.Contain("OnlineApply is Require").And.Contain("public.onl_req.ix_onl_req_a (a partitioned table cannot be indexed concurrently)"));
                Assert.That(s.Scalar("SELECT count(*) FROM pg_class WHERE relname = 'ix_onl_req_a'"), Is.EqualTo(0L));
                Assert.That(s.Scalar("SELECT count(*) FROM pg_class WHERE relname = 'ix_onl_req_unknown'"), Is.EqualTo(1L), "the drop was taken back");
                Assert.That(s.Scalar("SELECT format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 'public.onl_req'::regclass AND attname = 'a'"), Is.EqualTo("integer"));
            });
        }
        finally { s.Sql("DROP TABLE IF EXISTS public.onl_req CASCADE"); }
    }

    [Test]
    public void Require_RefusesAnExclusionConstraint_AndPreferBuildsItPlain()
    {
        const string extras = """, "ExcludeConstraints": [{ "Name": "ex_onl_exc_a", "ExcludeColumns": [{ "Column": "a", "Operator": "=" }] }]""";
        using var s = Open();
        try
        {
            Seed(s, "onl_exc");
            var refusal = Assert.Throws<PostgresException>(() => Deploy(s, Table("onl_exc", IdAndA, extras), "REQUIRE"));
            Assert.That(refusal!.MessageText, Does.Contain("public.onl_exc.ex_onl_exc_a (an exclusion constraint cannot be built concurrently)"));

            Deploy(s, Table("onl_exc", IdAndA, extras), "PREFER");
            Assert.Multiple(() =>
            {
                Assert.That(ConstraintDef(s, "ex_onl_exc_a"), Does.StartWith("EXCLUDE"));
                Assert.That(Mode(s, "public.onl_exc.ex_onl_exc_a", "created"), Is.EqualTo("unavailable"));
            });
        }
        finally { s.Sql("DROP TABLE IF EXISTS public.onl_exc CASCADE"); }
    }

    [Test]
    public void Prefer_AddsChecksAndForeignKeysNotValid_ThenValidatesThem()
    {
        using var s = Open();
        try
        {
            s.Sql("DROP TABLE IF EXISTS public.onl_fk CASCADE; DROP TABLE IF EXISTS public.onl_ref CASCADE; " +
                  "CREATE TABLE public.onl_ref (id integer PRIMARY KEY); INSERT INTO public.onl_ref SELECT g FROM generate_series(1, 200) g");
            Seed(s, "onl_fk");
            var columns = """{ "Name": "id", "DataType": "integer", "Nullable": false }, { "Name": "a", "DataType": "integer", "Nullable": true, "CheckExpression": "a > 0" }, { "Name": "c", "DataType": "text", "Nullable": true }""";
            Deploy(s, Table("onl_fk", columns, """
                , "CheckConstraints": [{ "Name": "ck_onl_fk_id", "Expression": "id > 0" }]
                , "ForeignKeys": [{ "Name": "fk_onl_fk_a", "Columns": "a", "RelatedTableSchema": "public", "RelatedTable": "onl_ref", "RelatedColumns": "id" }]
                """), "PREFER");
            Assert.Multiple(() =>
            {
                Assert.That(ConstraintDef(s, "ck_onl_fk_id"), Is.EqualTo("CHECK ((id > 0))"));
                Assert.That(ConstraintDef(s, "CK_onl_fk_a"), Is.EqualTo("CHECK ((a > 0))"));
                Assert.That(ConstraintDef(s, "fk_onl_fk_a"), Is.EqualTo("FOREIGN KEY (a) REFERENCES onl_ref(id)"));
                Assert.That(Mode(s, "public.onl_fk.ck_onl_fk_id", "created"), Is.EqualTo("online"));
                Assert.That(Mode(s, "public.onl_fk.CK_onl_fk_a", "created"), Is.EqualTo("online"));
                Assert.That(Mode(s, "public.onl_fk.fk_onl_fk_a", "created"), Is.EqualTo("online"));
                Assert.That(s.WorkLog, Has.Some.Contains("Validating foreign key public.onl_fk.fk_onl_fk_a"));
                Assert.That(s.WorkLog.Count(l => l.Contains("fk_onl_fk_a")), Is.EqualTo(1),
                    "the constraint-state pass sees the key NOT VALID too; the queue validates it once");
                Assert.That(s.WorkLog, Has.Some.Contains("Validating constraint public.onl_fk.ck_onl_fk_id"));
            });
        }
        finally { s.Sql("DROP TABLE IF EXISTS public.onl_fk CASCADE; DROP TABLE IF EXISTS public.onl_ref CASCADE"); }
    }

    // The add is NOT VALID, so it succeeds whatever the rows hold, and the validation after the pass is what the rows fail:
    // the deploy fails as a plain add would, and the constraint is left NOT VALID for the next deploy to validate again.
    [TestCase("check", "23514", "ck_onl_bad_a", "CHECK ((a > 100)) NOT VALID")]
    [TestCase("columnCheck", "23514", "CK_onl_bad_a", "CHECK ((a > 100)) NOT VALID")]
    [TestCase("foreignKey", "23503", "fk_onl_bad_a", "FOREIGN KEY (a) REFERENCES onl_bad_ref(id) NOT VALID")]
    public void Prefer_AValidationTheRowsFail_FailsTheDeploy_AndLeavesTheConstraintNotValid(string kind, string sqlState, string constraint, string expected)
    {
        using var s = Open();
        try
        {
            s.Sql("DROP TABLE IF EXISTS public.onl_bad CASCADE; DROP TABLE IF EXISTS public.onl_bad_ref CASCADE; CREATE TABLE public.onl_bad_ref (id integer PRIMARY KEY); INSERT INTO public.onl_bad_ref VALUES (1)");
            Seed(s, "onl_bad");
            var json = kind switch
            {
                "check" => Table("onl_bad", IdAndA, """, "CheckConstraints": [{ "Name": "ck_onl_bad_a", "Expression": "a > 100" }]"""),
                "columnCheck" => Table("onl_bad", """{ "Name": "id", "DataType": "integer", "Nullable": false }, { "Name": "a", "DataType": "integer", "Nullable": true, "CheckExpression": "a > 100" }, { "Name": "c", "DataType": "text", "Nullable": true }"""),
                _ => Table("onl_bad", IdAndA, """, "ForeignKeys": [{ "Name": "fk_onl_bad_a", "Columns": "a", "RelatedTableSchema": "public", "RelatedTable": "onl_bad_ref", "RelatedColumns": "id" }]"""),
            };
            var failure = Assert.Throws<PostgresException>(() => Deploy(s, json, "PREFER"));
            Assert.Multiple(() =>
            {
                Assert.That(failure!.SqlState, Is.EqualTo(sqlState), failure.MessageText);
                Assert.That(ConstraintDef(s, constraint), Is.EqualTo(expected));
            });
        }
        finally { s.Sql("DROP TABLE IF EXISTS public.onl_bad CASCADE; DROP TABLE IF EXISTS public.onl_bad_ref CASCADE"); }
    }

    [Test]
    public void Prefer_ValidatesAnExistingNotValidConstraint_OnItsOwn()
    {
        using var s = Open();
        try
        {
            Seed(s, "onl_nv");
            s.Sql("ALTER TABLE public.onl_nv ADD CONSTRAINT ck_onl_nv_id CHECK (id > 0) NOT VALID");
            Deploy(s, Table("onl_nv", IdAndA, """, "CheckConstraints": [{ "Name": "ck_onl_nv_id", "Expression": "id > 0" }]"""), "PREFER");
            Assert.Multiple(() =>
            {
                Assert.That(ConstraintDef(s, "ck_onl_nv_id"), Is.EqualTo("CHECK ((id > 0))"));
                Assert.That(Mode(s, "public.onl_nv.ck_onl_nv_id", "modified"), Is.EqualTo("online"));
            });
        }
        finally { s.Sql("DROP TABLE IF EXISTS public.onl_nv CASCADE"); }
    }

    [Test]
    public void Prefer_ForeignKeyOnAPartitionedTable_IsOnlineFrom18_AndPlainBelow()
    {
        using var s = Open();
        try
        {
            s.Sql("DROP TABLE IF EXISTS public.onl_pfk_ref CASCADE; CREATE TABLE public.onl_pfk_ref (id integer PRIMARY KEY); INSERT INTO public.onl_pfk_ref SELECT g FROM generate_series(1, 200) g");
            SeedPartitioned(s, "onl_pfk");
            Deploy(s, Table("onl_pfk", IdAndA, """, "ForeignKeys": [{ "Name": "fk_onl_pfk_a", "Columns": "a", "RelatedTableSchema": "public", "RelatedTable": "onl_pfk_ref", "RelatedColumns": "id" }]"""), "PREFER");
            var major = Convert.ToInt32(s.Scalar("SELECT \"SchemaSmith\".\"ServerVersionNum\"()"));
            Assert.Multiple(() =>
            {
                Assert.That(ConstraintDef(s, "fk_onl_pfk_a"), Is.EqualTo("FOREIGN KEY (a) REFERENCES onl_pfk_ref(id)"));
                Assert.That(Mode(s, "public.onl_pfk.fk_onl_pfk_a", "created"), Is.EqualTo(major >= 18 ? "online" : "unavailable"));
            });
        }
        finally { s.Sql("DROP TABLE IF EXISTS public.onl_pfk CASCADE; DROP TABLE IF EXISTS public.onl_pfk_ref CASCADE"); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Prefer_DropsAnIndexThePackageNoLongerDeclares_Concurrently(bool indexOnly)
    {
        var table = indexOnly ? "onl_drop_io" : "onl_drop";
        using var s = Open();
        try
        {
            Seed(s, table);
            s.Sql($"CREATE INDEX ix_{table}_c ON public.{table} (c); CREATE INDEX ix_{table}_a ON public.{table} (a)");
            Deploy(s, Table(table, IdAndA, $$""", "Indexes": [{ "Name": "ix_{{table}}_a", "IndexColumns": "a" }]"""), "PREFER", indexOnly: indexOnly, dropUnknownIndexes: true);
            Assert.Multiple(() =>
            {
                Assert.That(IndexValid(s, $"ix_{table}_c"), Is.EqualTo("<missing>"));
                Assert.That(Mode(s, $"public.{table}.ix_{table}_c", "dropped"), Is.EqualTo("online"));
                Assert.That(s.WorkLog, Has.Some.Contains($"Dropping Index public.{table}.ix_{table}_c concurrently"));
            });
        }
        finally { s.Sql($"DROP TABLE IF EXISTS public.{table} CASCADE"); }
    }

    [Test]
    public void Prefer_IndexOnly_BuildsAMissingIndexConcurrently()
    {
        using var s = Open();
        try
        {
            Seed(s, "onl_io");
            Deploy(s, Table("onl_io", IdAndA, """, "Indexes": [{ "Name": "ix_onl_io_a", "IndexColumns": "a" }, { "Name": "uq_onl_io_id", "UniqueConstraint": true, "IndexColumns": "id" }]"""), "PREFER", indexOnly: true);
            Assert.Multiple(() =>
            {
                Assert.That(IndexValid(s, "ix_onl_io_a"), Is.EqualTo("true"));
                Assert.That(ConstraintDef(s, "uq_onl_io_id"), Is.EqualTo("UNIQUE (id)"));
                Assert.That(Mode(s, "public.onl_io.ix_onl_io_a", "created"), Is.EqualTo("online"));
                Assert.That(Mode(s, "public.onl_io.uq_onl_io_id", "created"), Is.EqualTo("online"));
            });
        }
        finally { s.Sql("DROP TABLE IF EXISTS public.onl_io CASCADE"); }
    }

    [Test]
    public void IndexOnly_HonoursATablesOwnOnlineApply_TheWhatIfMode_AndRequire()
    {
        using var s = Open();
        try
        {
            Seed(s, "onl_io_ovr");
            SeedPartitioned(s, "onl_io_req");
            Deploy(s, Table("onl_io_ovr", IdAndA, """, "Indexes": [{ "Name": "ix_onl_io_ovr_a", "IndexColumns": "a" }]"""), "OFF", whatIf: true, indexOnly: true);
            var whatIfMode = Mode(s, "public.onl_io_ovr.ix_onl_io_ovr_a", "wouldCreate");
            Deploy(s, Table("onl_io_ovr", IdAndA, """, "Indexes": [{ "Name": "ix_onl_io_ovr_a", "IndexColumns": "a" }]""", "Prefer"), "OFF", whatIf: true, indexOnly: true);
            var overriddenMode = Mode(s, "public.onl_io_ovr.ix_onl_io_ovr_a", "wouldCreate");
            var refusal = Assert.Throws<PostgresException>(() =>
                Deploy(s, Table("onl_io_req", IdAndA, """, "Indexes": [{ "Name": "ix_onl_io_req_a", "IndexColumns": "a" }]"""), "REQUIRE", indexOnly: true));
            Assert.Multiple(() =>
            {
                Assert.That(whatIfMode, Is.EqualTo("NULL"));
                Assert.That(overriddenMode, Is.EqualTo("online"), "the preview row carries the form, and the table's own OnlineApply decided it");
                Assert.That(refusal!.MessageText, Does.Contain("public.onl_io_req.ix_onl_io_req_a (a partitioned table cannot be indexed concurrently)"));
                Assert.That(s.Scalar("SELECT count(*) FROM pg_class WHERE relname = 'ix_onl_io_req_a'"), Is.EqualTo(0L));
            });
        }
        finally { s.Sql("DROP TABLE IF EXISTS public.onl_io_ovr CASCADE; DROP TABLE IF EXISTS public.onl_io_req CASCADE"); }
    }

    [Test]
    public void Prefer_AUniqueBuildTheRowsFail_FailsTheDeploy_AndLeavesNoInvalidIndex()
    {
        using var s = Open();
        try
        {
            Seed(s, "onl_dup");
            s.Sql("UPDATE public.onl_dup SET a = 1");
            Assert.Throws<PostgresException>(() =>
                Deploy(s, Table("onl_dup", IdAndA, """, "Indexes": [{ "Name": "ux_onl_dup_a", "Unique": true, "IndexColumns": "a" }]"""), "PREFER"));
            Assert.Multiple(() =>
            {
                Assert.That(IndexValid(s, "ux_onl_dup_a"), Is.EqualTo("<missing>"), "the invalid index each failed build leaves is dropped");
                Assert.That(s.WorkLog, Has.Some.Contains("dropping it and building once more"));
            });
        }
        finally { s.Sql("DROP TABLE IF EXISTS public.onl_dup CASCADE"); }
    }

    [Test]
    public void WhatIf_ListsTheStatementsThatWouldRunAfterThePass_AndChangesNothing()
    {
        using var s = Open();
        try
        {
            s.Sql("DROP TABLE IF EXISTS public.onl_wi_ref CASCADE; CREATE TABLE public.onl_wi_ref (id integer PRIMARY KEY)");
            Seed(s, "onl_wi");
            s.Sql("CREATE INDEX ix_onl_wi_c ON public.onl_wi (c)");
            Deploy(s, Table("onl_wi", IdAndA, """
                , "Indexes": [{ "Name": "ix_onl_wi_a", "IndexColumns": "a" }, { "Name": "pk_onl_wi", "PrimaryKey": true, "IndexColumns": "id" }]
                , "CheckConstraints": [{ "Name": "ck_onl_wi_id", "Expression": "id > 0" }]
                """), "PREFER", whatIf: true, dropUnknownIndexes: true);
            var printed = string.Join("\n", s.Notices);
            Assert.Multiple(() =>
            {
                Assert.That(printed, Does.Contain("CREATE INDEX CONCURRENTLY \"ix_onl_wi_a\" ON \"public\".\"onl_wi\""));
                Assert.That(printed, Does.Contain("CREATE UNIQUE INDEX CONCURRENTLY \"pk_onl_wi\"").And.Contain("ADD CONSTRAINT \"pk_onl_wi\" PRIMARY KEY USING INDEX \"pk_onl_wi\""));
                Assert.That(printed, Does.Contain("DROP INDEX CONCURRENTLY IF EXISTS \"public\".\"ix_onl_wi_c\""));
                Assert.That(printed, Does.Contain("VALIDATE CONSTRAINT \"ck_onl_wi_id\""));
                Assert.That(printed, Does.Not.Contain("DeferOnlineWork"), "the preview shows the statements, not the queue");
                Assert.That(Mode(s, "public.onl_wi.ix_onl_wi_a", "wouldCreate"), Is.EqualTo("online"));
                Assert.That(Mode(s, "public.onl_wi.ix_onl_wi_c", "wouldDrop"), Is.EqualTo("online"));
                Assert.That(IndexValid(s, "ix_onl_wi_a"), Is.EqualTo("<missing>"));
                Assert.That(IndexValid(s, "ix_onl_wi_c"), Is.EqualTo("true"));
                Assert.That(s.WorkLog, Is.Empty);
            });
        }
        finally { s.Sql("DROP TABLE IF EXISTS public.onl_wi CASCADE; DROP TABLE IF EXISTS public.onl_wi_ref CASCADE"); }
    }
}
