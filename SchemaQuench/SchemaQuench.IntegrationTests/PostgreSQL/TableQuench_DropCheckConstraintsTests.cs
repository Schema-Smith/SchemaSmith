// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_DropCheckConstraintsTests : BaseTableQuenchTests
{
    // Non-vacuous: both checks are removed from the JSON in the same quench.
    // ChkSuppressed sets DropCheckConstraintsRemovedFromProduct:false -> its check survives.
    // ChkControl omits the flag (inherits cascade default true) -> its check drops.
    [Test]
    public void TableQuench_ShouldSuppressCheckDropWhenTableFlagIsFalse()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM pg_constraint WHERE conname = 'CK_ChkSuppressed_Pos' AND contype = 'c')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True, "CK_ChkSuppressed_Pos should still exist (suppressed by table flag).");

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM pg_constraint WHERE conname = 'CK_ChkControl_Pos' AND contype = 'c')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.False, "CK_ChkControl_Pos should be gone (no suppression flag).");

        conn.Close();
    }

    // The split: a MODIFIED check (same name, changed expression) must still be dropped and
    // recreated even when DropCheckConstraintsRemovedFromProduct:false. CK_ChkModified_Range was
    // created as "Val" > 0 and the JSON redefines it "Val" > 100; it must converge to the new form.
    [Test]
    public void TableQuench_ModifiedCheckStillReconcilesWhenTableFlagIsFalse()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'CK_ChkModified_Range' AND contype = 'c'";
        var def = cmd.ExecuteScalar() as string ?? "";
        Assert.That(def, Does.Contain("100"),
            "CK_ChkModified_Range must be reconciled to the new expression (> 100) despite DropCheckConstraintsRemovedFromProduct:false.");

        conn.Close();
    }

    [OneTimeSetUp]
    public void Setup()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        cmd.CommandText = @"
CREATE SCHEMA ""DropChkTests"";
CREATE TABLE ""DropChkTests"".""ChkSuppressed"" (""Id"" INT NOT NULL, ""Val"" INT, CONSTRAINT ""CK_ChkSuppressed_Pos"" CHECK (""Val"" > 0));
CREATE TABLE ""DropChkTests"".""ChkControl"" (""Id"" INT NOT NULL, ""Val"" INT, CONSTRAINT ""CK_ChkControl_Pos"" CHECK (""Val"" > 0));
CREATE TABLE ""DropChkTests"".""ChkModified"" (""Id"" INT NOT NULL, ""Val"" INT, CONSTRAINT ""CK_ChkModified_Range"" CHECK (""Val"" > 0));
";
        cmd.ExecuteNonQuery();

        // ChkSuppressed(flag false, no check) + ChkControl(no flag, no check) + ChkModified(flag false, check redefined > 100).
        var json = """
            [
            {
                "Schema": "DropChkTests",
                "Name": "ChkSuppressed",
                "DropCheckConstraintsRemovedFromProduct": false,
                "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": false }, { "Name": "Val", "DataType": "INT", "Nullable": true } ]
            },
            {
                "Schema": "DropChkTests",
                "Name": "ChkControl",
                "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": false }, { "Name": "Val", "DataType": "INT", "Nullable": true } ]
            },
            {
                "Schema": "DropChkTests",
                "Name": "ChkModified",
                "DropCheckConstraintsRemovedFromProduct": false,
                "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": false }, { "Name": "Val", "DataType": "INT", "Nullable": true } ],
                "CheckConstraints": [ { "Name": "CK_ChkModified_Range", "Expression": "\"Val\" > 100" } ]
            }
            ]
            """;
        RunTableQuenchProc(cmd, json);

        conn.Close();
    }
}
