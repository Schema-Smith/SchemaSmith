// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.SqlServer;

// A declared clustered key the table lacks by name replaces the clustered key it has. When that one is a primary key or
// unique constraint other tables' foreign keys reference -- a DBA's own primary key, say -- SQL Server refuses the drop
// (3725) and the deploy failed with the raw error. The references are set aside and put back on the declared key.
[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_ConflictingClusteredKeyTests : BaseTableQuenchTests
{
    private static string Json(string parent, string keyColumns) => $$"""
[ { "Schema": "[dbo]", "Name": "[{{parent}}]",
    "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false }, { "Name": "[Val]", "DataType": "INT", "Nullable": false } ],
    "Indexes": [ { "Name": "[PK_{{parent}}_Declared]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "{{keyColumns}}" } ] } ]
""";

    private (System.Data.IDbConnection Conn, System.Data.IDbCommand Cmd) Open()
    {
        var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        return (conn, cmd);
    }

    private static object Scalar(System.Data.IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static void Setup(System.Data.IDbCommand cmd, string parent)
    {
        cmd.CommandText = $"""
            CREATE TABLE dbo.[{parent}] ([Id] INT NOT NULL CONSTRAINT [PK_{parent}_Dba] PRIMARY KEY CLUSTERED, [Val] INT NOT NULL);
            CREATE TABLE dbo.[{parent}_C] ([Id] INT NOT NULL, [ParentId] INT NULL CONSTRAINT [FK_{parent}_C] REFERENCES dbo.[{parent}] ([Id]) ON DELETE CASCADE);
            INSERT dbo.[{parent}] VALUES (1, 10); INSERT dbo.[{parent}_C] VALUES (100, 1);
            """;
        cmd.ExecuteNonQuery();
    }

    private static void CleanUp(System.Data.IDbCommand cmd, string parent)
    {
        cmd.CommandText = $"DROP TABLE IF EXISTS dbo.[{parent}_C]; DROP TABLE IF EXISTS dbo.[{parent}]; " +
                          $"DELETE FROM SchemaSmith.RebuildSetAsideForeignKeys WHERE [ChildTable] = N'{parent}_C'";
        cmd.ExecuteNonQuery();
    }

    [Test]
    public void AReferencedClusteredKeyTheDeclaredOneReplaces_IsReplaced_AndItsReferencesComeBack()
    {
        var parent = $"ConfKey_{Guid.NewGuid().ToString("N")[..8]}";
        var (conn, cmd) = Open();
        using (conn)
        using (cmd)
        {
            try
            {
                Setup(cmd, parent);

                Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, Json(parent, "[Id]")));

                Assert.Multiple(() =>
                {
                    Assert.That(Scalar(cmd, $"SELECT COUNT(*) FROM sys.key_constraints WHERE [name] = 'PK_{parent}_Declared' AND [type] = 'PK'"), Is.EqualTo(1), "the declared key is there");
                    Assert.That(Scalar(cmd, $"SELECT COUNT(*) FROM sys.key_constraints WHERE [name] = 'PK_{parent}_Dba'"), Is.EqualTo(0), "the one it replaced is gone");
                    Assert.That(Scalar(cmd, $"SELECT CAST(delete_referential_action AS INT) FROM sys.foreign_keys WHERE [name] = 'FK_{parent}_C' AND referenced_object_id = OBJECT_ID('dbo.{parent}')"),
                        Is.EqualTo(1), "the reference is back, ON DELETE CASCADE as it was");
                    Assert.That(Scalar(cmd, $"SELECT COUNT(*) FROM SchemaSmith.RebuildSetAsideForeignKeys WHERE [ChildTable] = N'{parent}_C'"), Is.EqualTo(0), "and nothing waits to be put back");
                });
            }
            finally
            {
                CleanUp(cmd, parent);
            }
        }
    }

    [Test]
    public void AReferenceTheDeclaredTableCouldNotTakeBack_StopsTheDeployByName_BeforeAnythingIsDropped()
    {
        var parent = $"ConfRef_{Guid.NewGuid().ToString("N")[..8]}";
        var (conn, cmd) = Open();
        using (conn)
        using (cmd)
        {
            try
            {
                Setup(cmd, parent);

                var ex = Assert.Catch(() => RunTableQuenchProc(cmd, Json(parent, "[Id],[Val]")));

                Assert.Multiple(() =>
                {
                    Assert.That(ex!.Message, Does.Contain($"FK_{parent}_C"), "the refusal names the reference it could not put back");
                    Assert.That(Scalar(cmd, $"SELECT COUNT(*) FROM sys.key_constraints WHERE [name] = 'PK_{parent}_Dba'"), Is.EqualTo(1), "the key is where it was");
                    Assert.That(Scalar(cmd, $"SELECT COUNT(*) FROM sys.foreign_keys WHERE [name] = 'FK_{parent}_C'"), Is.EqualTo(1), "and so is the reference");
                });
            }
            finally
            {
                CleanUp(cmd, parent);
            }
        }
    }
}
