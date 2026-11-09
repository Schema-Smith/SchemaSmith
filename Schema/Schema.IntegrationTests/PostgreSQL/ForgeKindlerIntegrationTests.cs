// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.PostgreSQL;

/// <summary>
/// Integration tests for ForgeKindler against a live PostgreSQL database.
/// Uses dynamically created test databases via FixtureSetup.
/// Note: ForgeKindler is deployed once by FixtureSetup - these tests verify the deployment.
/// </summary>
[Category("PostgreSQL")]
[Category("Integration")]
[TestFixture]
public class ForgeKindlerIntegrationTests
{
    private IDbConnection _connection = null!;

    [SetUp]
    public void SetUp()
    {
        _connection = DbConnectionFactory.ForPlatform(Platform.PostgreSQL)
            .GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        _connection.Open();
    }

    [TearDown]
    public void TearDown()
    {
        _connection?.Close();
        _connection?.Dispose();
    }

    // The deployment record's table is kindled with the other helper tables, with the columns the record writes.
    [Test]
    public void KindleTheForge_CreatesTheDeploymentHistoryTable()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'SchemaSmith' AND TABLE_NAME = 'DeploymentHistory' ORDER BY ORDINAL_POSITION";
        var columns = new System.Collections.Generic.List<string>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) columns.Add(reader.GetString(0));

        Assert.That(columns, Is.EqualTo(new[] { "Id", "RunId", "Product", "Template", "SchemaName", "StartedUtc", "FinishedUtc", "Outcome", "ChangesApplied", "ToolVersion", "PackageHash", "SourceCommit", "SourceBranch", "RunLabel", "PipelineUrl", "Actor", "DeployedAs", "ResumedFromRunId" }));
    }

    [Test]
    public void KindleTheForge_CreatesCompletedMigrationScriptsSlotScopeIndex()
    {
        // Post-slice-8 cleanup (Commit B): a secondary index on
        // (ProductName, QuenchSlot, template_name, schema_name) covers GetCompletedEntriesBySlot
        // lookups. The PK leads with ScriptPath — the (ProductName, QuenchSlot, template_name,
        // schema_name) tracking-lookup pattern was previously serviced by a sequential /
        // clustered-index scan. KindleTheForge ran during FixtureSetup; the index must exist.
        using var command = _connection.CreateCommand();
        command.CommandText = @"
            SELECT COUNT(*)
            FROM pg_indexes
            WHERE schemaname = 'SchemaSmith'
              AND tablename = 'CompletedMigrationScripts'
              AND indexname = 'ix_completedmigrationscripts_slot_scope'";
        var indexCount = Convert.ToInt32(command.ExecuteScalar());
        Assert.That(indexCount, Is.EqualTo(1),
            "Secondary index ix_completedmigrationscripts_slot_scope must be created by KindleTheForge.");

        // Verify the column list. pg_indexes.indexdef is the CREATE INDEX statement text;
        // we assert the four leading columns appear in order.
        command.CommandText = @"
            SELECT indexdef
            FROM pg_indexes
            WHERE schemaname = 'SchemaSmith'
              AND tablename = 'CompletedMigrationScripts'
              AND indexname = 'ix_completedmigrationscripts_slot_scope'";
        var indexDef = command.ExecuteScalar()?.ToString() ?? "";

        // indexdef looks like:
        // CREATE INDEX ix_completedmigrationscripts_slot_scope ON "SchemaSmith"."CompletedMigrationScripts" USING btree ("ProductName", "QuenchSlot", template_name, schema_name)
        Assert.That(indexDef, Does.Contain("\"ProductName\""),
            $"Index must include ProductName as a key column. indexdef={indexDef}");
        Assert.That(indexDef, Does.Contain("\"QuenchSlot\""),
            $"Index must include QuenchSlot as a key column. indexdef={indexDef}");
        Assert.That(indexDef, Does.Contain("template_name"),
            $"Index must include template_name as a key column. indexdef={indexDef}");
        Assert.That(indexDef, Does.Contain("schema_name"),
            $"Index must include schema_name as a key column. indexdef={indexDef}");

        // Verify ordering: ProductName appears before QuenchSlot, etc. — the engine's
        // GetCompletedEntriesBySlot filters in this order, so the leading-column ordering matters.
        var productNameIdx = indexDef.IndexOf("\"ProductName\"", StringComparison.Ordinal);
        var quenchSlotIdx = indexDef.IndexOf("\"QuenchSlot\"", StringComparison.Ordinal);
        var templateIdx = indexDef.IndexOf("template_name", StringComparison.Ordinal);
        var schemaIdx = indexDef.IndexOf("schema_name", StringComparison.Ordinal);
        Assert.That(productNameIdx, Is.LessThan(quenchSlotIdx),
            "ProductName must lead QuenchSlot in the index.");
        Assert.That(quenchSlotIdx, Is.LessThan(templateIdx),
            "QuenchSlot must lead template_name in the index.");
        Assert.That(templateIdx, Is.LessThan(schemaIdx),
            "template_name must lead schema_name in the index.");
    }

    [Test]
    public void KindleTheForge_SlotScopeIndex_IsIdempotent()
    {
        // KindleTheForge runs the kindling script on every quench — the CREATE INDEX IF NOT
        // EXISTS guard must keep the secondary index creation a no-op when the index already
        // exists. Run KindleTheForge a second time and assert no exception + a unique index.
        using var command = _connection.CreateCommand();
        // force: the version-gated kindle would otherwise skip after the fixture's initial kindle
        Assert.DoesNotThrow(() => ForgeKindler.KindleTheForge(command, Platform.PostgreSQL, forceReKindle: true),
            "Repeated KindleTheForge must not throw — the secondary index DDL must be idempotent.");

        command.CommandText = @"
            SELECT COUNT(*)
            FROM pg_indexes
            WHERE schemaname = 'SchemaSmith'
              AND tablename = 'CompletedMigrationScripts'
              AND indexname = 'ix_completedmigrationscripts_slot_scope'";
        var indexCount = Convert.ToInt32(command.ExecuteScalar());
        Assert.That(indexCount, Is.EqualTo(1),
            "Re-running KindleTheForge must not produce a duplicate secondary index.");
    }

    [Test]
    public void KindleTheForge_LegacyCompletedMigrationScripts_BootstrapAddsColumnsAndIndex()
    {
        // BootstrapTableQuench refactor: a pre-slice-2 / pre-Commit-B PostgreSQL table is
        // upgraded in a single kindling pass via Bootstrap's ADD COLUMN IF NOT EXISTS +
        // CREATE INDEX IF NOT EXISTS guards.
        using var command = _connection.CreateCommand();
        try
        {
            command.CommandText = @"DROP TABLE IF EXISTS ""SchemaSmith"".""CompletedMigrationScripts""";
            command.ExecuteNonQuery();
            command.CommandText = @"
                CREATE TABLE ""SchemaSmith"".""CompletedMigrationScripts"" (
                    ""ScriptPath""  VARCHAR(800) NOT NULL,
                    ""ProductName"" VARCHAR(100) NOT NULL,
                    ""QuenchSlot""  VARCHAR(30)  NOT NULL,
                    ""QuenchDate""  TIMESTAMP    NOT NULL DEFAULT NOW(),
                    CONSTRAINT ""PK_CompletedMigrationScripts"" PRIMARY KEY (""ScriptPath"", ""ProductName"", ""QuenchSlot"")
                );";
            command.ExecuteNonQuery();

            // force: the version-gated kindle would otherwise skip after the fixture's initial kindle
            ForgeKindler.KindleTheForge(command, Platform.PostgreSQL, forceReKindle: true);

            // Verify both column type (VARCHAR(256)), nullability (NOT NULL), and default ('').
            // A regression producing a nullable column or missing default would silently break
            // PK extension and GetCompletedEntriesBySlot filtering.
            command.CommandText = @"
                SELECT column_name, data_type, character_maximum_length, is_nullable, column_default
                  FROM information_schema.columns
                 WHERE table_schema = 'SchemaSmith' AND table_name = 'CompletedMigrationScripts'
                   AND column_name IN ('template_name', 'schema_name')
                 ORDER BY column_name";
            using (var reader = command.ExecuteReader())
            {
                var rows = new List<(string Name, string Type, int MaxLen, string Nullable, string Default)>();
                while (reader.Read())
                {
                    rows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
                        reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
                }
                reader.Close();
                Assert.That(rows, Has.Count.EqualTo(2),
                    "Bootstrap must add both template_name and schema_name.");
                foreach (var row in rows)
                {
                    Assert.That(row.Type, Is.EqualTo("character varying"), $"{row.Name} type");
                    Assert.That(row.MaxLen, Is.EqualTo(256), $"{row.Name} max length");
                    Assert.That(row.Nullable, Is.EqualTo("NO"), $"{row.Name} must be NOT NULL");
                    Assert.That(row.Default, Is.Not.Null, $"{row.Name} must carry a default");
                    Assert.That(row.Default, Does.Contain("''"),
                        $"{row.Name} default must be empty string");
                }
            }

            // Verify the index exists AND covers the right columns in the right order.
            command.CommandText = @"
                SELECT indexdef FROM pg_indexes
                WHERE schemaname = 'SchemaSmith'
                  AND tablename = 'CompletedMigrationScripts'
                  AND indexname = 'ix_completedmigrationscripts_slot_scope'";
            var indexDef = command.ExecuteScalar() as string;
            Assert.That(indexDef, Is.Not.Null,
                "Bootstrap must create the secondary index from the shared JSON definition.");
            var pn = indexDef.IndexOf("\"ProductName\"", StringComparison.Ordinal);
            var qs = indexDef.IndexOf("\"QuenchSlot\"", StringComparison.Ordinal);
            var tn = indexDef.IndexOf("template_name", StringComparison.Ordinal);
            var sn = indexDef.IndexOf("schema_name", StringComparison.Ordinal);
            Assert.That(pn, Is.GreaterThan(0).And.LessThan(qs).And.LessThan(tn).And.LessThan(sn),
                $"Secondary index must lead with ProductName, QuenchSlot, template_name, schema_name in order. indexdef={indexDef}");
        }
        finally
        {
            command.CommandText = @"DROP TABLE IF EXISTS ""SchemaSmith"".""CompletedMigrationScripts""";
            command.ExecuteNonQuery();
            // force: the version-gated kindle would otherwise skip, leaving the dropped table unrebuilt
            ForgeKindler.KindleTheForge(command, Platform.PostgreSQL, forceReKindle: true);
        }
    }

    [Test]
    public void KindleTheForge_LegacyProductOwnership_BootstrapAddsTemplateNameColumn()
    {
        // PostgreSQL has a second kindling table — ProductOwnership — whose slice-3 audit B1
        // added a template_name column. The legacy 4-column shape must upgrade via Bootstrap.
        using var command = _connection.CreateCommand();
        try
        {
            command.CommandText = @"DROP TABLE IF EXISTS ""SchemaSmith"".""ProductOwnership""";
            command.ExecuteNonQuery();
            command.CommandText = @"
                CREATE TABLE ""SchemaSmith"".""ProductOwnership"" (
                    ""Schema""      VARCHAR(256) NOT NULL,
                    ""TableName""   VARCHAR(256) NOT NULL,
                    ""IndexName""   VARCHAR(256) NULL,
                    ""ProductName"" VARCHAR(100) NOT NULL,
                    CONSTRAINT ""PK_ProductOwnership"" UNIQUE (""Schema"", ""TableName"", ""IndexName"", ""ProductName"")
                );";
            command.ExecuteNonQuery();

            // force: the version-gated kindle would otherwise skip after the fixture's initial kindle
            ForgeKindler.KindleTheForge(command, Platform.PostgreSQL, forceReKindle: true);

            command.CommandText = @"
                SELECT data_type, character_maximum_length, is_nullable, column_default
                  FROM information_schema.columns
                 WHERE table_schema = 'SchemaSmith' AND table_name = 'ProductOwnership'
                   AND column_name = 'template_name'";
            using var reader = command.ExecuteReader();
            Assert.That(reader.Read(), Is.True,
                "Bootstrap must add template_name to legacy ProductOwnership tables.");
            Assert.That(reader.GetString(0), Is.EqualTo("character varying"), "template_name type");
            Assert.That(reader.GetInt32(1), Is.EqualTo(256), "template_name max length");
            Assert.That(reader.GetString(2), Is.EqualTo("NO"), "template_name must be NOT NULL");
            Assert.That(reader.IsDBNull(3), Is.False, "template_name must carry a default");
            Assert.That(reader.GetString(3), Does.Contain("''"),
                "template_name default must be empty string");
            reader.Close();
        }
        finally
        {
            command.CommandText = @"DROP TABLE IF EXISTS ""SchemaSmith"".""ProductOwnership""";
            command.ExecuteNonQuery();
            // force: the version-gated kindle would otherwise skip, leaving the dropped table unrebuilt
            ForgeKindler.KindleTheForge(command, Platform.PostgreSQL, forceReKindle: true);
        }
    }

    [Test]
    public void Kindle_FreshDatabase_WritesStamp()
    {
        // Simulate a fresh install by removing the stamp table, then kindle without force.
        // An absent stamp is indistinguishable from a version mismatch — the gate must kindle.
        using var command = _connection.CreateCommand();
        try
        {
            command.CommandText = @"DROP TABLE IF EXISTS ""SchemaSmith"".""KindleStamp""";
            command.ExecuteNonQuery();

            // Absent stamp => no match => kindle runs without needing force.
            ForgeKindler.KindleTheForge(command, Platform.PostgreSQL);

            Assert.That(ForgeKindler.ReadStamp(command, Platform.PostgreSQL),
                Is.EqualTo(ForgeKindler.ComputeKindleStamp(Platform.PostgreSQL)),
                "A fresh kindle must write the current content stamp.");
        }
        finally
        {
            // force: the version-gated kindle would otherwise skip, leaving the dropped table unrebuilt
            ForgeKindler.KindleTheForge(command, Platform.PostgreSQL, forceReKindle: true);
        }
    }

    [Test]
    public void Kindle_SecondRun_IsNoOp_StampUnchanged()
    {
        // If the stamp already matches the current kindle content the gate must skip without
        // rewriting the stamp row — a no-op second kindle must leave UpdatedUtc untouched.
        using var command = _connection.CreateCommand();
        ForgeKindler.KindleTheForge(command, Platform.PostgreSQL, forceReKindle: true); // ensure stamped

        command.CommandText = @"SELECT ""UpdatedUtc"" FROM ""SchemaSmith"".""KindleStamp"" LIMIT 1";
        var firstWrite = command.ExecuteScalar();

        // stamp matches => skip path — UpdatedUtc must not be rewritten
        ForgeKindler.KindleTheForge(command, Platform.PostgreSQL);

        command.CommandText = @"SELECT ""UpdatedUtc"" FROM ""SchemaSmith"".""KindleStamp"" LIMIT 1";
        var secondWrite = command.ExecuteScalar();

        Assert.That(secondWrite, Is.EqualTo(firstWrite), "Skip path must not rewrite the stamp row.");
    }

    [Test]
    public void Kindle_UpgradePath_NaturalStampMismatch_DropsDuplicateOverload()
    {
        // Prove the REAL upgrade trigger: a stale stamp + an old overload present -> next
        // unforced kindle drops the superseded overload and re-stamps, leaving exactly one
        // overload (no 42725 ambiguity).
        using var command = _connection.CreateCommand();
        try
        {
            ForgeKindler.KindleTheForge(command, Platform.PostgreSQL, forceReKindle: true); // current sigs present

            // Hand-create the pre-schema-templates 2-arg overload (the upgrade hazard).
            command.CommandText =
                @"CREATE OR REPLACE PROCEDURE ""SchemaSmith"".""ValidateTableOwnership""(p_ProductName varchar, p_WhatIf boolean) " +
                @"LANGUAGE plpgsql AS $$ BEGIN END; $$;";
            command.ExecuteNonQuery();
            Assert.That(CountOverloads(command, "ValidateTableOwnership"), Is.EqualTo(2),
                "Setup: two overloads of ValidateTableOwnership must exist before the upgrade kindle.");

            // Force a stale stamp so the next UNFORCED kindle detects a mismatch and re-kindles.
            ForgeKindler.WriteStamp(command, Platform.PostgreSQL, new string('0', 64));

            // Unforced: mismatch detected => drop superseded overload + rekindle + re-stamp.
            ForgeKindler.KindleTheForge(command, Platform.PostgreSQL);

            Assert.That(CountOverloads(command, "ValidateTableOwnership"), Is.EqualTo(1),
                "Re-kindle must drop the superseded 2-arg overload, leaving exactly one (no 42725 ambiguity).");
            Assert.That(ForgeKindler.ReadStamp(command, Platform.PostgreSQL),
                Is.EqualTo(ForgeKindler.ComputeKindleStamp(Platform.PostgreSQL)),
                "Stamp must be refreshed after the upgrade kindle.");
        }
        finally
        {
            // force: the version-gated kindle would otherwise skip, leaving state inconsistent
            ForgeKindler.KindleTheForge(command, Platform.PostgreSQL, forceReKindle: true);
        }
    }

    [Test]
    public void Kindle_ParallelFirstArrivals_KindleOnce_NoCollision()
    {
        // Clear the stamp so N racing sessions contend to be the first kindler. Each task uses
        // its OWN connection (the session advisory lock is per-connection). The lock must
        // serialize them so only one kindles and none collide (no 'tuple concurrently updated').
        using (var prep = _connection.CreateCommand())
        {
            prep.CommandText = @"DROP TABLE IF EXISTS ""SchemaSmith"".""KindleStamp""";
            prep.ExecuteNonQuery();
        }

        var errors = new ConcurrentBag<Exception>();
        try
        {
            Parallel.For(0, 8, _ =>
            {
                try
                {
                    using var c = DbConnectionFactory.ForPlatform(Platform.PostgreSQL)
                        .GetDbConnection(FixtureSetup.GetMainDbConnectionString());
                    c.Open();
                    using var cmd = c.CreateCommand();
                    ForgeKindler.KindleTheForge(cmd, Platform.PostgreSQL);
                }
                catch (Exception ex) { errors.Add(ex); }
            });

            Assert.That(errors, Is.Empty,
                $"Parallel kindle must not collide. Errors: {string.Join("; ", errors)}");

            using var verify = _connection.CreateCommand();
            verify.CommandText = @"SELECT COUNT(*) FROM ""SchemaSmith"".""KindleStamp""";
            Assert.That(Convert.ToInt32(verify.ExecuteScalar()), Is.EqualTo(1),
                "Exactly one stamp row must exist after parallel kindle.");
        }
        finally
        {
            // force: the version-gated kindle would otherwise skip, leaving the dropped table unrebuilt
            using var restore = _connection.CreateCommand();
            ForgeKindler.KindleTheForge(restore, Platform.PostgreSQL, forceReKindle: true);
        }
    }

    [Test]
    public void Kindle_ReleasesAdvisoryLock_AfterCompletion()
    {
        // After a kindle completes, the session advisory lock must be released by the finally
        // block. PG advisory locks are REENTRANT per session — calling pg_try_advisory_lock on
        // the SAME session that took the lock succeeds even if release didn't fire (the session
        // already "holds" it). So we must probe from a FRESH connection to actually detect a
        // missing release; from the original session the test would pass trivially.
        using (var c = _connection.CreateCommand())
            ForgeKindler.KindleTheForge(c, Platform.PostgreSQL, forceReKindle: true);

        using var probeConn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL)
            .GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        probeConn.Open();
        using var check = probeConn.CreateCommand();
        check.CommandText =
            "SELECT pg_try_advisory_lock(hashtext(current_database() || ':SchemaSmith_Kindle'))";
        var acquired = check.ExecuteScalar();
        Assert.That(Convert.ToBoolean(acquired), Is.True,
            "Kindle must release its session advisory lock on completion; a fresh session must be able to acquire the same key.");

        // Release on the probe session so we don't leak the lock for any later test.
        check.CommandText =
            "SELECT pg_advisory_unlock(hashtext(current_database() || ':SchemaSmith_Kindle'))";
        check.ExecuteNonQuery();
    }

    /// <summary>
    /// Count how many pg_proc entries exist for a given procedure name in the SchemaSmith schema.
    /// Used to verify overload cleanup during upgrade kindles.
    /// </summary>
    private int CountOverloads(IDbCommand cmd, string procName)
    {
        cmd.CommandText =
            "SELECT COUNT(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace " +
            $"WHERE n.nspname = 'SchemaSmith' AND p.proname = '{procName}'";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
