// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

// Phase 0 unsupported-feature policy — PostgreSQL. NULLS NOT DISTINCT is a PG15 feature; below the floor
// it is degraded (warn, default) or refused (fail). Layer-1 (program design §Test strategy):
// schemasmith.version_override forces the < 15 branch on the modern CI container, so no second container
// is needed for CI; the SAME tests pass unchanged against a real postgres:14 container (the override is
// then a no-op that matches reality) — the milestone proof that the guarded catalog read parses on a
// genuine old server. NULLS NOT DISTINCT is emitted only by IndexOnlyQuench (the --IndexOnly path), so
// the policy tests drive that proc; the read-guard smoke drives the normal TableQuench flow, whose
// ModifiedTableQuench builds the existing-index snapshot that reads pg_index.indnullsnotdistinct.
[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.All)]
public class UnsupportedFeaturePolicyTests : BaseTableQuenchTests
{
    private const string Schema = "UnsupportedFeaturePolicyTests";

    [OneTimeSetUp]
    public void Setup()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"CREATE SCHEMA IF NOT EXISTS ""{Schema}"";";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    // The unsupported-feature policy helper: 'warn' by default; 'fail' only when the session setting
    // schemasmith.unsupported_policy is explicitly 'fail'. This is the per-connection lever ProductQuench
    // sets from Target:UnsupportedFeaturePolicy; version-gated emit sites read it to decide degrade-with-
    // warning vs abort.
    [Test]
    public void UnsupportedFeaturePolicy_DefaultsToWarn()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT \"SchemaSmith\".\"UnsupportedFeaturePolicy\"()";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo("warn"), "Default policy must be warn");
        conn.Close();
    }

    [Test]
    public void UnsupportedFeaturePolicy_HonorsFailOverride()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SET schemasmith.unsupported_policy = 'fail'";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "SELECT \"SchemaSmith\".\"UnsupportedFeaturePolicy\"()";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo("fail"), "An explicit 'fail' override must be honored");

        // Any other value falls back to the safe default.
        cmd.CommandText = "SET schemasmith.unsupported_policy = 'nonsense'";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "SELECT \"SchemaSmith\".\"UnsupportedFeaturePolicy\"()";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo("warn"), "An unrecognized value must fall back to warn");
        conn.Close();
    }

    // The compare-side snapshot reads pg_index.indnullsnotdistinct, a PG15+ column. Forced to report
    // PG14, the guarded dynamic snapshot must omit that column — a normal table+unique-index quench must
    // complete without 42703 (undefined column). This read guard is what unblocks PG < 15 at all.
    [Test]
    public void NormalFlow_BelowPg15_TableWithUniqueIndex_DeploysWithoutUndefinedColumnError()
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"Snap_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = "SET schemasmith.version_override = '14';";
        cmd.ExecuteNonQuery();

        var json = $$"""
[{
    "Schema": "{{Schema}}",
    "Name": "{{tableName}}",
    "Columns": [
        { "Name": "Id", "DataType": "INT", "Nullable": false },
        { "Name": "Code", "DataType": "INT", "Nullable": true }
    ],
    "Indexes": [
        { "Name": "UX_{{tableName}}_Code", "Unique": true, "IndexColumns": "Code" }
    ]
}]
""";
        Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, json, productName: $"UFP_{uniqueId}"),
            "A normal quench of a table with a unique index must not 42703 on PG < 15 (guarded indnullsnotdistinct read).");

        Assert.That(IndexExists(cmd, tableName, $"UX_{tableName}_Code"), Is.True, "unique index must exist");

        cmd.CommandText = $@"RESET schemasmith.version_override; DROP TABLE ""{Schema}"".""{tableName}"";";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    // warn (default): a unique index declared NULLS NOT DISTINCT on a < 15 target is created WITHOUT the
    // clause and an unsupportedDowngrade manifest row is recorded naming it. Deploy succeeds.
    [Test]
    public void NullsNotDistinct_BelowPg15_WarnPolicy_CreatesIndexWithoutClause_AndRecordsDowngrade()
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"WarnNnd_{uniqueId}";
        var indexName = $"UX_{tableName}_Code";

        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        // Table exists but the unique index does not — so IndexOnlyQuench emits the CREATE UNIQUE INDEX.
        cmd.CommandText = $@"
