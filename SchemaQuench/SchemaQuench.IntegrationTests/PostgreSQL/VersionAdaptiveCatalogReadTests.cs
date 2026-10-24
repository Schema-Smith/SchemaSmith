// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

// Phase 1 (floor 14->12) version-adaptive PostgreSQL catalog reads. Unlike the PG15 NULLS NOT DISTINCT
// policy (which has both a read AND an emit and can be simulated via schemasmith.version_override), a
// pure catalog READ such as pg_attribute.attcompression (PG14) is keyed on the REAL server version — the
// physical column-existence question — so an override on the modern sandbox cannot exercise the fallback
// branch (the column still exists on PG16). The universal test therefore asserts the SERIALIZED value
// ('DEFAULT' for an uncompressed column) which is identical on both branches: on PG14+ the helper reads
// the real (uncompressed) attcompression; on a genuine PG13 container the helper skips the read entirely.
// The milestone proof is the same file running green against a real postgres:13 leg (no 42703). The
// PG14+-only test proves the >=14 branch returns actual catalog values, so 'DEFAULT' is a real read, not
// a fallback that happens to match.
[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.Self)]
public class VersionAdaptiveCatalogReadTests : BaseTableQuenchTests
{
    private const string Schema = "VersionAdaptiveCatalogReadTests";

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

    // PG12: BootstrapTableQuench declared each column's identity TWICE — Step 1's CREATE TABLE builds the
    // full column list inline, then Step 2 emitted ADD COLUMN IF NOT EXISTS for EVERY column. On PostgreSQL
    // 12, ADD COLUMN IF NOT EXISTS ... GENERATED AS IDENTITY leaks an orphan owned sequence even while it
    // skips the already-present column, so the identity column ends up owning two sequences and every later
    // INSERT fails with "more than one owned sequence found". ChangeAudit.Id is such a column, so the
    // quench's own audit INSERT broke on PG12. After the fix (Step 2 guarded by information_schema so it
    // only ADDs genuinely-missing columns), the column owns exactly one sequence and inserts succeed on
    // every version.
    [Test]
    public void ChangeAudit_IdentityColumn_OwnsExactlyOneSequence_AndInsertSucceeds()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        cmd.CommandText = @"
SELECT count(*) FROM pg_depend d
  JOIN pg_class s ON s.oid = d.objid AND s.relkind = 'S'
 WHERE d.refobjid = '""SchemaSmith"".""ChangeAudit""'::regclass
   AND d.refobjsubid = (SELECT attnum FROM pg_attribute
                          WHERE attrelid = '""SchemaSmith"".""ChangeAudit""'::regclass AND attname = 'Id');";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1),
            "ChangeAudit.Id must own exactly one sequence (PG12 leaked a second via Step-2 ADD COLUMN IF NOT EXISTS + IDENTITY)");

        cmd.CommandText = @"INSERT INTO ""SchemaSmith"".""ChangeAudit"" (""SessionId"", ""ObjectType"", ""ObjectName"", ""ActionType"")
                            VALUES (pg_backend_pid(), 'table', 'VacrTest.OwnedSeq', 'created');";
        Assert.DoesNotThrow(() => cmd.ExecuteNonQuery(),
            "INSERT into ChangeAudit must not hit 'more than one owned sequence found' (PG12 duplicate owned sequence)");

        conn.Close();
    }

    // Per-column compression (ALTER COLUMN ... SET COMPRESSION) is PostgreSQL 14. Authoring a non-default
    // compression against a below-14 target must NOT emit SET COMPRESSION (a 42601 syntax error there); the
    // emit is gated off and routed through the unsupported-feature policy (warn: skip + downgrade manifest).
    // On PG14+ it applies normally.
    [Test]
    public void ModifiedColumn_AuthoredCompression_BelowPg14_DoesNotEmitSetCompression_AndManifests()
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"Cmp_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        var createJson = $$"""
[{ "Schema": "{{Schema}}", "Name": "{{tableName}}",
   "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": false },
                { "Name": "Notes", "DataType": "TEXT", "Nullable": true } ] }]
""";
        RunTableQuenchProc(cmd, createJson, productName: $"CMP_{uniqueId}");

        // Re-quench with a non-default compression authored on the TEXT column.
        var compJson = $$"""
[{ "Schema": "{{Schema}}", "Name": "{{tableName}}",
   "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": false },
                { "Name": "Notes", "DataType": "TEXT", "Nullable": true, "Compression": "pglz" } ] }]
