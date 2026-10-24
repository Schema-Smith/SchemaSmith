// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_DropForeignKeysTests : BaseTableQuenchTests
{
    // Non-vacuous: both FKs are removed from the JSON in the same quench.
    // FKSuppressed sets DropForeignKeysRemovedFromProduct:false -> its FK survives.
    // FKControl omits the flag (inherits cascade default true) -> its FK drops.
    [Test]
    public void TableQuench_ShouldSuppressForeignKeyDropWhenTableFlagIsFalse()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM pg_constraint WHERE conname = 'FK_FKSuppressed_Ref' AND contype = 'f')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True, "FK_FKSuppressed_Ref should still exist (suppressed by table flag).");

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM pg_constraint WHERE conname = 'FK_FKControl_Ref' AND contype = 'f')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.False, "FK_FKControl_Ref should be gone (no suppression flag).");

        conn.Close();
    }

    // The split: a MODIFIED FK (same name, changed definition) must still be dropped and
    // recreated even when DropForeignKeysRemovedFromProduct:false — the flag only governs
    // by-absence removal, never modified-FK reconciliation. FKModified's FK was created with
    // no delete action ('a') and the JSON redefines it ON DELETE CASCADE; it must converge to 'c'.
    [Test]
    public void TableQuench_ModifiedForeignKeyStillReconcilesWhenTableFlagIsFalse()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT confdeltype FROM pg_constraint WHERE conname = 'FK_FKModified_Ref' AND contype = 'f'";
        Assert.That(cmd.ExecuteScalar() as char? ?? ' ', Is.EqualTo('c'),
            "FK_FKModified_Ref must be reconciled to ON DELETE CASCADE despite DropForeignKeysRemovedFromProduct:false.");

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
CREATE SCHEMA ""DropFKTests"";
CREATE TABLE ""DropFKTests"".""FKRef"" (""Id"" INT NOT NULL CONSTRAINT ""PK_FKRef"" PRIMARY KEY);
CREATE TABLE ""DropFKTests"".""FKSuppressed"" (""Id"" INT NOT NULL CONSTRAINT ""PK_FKSuppressed"" PRIMARY KEY, ""RefId"" INT);
CREATE TABLE ""DropFKTests"".""FKControl"" (""Id"" INT NOT NULL CONSTRAINT ""PK_FKControl"" PRIMARY KEY, ""RefId"" INT);
CREATE TABLE ""DropFKTests"".""FKModified"" (""Id"" INT NOT NULL CONSTRAINT ""PK_FKModified"" PRIMARY KEY, ""RefId"" INT);
ALTER TABLE ""DropFKTests"".""FKSuppressed"" ADD CONSTRAINT ""FK_FKSuppressed_Ref"" FOREIGN KEY (""RefId"") REFERENCES ""DropFKTests"".""FKRef"" (""Id"");
ALTER TABLE ""DropFKTests"".""FKControl"" ADD CONSTRAINT ""FK_FKControl_Ref"" FOREIGN KEY (""RefId"") REFERENCES ""DropFKTests"".""FKRef"" (""Id"");
ALTER TABLE ""DropFKTests"".""FKModified"" ADD CONSTRAINT ""FK_FKModified_Ref"" FOREIGN KEY (""RefId"") REFERENCES ""DropFKTests"".""FKRef"" (""Id"");
";
        cmd.ExecuteNonQuery();

        // FKRef + FKSuppressed(flag false, no FK) + FKControl(no flag, no FK) + FKModified(flag false, FK redefined CASCADE).
        var json = """
            [
            {
                "Schema": "DropFKTests",
                "Name": "FKRef",
                "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": false } ],
                "Indexes": [ { "Name": "PK_FKRef", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" } ]
            },
            {
                "Schema": "DropFKTests",
                "Name": "FKSuppressed",
                "DropForeignKeysRemovedFromProduct": false,
                "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": false }, { "Name": "RefId", "DataType": "INT", "Nullable": true } ],
                "Indexes": [ { "Name": "PK_FKSuppressed", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" } ]
            },
            {
                "Schema": "DropFKTests",
                "Name": "FKControl",
                "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": false }, { "Name": "RefId", "DataType": "INT", "Nullable": true } ],
                "Indexes": [ { "Name": "PK_FKControl", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" } ]
            },
            {
                "Schema": "DropFKTests",
                "Name": "FKModified",
                "DropForeignKeysRemovedFromProduct": false,
                "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": false }, { "Name": "RefId", "DataType": "INT", "Nullable": true } ],
                "Indexes": [ { "Name": "PK_FKModified", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" } ],
                "ForeignKeys": [
                    { "Name": "FK_FKModified_Ref", "Columns": "RefId", "RelatedTableSchema": "DropFKTests", "RelatedTable": "FKRef", "RelatedColumns": "Id", "DeleteAction": "CASCADE", "UpdateAction": "NO ACTION" }
                ]
            }
            ]
            """;
        RunTableQuenchProc(cmd, json);

        conn.Close();
    }
}