SET schemasmith.version_override = '14';
CREATE TABLE ""{Schema}"".""{tableName}"" (""Id"" INT NOT NULL, ""Code"" INT NULL);";
        cmd.ExecuteNonQuery();

        IndexOnlyQuenchNnd(cmd, tableName, indexName, productName: $"UFP_{uniqueId}");

        Assert.That(IndexExists(cmd, tableName, indexName), Is.True, "unique index must be created");

        cmd.CommandText = $@"SELECT COUNT(*) FROM ""SchemaSmith"".""ChangeAudit""
                             WHERE ""ActionType"" = 'downgraded'
                               AND ""ObjectName"" = '{Schema}.{tableName}.{indexName}'
                               AND ""ObjectType"" = 'NULLS NOT DISTINCT (PG15)';";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1),
            "a downgrade manifest row must name the index that lost NULLS NOT DISTINCT");

        cmd.CommandText = $@"RESET schemasmith.version_override; DROP TABLE ""{Schema}"".""{tableName}"";";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    // No phantom churn: on a < 15 target the declared NULLS NOT DISTINCT is neutralised to false for the
    // compare, so a second quench of the same index must not drop-and-recreate it (stable index oid).
    [Test]
    public void NullsNotDistinct_BelowPg15_Warn_SecondQuench_DoesNotRecreateIndex()
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"NoChurn_{uniqueId}";
        var indexName = $"UX_{tableName}_Code";
        var productName = $"UFP_{uniqueId}";

        // Each quench is a distinct work unit on its own connection (fresh session-scoped temp tables),
        // mirroring how the --IndexOnly flow runs. The index is a real object and persists across both.
        uint firstOid;
        using (var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString))
        {
            conn.Open();
            conn.ChangeDatabase(_mainDb);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 300;
            cmd.CommandText = $@"
SET schemasmith.version_override = '14';
CREATE TABLE ""{Schema}"".""{tableName}"" (""Id"" INT NOT NULL, ""Code"" INT NULL);";
            cmd.ExecuteNonQuery();

            IndexOnlyQuenchNnd(cmd, tableName, indexName, productName);
            firstOid = IndexOid(cmd, indexName);
            Assert.That(firstOid, Is.GreaterThan(0u), "index must exist after first quench");
            conn.Close();
        }

        using (var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString))
        {
            conn.Open();
            conn.ChangeDatabase(_mainDb);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 300;
            cmd.CommandText = "SET schemasmith.version_override = '14';";
            cmd.ExecuteNonQuery();

            IndexOnlyQuenchNnd(cmd, tableName, indexName, productName);
            var secondOid = IndexOid(cmd, indexName);

            Assert.That(secondOid, Is.EqualTo(firstOid),
                "the index must not be dropped/recreated on a repeat quench (NULLS NOT DISTINCT neutralised, not churned) on PG < 15");

            cmd.CommandText = $@"RESET schemasmith.version_override; DROP TABLE ""{Schema}"".""{tableName}"";";
            cmd.ExecuteNonQuery();
            conn.Close();
        }
    }

    // fail (opt-in): a below-15 target with a declared NULLS NOT DISTINCT index aborts the quench with a
    // clear "requires PostgreSQL 15" message rather than silently degrading.
    [Test]
    public void NullsNotDistinct_BelowPg15_FailPolicy_AbortsWithRequiresPg15()
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"FailNnd_{uniqueId}";
        var indexName = $"UX_{tableName}_Code";

        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        cmd.CommandText = $@"
