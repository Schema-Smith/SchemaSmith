// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>
/// Repro for #304 (surfaced by an external POC): a declared PRIMARY KEY is not created when the
/// target already has a differently-named unique CLUSTERED index on the same column(s). PK
/// reconciliation matches existing objects by name only, and a clustered PK additionally conflicts
/// with the existing clustered index. Exact-repro from the reporter's case (unique clustered index
/// occupying the clustered slot; declared clustered PK on the same column).
/// </summary>
[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_PrimaryKeyVsUniqueClusteredTests : BaseTableQuenchTests
{
    [Test]
    public void PrimaryKey_IsCreated_WhenSameColumnUniqueClusteredIndexAlreadyExists()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        // Arrange: a table whose clustered slot is occupied by a unique CLUSTERED index (not a PK),
        // under a different name than the declared PK.
        cmd.CommandText = @"
DROP TABLE IF EXISTS dbo.PkOnUniqueClustered;
CREATE TABLE dbo.PkOnUniqueClustered (
    PortalID INT NOT NULL,
    Name NVARCHAR(100) NULL
);
CREATE UNIQUE CLUSTERED INDEX CUX_PkOnUniqueClustered_PortalID ON dbo.PkOnUniqueClustered (PortalID);
";
        cmd.ExecuteNonQuery();

        // Act: quench a definition that declares a clustered PRIMARY KEY on the same column.
        var json = """
        [
            {
                "Schema": "[dbo]",
                "Name": "[PkOnUniqueClustered]",
                "Columns": [
                    { "Name": "[PortalID]", "DataType": "INT", "Nullable": false },
                    { "Name": "[Name]", "DataType": "NVARCHAR(100)", "Nullable": true }
                ],
                "Indexes": [
                    {
                        "Name": "[PK_PkOnUniqueClustered]",
                        "IndexColumns": "[PortalID]",
                        "PrimaryKey": true,
                        "Unique": true,
                        "Clustered": true
                    }
                ]
            }
        ]
        """;
        RunTableQuenchProc(cmd, json);

        // Assert: the declared primary key now exists on PortalID.
        cmd.CommandText = @"
            SELECT COUNT(*)
              FROM sys.indexes i WITH (NOLOCK)
             WHERE i.[object_id] = OBJECT_ID('dbo.PkOnUniqueClustered') AND i.is_primary_key = 1";
        Assert.That((int)cmd.ExecuteScalar()!, Is.EqualTo(1),
            "Declared PRIMARY KEY must be created even when a same-column unique clustered index already exists (#304).");

        conn.Close();
    }
}
