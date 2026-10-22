// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.SqlServer;

// Cross-engine parity for #375 (a MySQL-family bug): prove SQL Server stays idempotent when two tables declare an
// empty OldName. SQL Server manufactures a [] bracket pair for a blank OldName too, but keys its rename/new-
// table logic on OBJECT_ID(schema.OldName) existence (OBJECT_ID('dbo.[]') is NULL) and has no
// _SchemaSmith_TableRenames PK to collide on — so it is clean by construction. This locks that in.
[Category("SqlServer")]
[TestFixture]
public class TableRenameEmptyOldNameParityTests : BaseTableQuenchTests
{
    [Test]
    public void EmptyOldNameOnMultipleTables_IsIdempotentAcrossDeploys()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var product = $"EmptyOld_{id}";
        var a = $"EmptyOldA_{id}";
        var b = $"EmptyOldB_{id}";
        var json = $$"""
[
  { "Schema": "[dbo]", "Name": "[{{a}}]", "OldName": "", "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false, "PrimaryKey": true } ] },
  { "Schema": "[dbo]", "Name": "[{{b}}]", "OldName": "", "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false, "PrimaryKey": true } ] }
]
""";

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        try
        {
            RunTableQuenchProc(cmd, json, productName: product);
            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, json, productName: product),
                "two tables declaring an empty OldName must stay idempotent on re-deploy (#375 parity)");
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS dbo.[{a}]; DROP TABLE IF EXISTS dbo.[{b}];";
            cmd.ExecuteNonQuery();
        }
    }
}