SET schemasmith.version_override = '14';
SET schemasmith.unsupported_policy = 'fail';
CREATE TABLE ""{Schema}"".""{tableName}"" (""Id"" INT NOT NULL, ""Code"" INT NULL);";
        cmd.ExecuteNonQuery();

        var ex = Assert.Catch(() => IndexOnlyQuenchNnd(cmd, tableName, indexName, productName: $"UFP_{uniqueId}"));
        Assert.That(ex!.Message, Does.Contain("requires PostgreSQL 15"),
            "the fail policy must abort naming the required version");

        cmd.CommandText = $@"RESET schemasmith.version_override; RESET schemasmith.unsupported_policy; DROP TABLE ""{Schema}"".""{tableName}"";";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    // The FULL TableQuench flow (not just --IndexOnly) must honor NULLS NOT DISTINCT on a unique index —
    // it previously only applied through IndexOnlyQuench, so a normal deploy silently dropped the clause.
    [Test]
    public void NormalFlow_NullsNotDistinct_AppliedOnPg15Plus()
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"NfNnd_{uniqueId}";
        var indexName = $"UX_{tableName}_Code";

        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        // Skip on a genuinely-old server (the clause cannot exist there); the sandbox is modern.
        cmd.CommandText = "SELECT current_setting('server_version_num')::int / 10000";
        if (Convert.ToInt32(cmd.ExecuteScalar()) < 15) Assert.Ignore("requires PostgreSQL 15+");

        var json = $$"""
[{
    "Schema": "{{Schema}}",
    "Name": "{{tableName}}",
    "Columns": [
        { "Name": "Id", "DataType": "INT", "Nullable": false },
        { "Name": "Code", "DataType": "INT", "Nullable": true }
    ],
    "Indexes": [
        { "Name": "{{indexName}}", "Unique": true, "IndexColumns": "Code", "NullsNotDistinct": true }
    ]
}]
""";
        RunTableQuenchProc(cmd, json, productName: $"NF_{uniqueId}");

        cmd.CommandText = $@"SELECT idx.indnullsnotdistinct FROM pg_index idx
                             JOIN pg_class i ON i.oid = idx.indexrelid WHERE i.relname = '{indexName}';";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo(true),
            "a normal (full) TableQuench deploy must apply NULLS NOT DISTINCT on PG15+, not only --IndexOnly");

        // Idempotent: a second normal quench must not drop/recreate the index over NND.
        var firstOid = IndexOid(cmd, indexName);
        RunTableQuenchProc(cmd, json, productName: $"NF_{uniqueId}");
        Assert.That(IndexOid(cmd, indexName), Is.EqualTo(firstOid), "no phantom churn on re-quench");

        cmd.CommandText = $@"DROP TABLE ""{Schema}"".""{tableName}"";";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    // Below 15 the normal flow degrades identically to the --IndexOnly path: clause omitted + a
    // downgrade manifest row recorded.
    [Test]
    public void NormalFlow_NullsNotDistinct_BelowPg15_RecordsDowngrade()
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"NfWarn_{uniqueId}";
        var indexName = $"UX_{tableName}_Code";

        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = "SET schemasmith.version_override = '14';";
        cmd.ExecuteNonQuery();

        var json = $$"""
[{
    "Schema": "{{Schema}}",
    "Name": "{{tableName}}",
    "Columns": [
        { "Name": "Id", "DataType": "INT", "Nullable": false },
        { "Name": "Code", "DataType": "INT", "Nullable": true }
    ],
    "Indexes": [
        { "Name": "{{indexName}}", "Unique": true, "IndexColumns": "Code", "NullsNotDistinct": true }
    ]
}]
""";
        RunTableQuenchProc(cmd, json, productName: $"NFW_{uniqueId}");

        Assert.That(IndexExists(cmd, tableName, indexName), Is.True, "index created (without the clause)");
        cmd.CommandText = $@"SELECT COUNT(*) FROM ""SchemaSmith"".""ChangeAudit""
                             WHERE ""ActionType"" = 'downgraded'
                               AND ""ObjectName"" = '{Schema}.{tableName}.{indexName}'
                               AND ""ObjectType"" = 'NULLS NOT DISTINCT (PG15)';";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1),
            "the normal flow must record a downgrade manifest row below PG15");

        cmd.CommandText = $@"RESET schemasmith.version_override; DROP TABLE ""{Schema}"".""{tableName}"";";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    // The --IndexOnly path must also actually APPLY NULLS NOT DISTINCT on PG15+ (its emit was never
    // exercised with the clause present — the policy tests all force override=14, which omits it).
    [Test]
    public void IndexOnly_NullsNotDistinct_AppliedOnPg15Plus()
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"IoNnd_{uniqueId}";
        var indexName = $"UX_{tableName}_Code";

        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        cmd.CommandText = "SELECT current_setting('server_version_num')::int / 10000";
        if (Convert.ToInt32(cmd.ExecuteScalar()) < 15) Assert.Ignore("requires PostgreSQL 15+");

        cmd.CommandText = $@"CREATE TABLE ""{Schema}"".""{tableName}"" (""Id"" INT NOT NULL, ""Code"" INT NULL);";
        cmd.ExecuteNonQuery();

        IndexOnlyQuenchNnd(cmd, tableName, indexName, productName: $"IO_{uniqueId}");

        cmd.CommandText = $@"SELECT idx.indnullsnotdistinct FROM pg_index idx
                             JOIN pg_class i ON i.oid = idx.indexrelid WHERE i.relname = '{indexName}';";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo(true),
            "the --IndexOnly path must apply NULLS NOT DISTINCT on PG15+");

        cmd.CommandText = $@"DROP TABLE ""{Schema}"".""{tableName}"";";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    // Table access method. CREATE TABLE ... USING has worked since PostgreSQL 12, so a NEW table gets its declared access
    // method on every supported version; only changing an EXISTING table's method (ALTER TABLE ... SET ACCESS METHOD)
    // needs 15. Below 15 that change degrades: warn keeps the table where it is and records a downgrade, fail aborts.
    // The earlier tests declared an access method that does not exist ("columnar") on a new table and asserted the
    // downgrade, which pinned PG-058: a new table silently lost its declared method below 15. These use a real second
    // access method built on the heap handler, which every supported version has.
    private const string SecondAccessMethod = "ss_heap2";

    private static void EnsureSecondAccessMethod(IDbCommand cmd)
    {
        cmd.CommandText = $"DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_am WHERE amname = '{SecondAccessMethod}') THEN " +
                          $"CREATE ACCESS METHOD {SecondAccessMethod} TYPE TABLE HANDLER heap_tableam_handler; END IF; END $$;";
        cmd.ExecuteNonQuery();
    }

    private static string AccessMethodTableJson(string tableName) => $$"""
[{
    "Schema": "{{Schema}}",
    "Name": "{{tableName}}",
    "AccessMethod": "{{SecondAccessMethod}}",
    "Columns": [
        { "Name": "Id", "DataType": "INT", "Nullable": false }
    ]
}]
""";

    private static string LiveAccessMethod(IDbCommand cmd, string tableName)
    {
        cmd.CommandText = $@"SELECT am.amname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                             JOIN pg_am am ON am.oid = c.relam WHERE n.nspname = '{Schema}' AND c.relname = '{tableName}';";
        return cmd.ExecuteScalar()?.ToString();
    }

    private static int AccessMethodDowngrades(IDbCommand cmd, string tableName)
    {
        cmd.CommandText = $@"SELECT COUNT(*) FROM ""SchemaSmith"".""ChangeAudit""
                             WHERE ""ActionType"" = 'downgraded'
                               AND ""ObjectName"" = '{Schema}.{tableName}'
                               AND ""ObjectType"" = 'table access method (PG15)';";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private IDbCommand OpenBelowPg15(IDbConnection conn, string policy)
    {
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        EnsureSecondAccessMethod(cmd);
        cmd.CommandText = $"SET schemasmith.version_override = '14'; SET schemasmith.unsupported_policy = '{policy}';";
        cmd.ExecuteNonQuery();
        return cmd;
    }

    private static void ResetAndDrop(IDbCommand cmd, string tableName)
    {
        cmd.CommandText = $@"RESET schemasmith.version_override; RESET schemasmith.unsupported_policy;
                             DROP TABLE IF EXISTS ""{Schema}"".""{tableName}"";";
        cmd.ExecuteNonQuery();
    }

    [Test]
    public void AccessMethod_NewTable_IsCreatedWithIt_EvenBelowPg15()
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"NewAm_{uniqueId}";
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        using var cmd = OpenBelowPg15(conn, "warn");
        try
        {
            RunTableQuenchProc(cmd, AccessMethodTableJson(tableName), productName: $"AMN_{uniqueId}");

            Assert.That(LiveAccessMethod(cmd, tableName), Is.EqualTo(SecondAccessMethod));
            Assert.That(AccessMethodDowngrades(cmd, tableName), Is.Zero, "nothing was lost, so nothing is downgraded");
        }
        finally { ResetAndDrop(cmd, tableName); }
    }

    [Test]
    public void AccessMethod_ExistingTable_BelowPg15_WarnPolicy_StaysWhereItIs_AndRecordsDowngrade()
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"WarnAm_{uniqueId}";
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        using var cmd = OpenBelowPg15(conn, "warn");
        try
        {
            cmd.CommandText = $@"CREATE TABLE ""{Schema}"".""{tableName}"" (""Id"" INT NOT NULL) USING heap;";
            cmd.ExecuteNonQuery();

            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, AccessMethodTableJson(tableName), productName: $"AMW_{uniqueId}"),
                "changing an existing table's access method must degrade below PG15, not error on SET ACCESS METHOD");

            Assert.That(LiveAccessMethod(cmd, tableName), Is.EqualTo("heap"));
            Assert.That(AccessMethodDowngrades(cmd, tableName), Is.EqualTo(1));
        }
        finally { ResetAndDrop(cmd, tableName); }
    }

    [Test]
    public void AccessMethod_ExistingTable_AlreadyOnIt_BelowPg15_RecordsNothing()
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"SameAm_{uniqueId}";
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        using var cmd = OpenBelowPg15(conn, "warn");
        try
        {
            cmd.CommandText = $@"CREATE TABLE ""{Schema}"".""{tableName}"" (""Id"" INT NOT NULL) USING {SecondAccessMethod};";
            cmd.ExecuteNonQuery();

            RunTableQuenchProc(cmd, AccessMethodTableJson(tableName), productName: $"AMS_{uniqueId}");

            Assert.That(AccessMethodDowngrades(cmd, tableName), Is.Zero);
        }
        finally { ResetAndDrop(cmd, tableName); }
    }

    [Test]
    public void AccessMethod_ExistingTable_BelowPg15_FailPolicy_AbortsWithRequiresPg15()
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"FailAm_{uniqueId}";
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        using var cmd = OpenBelowPg15(conn, "fail");
        try
        {
            cmd.CommandText = $@"CREATE TABLE ""{Schema}"".""{tableName}"" (""Id"" INT NOT NULL) USING heap;";
            cmd.ExecuteNonQuery();

            var ex = Assert.Catch(() => RunTableQuenchProc(cmd, AccessMethodTableJson(tableName), productName: $"AMF_{uniqueId}"));
            Assert.That(ex!.Message, Does.Contain("requires PostgreSQL 15"));
        }
        finally { ResetAndDrop(cmd, tableName); }
    }

    // Turning a generated column into a plain one keeps its values from PostgreSQL 13 (ALTER COLUMN ... DROP EXPRESSION).
    // Below 13 the column is dropped and re-added, so its values are lost (PG-110). That path was unregistered and
    // left no downgrade row; warn now records one per column, and fail refuses.
    private const string UngenerateObjectType = "un-generated column, values not kept (PG13)";

    private static string PlainColumnTableJson(string tableName) => $$"""
[{
    "Schema": "{{Schema}}",
    "Name": "{{tableName}}",
    "Columns": [
        { "Name": "Id", "DataType": "INT", "Nullable": false },
        { "Name": "Doubled", "DataType": "INT", "Nullable": true }
    ]
}]
""";

    private void CreateGeneratedTable(IDbCommand cmd, string tableName)
    {
        cmd.CommandText = $@"CREATE TABLE ""{Schema}"".""{tableName}"" (""Id"" INT NOT NULL, ""Doubled"" INT GENERATED ALWAYS AS (""Id"" * 2) STORED);
                             INSERT INTO ""{Schema}"".""{tableName}"" (""Id"") VALUES (21);";
        cmd.ExecuteNonQuery();
    }

    [Test]
    public void Ungenerate_BelowPg13_WarnPolicy_RecordsThatTheValuesWereNotKept()
    {
        var tableName = $"Ungen_{Guid.NewGuid().ToString("N")[..8]}";
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        try
        {
            CreateGeneratedTable(cmd, tableName);
            cmd.CommandText = "SET schemasmith.version_override = '12';";
            cmd.ExecuteNonQuery();

            RunTableQuenchProc(cmd, PlainColumnTableJson(tableName), productName: tableName);

            cmd.CommandText = $@"SELECT is_generated FROM information_schema.columns
                                 WHERE table_schema = '{Schema}' AND table_name = '{tableName}' AND column_name = 'Doubled';";
            var generated = cmd.ExecuteScalar()?.ToString();
            cmd.CommandText = $@"SELECT COUNT(*) FROM ""SchemaSmith"".""ChangeAudit"" WHERE ""ActionType"" = 'downgraded'
                                   AND ""ObjectType"" = '{UngenerateObjectType}' AND ""ObjectName"" = '{Schema}.{tableName}.Doubled';";
            var downgrades = Convert.ToInt32(cmd.ExecuteScalar());
            Assert.Multiple(() =>
            {
                Assert.That(generated, Is.EqualTo("NEVER"), "the column must be plain");
                Assert.That(downgrades, Is.EqualTo(1), "the lost values must be on the record");
            });
        }
        finally
        {
            cmd.CommandText = $@"RESET schemasmith.version_override; DROP TABLE IF EXISTS ""{Schema}"".""{tableName}"";";
            cmd.ExecuteNonQuery();
        }
    }

    [Test]
    public void Ungenerate_BelowPg13_FailPolicy_Aborts_AndTheColumnKeepsItsValues()
    {
        var tableName = $"UngenF_{Guid.NewGuid().ToString("N")[..8]}";
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        try
        {
            CreateGeneratedTable(cmd, tableName);
            cmd.CommandText = "SET schemasmith.version_override = '12'; SET schemasmith.unsupported_policy = 'fail';";
            cmd.ExecuteNonQuery();

            var ex = Assert.Catch(() => RunTableQuenchProc(cmd, PlainColumnTableJson(tableName), productName: tableName));
            Assert.That(ex!.Message, Does.Contain("PostgreSQL 13"));

            cmd.CommandText = $@"RESET schemasmith.version_override; RESET schemasmith.unsupported_policy;
                                 SELECT ""Doubled"" FROM ""{Schema}"".""{tableName}"";";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(42), "a refused deploy must not drop the column");
        }
        finally
        {
            cmd.CommandText = $@"RESET schemasmith.version_override; RESET schemasmith.unsupported_policy;
                                 DROP TABLE IF EXISTS ""{Schema}"".""{tableName}"";";
            cmd.ExecuteNonQuery();
        }
    }

    [Test]
    public void Ungenerate_AtPg13_KeepsTheValues_AndRecordsNothing()
    {
        var tableName = $"Ungen13_{Guid.NewGuid().ToString("N")[..8]}";
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        try
        {
            CreateGeneratedTable(cmd, tableName);
            cmd.CommandText = "SET schemasmith.version_override = '13';";
            cmd.ExecuteNonQuery();

            RunTableQuenchProc(cmd, PlainColumnTableJson(tableName), productName: tableName);

            cmd.CommandText = $@"SELECT ""Doubled"" FROM ""{Schema}"".""{tableName}"";";
            var value = Convert.ToInt32(cmd.ExecuteScalar());
            cmd.CommandText = $@"SELECT COUNT(*) FROM ""SchemaSmith"".""ChangeAudit"" WHERE ""ObjectType"" = '{UngenerateObjectType}'
                                   AND ""ObjectName"" = '{Schema}.{tableName}.Doubled';";
            var downgrades = Convert.ToInt32(cmd.ExecuteScalar());
            Assert.Multiple(() =>
            {
                Assert.That(value, Is.EqualTo(42));
                Assert.That(downgrades, Is.Zero);
            });
        }
        finally
        {
            cmd.CommandText = $@"RESET schemasmith.version_override; DROP TABLE IF EXISTS ""{Schema}"".""{tableName}"";";
            cmd.ExecuteNonQuery();
        }
    }

    // warn (default): a VIRTUAL generated column on a < 18 target is skipped entirely (STORED siblings
    // are unaffected) and an unsupportedDowngrade manifest row is recorded naming it. Deploy succeeds.
    [Test]
    public void VirtualGeneratedColumn_BelowPg18_WarnPolicy_SkipsColumn_AndRecordsDowngrade()
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"WarnVirt_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = "SET schemasmith.version_override = '17';";
        cmd.ExecuteNonQuery();

        var json = $$"""
[{
    "Schema": "{{Schema}}",
    "Name": "{{tableName}}",
    "Columns": [
        { "Name": "Qty", "DataType": "INT", "Nullable": false },
        { "Name": "DoubleQty", "DataType": "INT", "Nullable": true,
          "Generated": "ALWAYS", "GenerationExpression": "(\"Qty\" * 2)" },
        { "Name": "TripleQty", "DataType": "INT", "Nullable": true,
          "Generated": "ALWAYS", "GenerationExpression": "(\"Qty\" * 3)", "Virtual": true }
    ]
}]
""";
        Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, json, productName: $"UFPV_{uniqueId}"),
            "a VIRTUAL generated column must degrade (skipped) below PG18, not raise a raw syntax error");

        cmd.CommandText = $@"SELECT is_generated FROM information_schema.columns
                             WHERE table_schema = '{Schema}' AND table_name = '{tableName}' AND column_name = 'DoubleQty';";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("ALWAYS"),
            "a STORED generated column on the same table must still be created — the rest of the deploy proceeds");

        cmd.CommandText = $@"SELECT COUNT(*) FROM information_schema.columns
                             WHERE table_schema = '{Schema}' AND table_name = '{tableName}' AND column_name = 'TripleQty';";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(0), "the VIRTUAL column must not be created");

        cmd.CommandText = $@"SELECT COUNT(*) FROM ""SchemaSmith"".""ChangeAudit""
                             WHERE ""ActionType"" = 'downgraded'
                               AND ""ObjectName"" = '{Schema}.{tableName}.TripleQty'
                               AND ""ObjectType"" = 'VIRTUAL generated column (PG18)';";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1),
            "a downgrade manifest row must name the column that lost VIRTUAL storage");

        cmd.CommandText = $@"RESET schemasmith.version_override; DROP TABLE ""{Schema}"".""{tableName}"";";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    // fail (opt-in): a below-18 target declaring a VIRTUAL generated column aborts the quench with a clear
    // "require PostgreSQL 18" message rather than surfacing PostgreSQL's own generated-column syntax error.
    [Test]
    public void VirtualGeneratedColumn_BelowPg18_FailPolicy_AbortsWithRequiresPg18()
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"FailVirt_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = "SET schemasmith.version_override = '17'; SET schemasmith.unsupported_policy = 'fail';";
        cmd.ExecuteNonQuery();

        var json = $$"""
[{
    "Schema": "{{Schema}}",
    "Name": "{{tableName}}",
    "Columns": [
        { "Name": "Qty", "DataType": "INT", "Nullable": false },
        { "Name": "TripleQty", "DataType": "INT", "Nullable": true,
          "Generated": "ALWAYS", "GenerationExpression": "(\"Qty\" * 3)", "Virtual": true }
    ]
}]
""";
        var ex = Assert.Catch(() => RunTableQuenchProc(cmd, json, productName: $"UFPV_{uniqueId}"));
        Assert.That(ex!.Message, Does.Contain("require PostgreSQL 18"),
            "the fail policy must abort naming the required version, not surface a raw PostgreSQL syntax error");

        cmd.CommandText = $@"RESET schemasmith.version_override; RESET schemasmith.unsupported_policy;
                             DROP TABLE IF EXISTS ""{Schema}"".""{tableName}"";";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    private void IndexOnlyQuenchNnd(IDbCommand cmd, string tableName, string indexName, string productName)
    {
        var json = $$"""
[{
    "Schema": "{{Schema}}",
    "Name": "{{tableName}}",
    "Indexes": [
        { "Name": "{{indexName}}", "Unique": true, "IndexColumns": "Code", "NullsNotDistinct": true }
    ]
}]
""";
        cmd.CommandText = $@"CALL ""SchemaSmith"".""IndexOnlyQuench""(p_ProductName := '{productName}', p_TableDefinitions := '{json.Replace("'", "''")}', p_DropUnknownIndexes := false);";
        cmd.ExecuteNonQuery();
    }

    private bool IndexExists(IDbCommand cmd, string tableName, string indexName)
    {
        cmd.CommandText = $@"SELECT COUNT(*) FROM pg_indexes WHERE schemaname = '{Schema}' AND tablename = '{tableName}' AND indexname = '{indexName}';";
        return Convert.ToInt32(cmd.ExecuteScalar()) == 1;
    }

    private uint IndexOid(IDbCommand cmd, string indexName)
    {
        cmd.CommandText = $@"SELECT COALESCE(to_regclass('""{Schema}"".""{indexName}""')::oid, 0)::oid;";
        var result = cmd.ExecuteScalar();
        return result == null || result == DBNull.Value ? 0u : Convert.ToUInt32(result);
    }
}
