// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

// Cross-engine parity for #375 (a MySQL-family bug): prove PostgreSQL stays idempotent when two tables declare an
// empty OldName. PostgreSQL stores a blank OldName as '' (COALESCE(elem->>'OldName','')) and never keys a
// rename on OldName IS NOT NULL, so it is clean by construction. This locks that in.
[Category("PostgreSQL")]
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
  { "Schema": "public", "Name": "{{a}}", "OldName": "", "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": false, "PrimaryKey": true } ] },
  { "Schema": "public", "Name": "{{b}}", "OldName": "", "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": false, "PrimaryKey": true } ] }
]
""";

        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
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
            cmd.CommandText = $@"DROP TABLE IF EXISTS ""public"".""{a}""; DROP TABLE IF EXISTS ""public"".""{b}"";";
            cmd.ExecuteNonQuery();
        }
    }
}
