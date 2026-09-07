// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using MySqlConnector;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.IntegrationTests.MariaDb;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.MariaDb;

/// <summary>
/// A table declaring <c>IsSystemVersioned: true</c> has to actually deploy WITH SYSTEM VERSIONING, not
/// as an ordinary table.
/// <para>Extraction already reads <c>TABLE_TYPE = 'SYSTEM VERSIONED'</c> back into the package (the
/// round-trip half); this is the write half, closing the gap documented on
/// <see cref="Schema.Domain.MariaDb.MariaDbTable.IsSystemVersioned"/> and exercised by hand in
/// <c>Schema.IntegrationTests.MariaDb.WithoutSystemVersioningTests</c> ("because SchemaSmith cannot").
/// A package extracted from a versioned server and redeployed to a fresh database must produce a
/// versioned table, not silently lose the attribute.</para>
/// <para>MariaDB-only: MySQL has no system versioning at any version.</para>
/// <para>Covers both F1S1 (CREATE a new versioned table) and F1S2 (converge an EXISTING table's
/// versioning in <c>SchemaSmith_ModifiedTableQuench</c> STEP 7.5): ADD SYSTEM VERSIONING when the
/// package newly declares it, a hard refuse-by-name (never DROP -- MariaDB purges row history) when the
/// package stops declaring it on an already-versioned table, and a no-op when declared and deployed
/// already agree.</para>
/// </summary>
[Category("MariaDb")]
[TestFixture]
public class SystemVersioningDeployTests : BaseTableQuenchTests
{
    protected override Platform Platform => Platform.MariaDb;
    protected override string MainDbName => FixtureSetup.MainDb;
    protected override string MainConnectionString => FixtureSetup.GetMainDbConnectionString();

    private const string TableName = "SystemVersionedDeployTarget";

    private static string TableJson() => $$"""
        [{
            "Name": "{{TableName}}",
            "IsSystemVersioned": true,
            "Columns": [
                { "Name": "Id", "DataType": "INT", "Nullable": false },
                { "Name": "Description", "DataType": "VARCHAR(100)", "Nullable": true }
            ],
            "Indexes": [ { "Name": "PRIMARY", "IndexColumns": "Id", "PrimaryKey": true, "Unique": true } ]
        }]
        """;

    private string GetTableType(System.Data.IDbCommand cmd, string tableName = TableName)
    {
        cmd.CommandText = $"SELECT TABLE_TYPE FROM INFORMATION_SCHEMA.TABLES "
                          + $"WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = '{tableName}'";
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    // F1S2 (STEP 7.5) convergence: an EXISTING table's declared IsSystemVersioned vs its deployed
    // TABLE_TYPE. Each scenario below uses its own table name (not the F1S1 CREATE-path TableName
    // above) so the three converge scenarios cannot interfere with each other or with the CREATE tests.
    private static string TableJsonFor(string tableName, bool? isSystemVersioned)
    {
        // null omits the property entirely (mirrors a hand-edited package that never mentions it);
        // false declares it explicitly false. Both must be distinguishable in the DROP-refuse test.
        var versionedClause = isSystemVersioned.HasValue
            ? $"\"IsSystemVersioned\": {(isSystemVersioned.Value ? "true" : "false")},"
            : "";
        return $$"""
            [{
                "Name": "{{tableName}}",
                {{versionedClause}}
                "Columns": [
                    { "Name": "Id", "DataType": "INT", "Nullable": false },
                    { "Name": "Description", "DataType": "VARCHAR(100)", "Nullable": true }
                ],
                "Indexes": [ { "Name": "PRIMARY", "IndexColumns": "Id", "PrimaryKey": true, "Unique": true } ]
            }]
            """;
    }

    [Test]
    public void ADeclaredSystemVersionedTableIsCreatedAsSystemVersioned()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.MariaDb).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT SchemaSmith_SupportsSystemVersioning()";
        if (Convert.ToInt32(cmd.ExecuteScalar()) == 0)
            Assert.Ignore("This server cannot declare system versioning (MariaDB 10.3+), so the clause is "
                          + "deliberately suppressed and there is nothing to verify.");

        cmd.CommandText = $"DROP TABLE IF EXISTS `{TableName}`";
        cmd.ExecuteNonQuery();

        RunTableQuenchProc(cmd, TableJson());

