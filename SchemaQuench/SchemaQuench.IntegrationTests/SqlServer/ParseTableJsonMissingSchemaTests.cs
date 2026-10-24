// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Schema.DataAccess;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.SqlServer;

// I5: SQL Server engine procs (ParseTableJsonIntoTempTables, IndexOnlyQuench) used to silently
// default a missing/blank [Schema] to 'dbo'. Slice-1's SchemaDefaultResolver populates the
// field on the canonical Template.Load path; a blank value at this layer is a programmer error
// (a caller bypassed Load or a substitution swallowed the {{SchemaName}} token). Silent dbo
// fallback is data-loss-equivalent for schema templates. These tests assert the procs now
// throw a clear, actionable error rather than writing to dbo.
[Category("SqlServer")]
public class ParseTableJsonMissingSchemaTests : BaseTableQuenchTests
{
    [Test]
    public void TableQuench_MissingSchema_ThrowsClearError()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // JSON with no Schema field on the table — pre-fix this would silently write to dbo.
        var json = """
            [
            {
                "Name": "[NoSchemaTable]",
                "Columns": [
                    { "Name": "[Id]", "DataType": "INT" }
                ]
            }
            ]
            """;
        cmd.CommandTimeout = 60;
        cmd.CommandText =
            $"EXEC SchemaSmith.TableQuench @ProductName = '{_productName}', @TableDefinitions = '{json.Replace("'", "''")}', @DropTablesRemovedFromProduct = 0, @DropUnknownIndexes = 0";

        var ex = Assert.Throws<Microsoft.Data.SqlClient.SqlException>(() => TransientRetry.Run(() => cmd.ExecuteNonQuery()));
        Assert.That(ex!.Message, Does.Contain("missing Schema"));
        Assert.That(ex.Message, Does.Contain("NoSchemaTable"));
        // The error should NOT have created a dbo table as a side-effect.
        cmd.CommandText = "SELECT CAST(CASE WHEN OBJECT_ID('dbo.NoSchemaTable', 'U') IS NOT NULL THEN 1 ELSE 0 END AS BIT)";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.False,
            "Missing-Schema table must NOT have been silently created in dbo.");
        conn.Close();
    }

    [Test]
    public void TableQuench_BlankSchema_ThrowsClearError()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // Explicit empty Schema — same programmer-error semantics as missing.
        var json = """
            [
            {
                "Schema": "",
                "Name": "[BlankSchemaTable]",
                "Columns": [
                    { "Name": "[Id]", "DataType": "INT" }
                ]
            }
            ]
            """;
        cmd.CommandTimeout = 60;
        cmd.CommandText =
            $"EXEC SchemaSmith.TableQuench @ProductName = '{_productName}', @TableDefinitions = '{json.Replace("'", "''")}', @DropTablesRemovedFromProduct = 0, @DropUnknownIndexes = 0";

        var ex = Assert.Throws<Microsoft.Data.SqlClient.SqlException>(() => TransientRetry.Run(() => cmd.ExecuteNonQuery()));
        Assert.That(ex!.Message, Does.Contain("missing Schema"));
        conn.Close();
    }

    [Test]
    public void IndexOnlyQuench_MissingSchema_ThrowsClearError()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        var json = """
            [
            {
                "Name": "[NoSchemaTableIO]",
                "Indexes": [
                    { "Name": "[IDX_X]", "IndexColumns": "[Id]" }
                ]
            }
            ]
            """;
        cmd.CommandTimeout = 60;
        cmd.CommandText =
            $"EXEC SchemaSmith.IndexOnlyQuench @ProductName = '{_productName}', @TableDefinitions = '{json.Replace("'", "''")}', @DropUnknownIndexes = 0";

        var ex = Assert.Throws<Microsoft.Data.SqlClient.SqlException>(() => TransientRetry.Run(() => cmd.ExecuteNonQuery()));
        Assert.That(ex!.Message, Does.Contain("missing Schema"));
        Assert.That(ex.Message, Does.Contain("NoSchemaTableIO"));
        conn.Close();
    }

    [Test]
    public void TableQuench_MissingRelatedTableSchema_ThrowsClearError()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // Table has Schema populated, but the FK is missing RelatedTableSchema — same programmer
        // error after slice-1's resolver populates it from the platform default.
        var json = """
            [
            {
                "Schema": "[dbo]",
                "Name": "[FkMissingRelatedSchema]",
                "Columns": [
                    { "Name": "[Id]", "DataType": "INT" }
                ],
                "ForeignKeys": [
                    {
                        "Name": "[FK_MissingRelated]",
                        "Columns": "[Id]",
                        "RelatedTable": "[SomeOtherTable]",
                        "RelatedColumns": "[Id]"
                    }
                ]
            }
            ]
            """;
        cmd.CommandTimeout = 60;
        cmd.CommandText =
            $"EXEC SchemaSmith.TableQuench @ProductName = '{_productName}', @TableDefinitions = '{json.Replace("'", "''")}', @DropTablesRemovedFromProduct = 0, @DropUnknownIndexes = 0";

        var ex = Assert.Throws<Microsoft.Data.SqlClient.SqlException>(() => TransientRetry.Run(() => cmd.ExecuteNonQuery()));
        Assert.That(ex!.Message, Does.Contain("missing RelatedTableSchema"));
        Assert.That(ex.Message, Does.Contain("FK_MissingRelated"));
        conn.Close();
    }
}