""";
        Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, compJson, productName: $"CMP_{uniqueId}"),
            "authoring per-column compression on a below-14 target must not emit SET COMPRESSION (42601)");

        cmd.CommandText = "SELECT current_setting('server_version_num')::int / 10000";
        if (Convert.ToInt32(cmd.ExecuteScalar()) < 14)
        {
            cmd.CommandText = $@"SELECT COUNT(*) FROM ""SchemaSmith"".""ChangeAudit""
                                 WHERE ""ActionType"" = 'downgraded' AND ""ObjectType"" = 'per-column compression (PG14)'
                                   AND ""ObjectName"" = '{Schema}.{tableName}.Notes';";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.GreaterThanOrEqualTo(1),
                "a downgrade manifest row must name the column whose compression was skipped");
        }

        cmd.CommandText = $@"DROP TABLE ""{Schema}"".""{tableName}"";";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    // The compare-side serialization (GenerateTableJSON) and the modify-side snapshot (ModifiedTableQuench's
    // temp_existing_columns) both read pg_attribute.attcompression, a PG14 column. Below 14 the read is a
    // plan-time 42703; the ColumnCompression helper reads it via EXECUTE only on a server that has it. A
    // normal quench of a table (create then re-quench, so ModifiedTableQuench reads the existing columns)
    // must not 42703, and the serialized Compression of an uncompressed column must be 'DEFAULT' on every
    // supported version. This is what unblocks PG < 14 at all.
    [Test]
    public void NormalFlow_TableWithColumns_CompressionRead_DoesNotThrowAndSerializesDefault()
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"Comp_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        var json = $$"""
[{
    "Schema": "{{Schema}}",
    "Name": "{{tableName}}",
    "Columns": [
        { "Name": "Id", "DataType": "INT", "Nullable": false },
        { "Name": "Notes", "DataType": "TEXT", "Nullable": true }
    ]
}]
""";
        // First quench creates the table; the second drives ModifiedTableQuench, whose existing-columns
        // snapshot reads attcompression via the helper. Neither may 42703 on a below-14 server.
        Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, json, productName: $"VAC_{uniqueId}"),
            "creating a table must not 42703 on the attcompression read below PG14");
        Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, json, productName: $"VAC_{uniqueId}"),
            "re-quenching (ModifiedTableQuench existing-columns snapshot) must not 42703 on the attcompression read below PG14");

        // The compare-side read: an uncompressed column serializes Compression = DEFAULT on all versions.
        cmd.CommandText = $@"SELECT ""SchemaSmith"".""GenerateTableJSON""('{Schema}', '{tableName}');";
        var tableJson = (string)cmd.ExecuteScalar()!;
        Assert.That(tableJson, Does.Contain("\"Compression\": \"DEFAULT\""),
            "an uncompressed column must serialize Compression = DEFAULT (the value shared by the >=14 read and the below-14 fallback)");

        cmd.CommandText = $@"DROP TABLE ""{Schema}"".""{tableName}"";";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    // Proves the helper's >=14 branch returns the ACTUAL catalog value, so 'DEFAULT' elsewhere is a genuine
    // read of an uncompressed column, not a fallback that coincidentally matches. pglz is always available
    // on PG14+ (unlike lz4, which is a build option). Skipped on a genuinely-old server, where SET
    // COMPRESSION itself does not exist.
    [Test]
    public void CompressionRead_Pg14Plus_ReturnsActualCompressionFromCatalog()
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"CompReal_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        cmd.CommandText = "SELECT current_setting('server_version_num')::int / 10000";
        if (Convert.ToInt32(cmd.ExecuteScalar()) < 14) Assert.Ignore("requires PostgreSQL 14+ (per-column SET COMPRESSION)");

        cmd.CommandText = $@"
CREATE TABLE ""{Schema}"".""{tableName}"" (""Id"" INT NOT NULL, ""Notes"" TEXT NULL);
ALTER TABLE ""{Schema}"".""{tableName}"" ALTER COLUMN ""Notes"" SET COMPRESSION pglz;";
        cmd.ExecuteNonQuery();

        cmd.CommandText = $@"SELECT ""SchemaSmith"".""GenerateTableJSON""('{Schema}', '{tableName}');";
        var tableJson = (string)cmd.ExecuteScalar()!;
        Assert.That(tableJson, Does.Contain("\"Compression\": \"pglz\""),
            "the >=14 branch must read the real attcompression value (pglz) from the catalog");

        cmd.CommandText = $@"DROP TABLE ""{Schema}"".""{tableName}"";";
        cmd.ExecuteNonQuery();
        conn.Close();
    }
}