        // Asserted against the catalog rather than against the generated SQL: the point is that the
        // table IS system-versioned, not that a particular statement was emitted (Rule 32).
        Assert.That(GetTableType(cmd), Is.EqualTo("SYSTEM VERSIONED"),
            "A table declaring IsSystemVersioned: true must deploy WITH SYSTEM VERSIONING, not as an "
            + "ordinary table — losing this silently would mean the package's declared history-tracking "
            + "attribute never reaches the database.");

        cmd.CommandText = $"DROP TABLE IF EXISTS `{TableName}`";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    /// <summary>
    /// Redeploying the same unchanged package is a clean no-op: no error, and the table stays
    /// SYSTEM VERSIONED.
    /// <para>The table is no longer NewTable on redeploy, so F1S1's CREATE path is skipped entirely;
    /// F1S2's STEP 7.5 convergence runs instead and must also no-op, since declared (true) and deployed
    /// (SYSTEM VERSIONED) already agree — see <see cref="RedeployingAnUnchangedSystemVersionedPackageDoesNotReissueAddSystemVersioning"/>
    /// for the converge-step-specific version of this same idempotency guarantee.</para>
    /// </summary>
    [Test]
    public void RedeployingAnUnchangedSystemVersionedPackageIsANoOp()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.MariaDb).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT SchemaSmith_SupportsSystemVersioning()";
        if (Convert.ToInt32(cmd.ExecuteScalar()) == 0)
            Assert.Ignore("This server cannot declare system versioning (MariaDB 10.3+), so the clause is "
                          + "deliberately suppressed and there is nothing to verify.");

        cmd.CommandText = $"DROP TABLE IF EXISTS `{TableName}`";
        cmd.ExecuteNonQuery();

        RunTableQuenchProc(cmd, TableJson());
        Assert.That(GetTableType(cmd), Is.EqualTo("SYSTEM VERSIONED"), "setup: the table must be created versioned");

        Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, TableJson()),
            "Re-deploying an unchanged package must be a no-op. The table is no longer new on this pass, "
            + "so nothing should attempt to re-issue WITH SYSTEM VERSIONING against an already-versioned table.");

        Assert.That(GetTableType(cmd), Is.EqualTo("SYSTEM VERSIONED"),
            "the table must remain system-versioned after the no-op redeploy");

        cmd.CommandText = $"DROP TABLE IF EXISTS `{TableName}`";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    /// <summary>
    /// F1S2 ADD converge: an ordinary EXISTING table whose package starts declaring
    /// <c>IsSystemVersioned: true</c> must pick up ADD SYSTEM VERSIONING on the next deploy. Additive
    /// and lossless, so it applies unconditionally (gated only on SchemaSmith_SupportsSystemVersioning())
    /// -- unlike the DROP direction, which refuses.
    /// </summary>
    [Test]
    public void AnExistingOrdinaryTableConvergesToSystemVersionedWhenDeclaredTrue()
    {
        const string table = "SysVerConvergeAdd";

        using var conn = DbConnectionFactory.ForPlatform(Platform.MariaDb).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT SchemaSmith_SupportsSystemVersioning()";
        if (Convert.ToInt32(cmd.ExecuteScalar()) == 0)
            Assert.Ignore("This server cannot declare system versioning (MariaDB 10.3+), so there is "
                          + "nothing for the converge step to do.");

        cmd.CommandText = $"DROP TABLE IF EXISTS `{table}`";
        cmd.ExecuteNonQuery();

        RunTableQuenchProc(cmd, TableJsonFor(table, isSystemVersioned: null));
        Assert.That(GetTableType(cmd, table), Is.EqualTo("BASE TABLE"), "setup: table must deploy ordinary (no IsSystemVersioned declared)");

        RunTableQuenchProc(cmd, TableJsonFor(table, isSystemVersioned: true));

        // Outcome, not mechanism (Rule 32): assert what the table IS, not that a particular ALTER TABLE
        // statement was emitted.
        Assert.That(GetTableType(cmd, table), Is.EqualTo("SYSTEM VERSIONED"),
            "Redeclaring IsSystemVersioned: true on an existing ordinary table must converge it to "
            + "system-versioned (ALTER TABLE ... ADD SYSTEM VERSIONING) -- an existing table must not be "
            + "stuck ordinary forever just because it predates the declaration.");

        cmd.CommandText = $"DROP TABLE IF EXISTS `{table}`";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    /// <summary>
    /// F1S2 DROP refuse: once a table is system-versioned, a package that stops declaring
    /// IsSystemVersioned must NOT cause SchemaSmith to DROP SYSTEM VERSIONING -- MariaDB purges the row
    /// history on that DROP, and there is no way to tell "never wanted it" apart from "stopped
    /// declaring it by mistake" from a state diff alone. The quench must fail closed (SIGNAL, refusing
    /// by name) and leave the table's versioning untouched.
    /// </summary>
    [Test]
    public void AnExistingSystemVersionedTableRefusesToDropVersioningWhenNoLongerDeclared()
    {
        const string table = "SysVerConvergeRefuse";

        using var conn = DbConnectionFactory.ForPlatform(Platform.MariaDb).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT SchemaSmith_SupportsSystemVersioning()";
        if (Convert.ToInt32(cmd.ExecuteScalar()) == 0)
            Assert.Ignore("This server cannot declare system versioning (MariaDB 10.3+), so there is "
                          + "nothing for the refuse guard to protect.");

        cmd.CommandText = $"DROP TABLE IF EXISTS `{table}`";
        cmd.ExecuteNonQuery();

        try
        {
            RunTableQuenchProc(cmd, TableJsonFor(table, isSystemVersioned: true));
            Assert.That(GetTableType(cmd, table), Is.EqualTo("SYSTEM VERSIONED"), "setup: table must be created versioned");

            var ex = Assert.Throws<MySqlException>(() => RunTableQuenchProc(cmd, TableJsonFor(table, isSystemVersioned: false)),
                "Declaring IsSystemVersioned: false (or omitting it) on an already-versioned table must "
                + "refuse rather than silently DROP SYSTEM VERSIONING and purge the row history.");
            Assert.That(ex!.Message, Does.Contain(table),
                $"The refusal must name the offending table so the operator knows what to fix. Message: {ex.Message}");

            Assert.That(GetTableType(cmd, table), Is.EqualTo("SYSTEM VERSIONED"),
                "The data-loss guard must leave the table's versioning untouched -- nothing was dropped.");
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS `{table}`";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    /// <summary>
    /// F1S2 idempotent converge: redeploying an unchanged versioned package must not re-issue ADD SYSTEM
    /// VERSIONING against a table that is already versioned (STEP 7.5's converge predicate excludes a
    /// deployed TABLE_TYPE that already reads 'SYSTEM VERSIONED'). Distinct from
    /// <see cref="RedeployingAnUnchangedSystemVersionedPackageIsANoOp"/> above, which covers the same
    /// shape for the shared TableName fixture; this one is dedicated to proving STEP 7.5 specifically
    /// (not just the F1S1 CREATE-path skip) no-ops.
    /// </summary>
    [Test]
    public void RedeployingAnUnchangedSystemVersionedPackageDoesNotReissueAddSystemVersioning()
    {
        const string table = "SysVerConvergeIdempotent";

        using var conn = DbConnectionFactory.ForPlatform(Platform.MariaDb).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT SchemaSmith_SupportsSystemVersioning()";
        if (Convert.ToInt32(cmd.ExecuteScalar()) == 0)
            Assert.Ignore("This server cannot declare system versioning (MariaDB 10.3+), so there is "
                          + "nothing for the converge step to no-op on.");

        cmd.CommandText = $"DROP TABLE IF EXISTS `{table}`";
        cmd.ExecuteNonQuery();

        RunTableQuenchProc(cmd, TableJsonFor(table, isSystemVersioned: true));
        Assert.That(GetTableType(cmd, table), Is.EqualTo("SYSTEM VERSIONED"), "setup: table must be created versioned");

        Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, TableJsonFor(table, isSystemVersioned: true)),
            "Redeploying the same IsSystemVersioned: true declaration against an already-versioned table "
            + "must be a clean no-op -- declared and deployed already agree, so STEP 7.5 must not attempt "
            + "any ALTER.");

        Assert.That(GetTableType(cmd, table), Is.EqualTo("SYSTEM VERSIONED"),
            "the table must remain system-versioned after the no-op redeploy");

        cmd.CommandText = $"DROP TABLE IF EXISTS `{table}`";
        cmd.ExecuteNonQuery();
        conn.Close();
    }
}
