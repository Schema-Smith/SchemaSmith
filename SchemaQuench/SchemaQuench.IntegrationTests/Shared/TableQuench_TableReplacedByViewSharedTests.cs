// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.Shared;

// #613: an owned table replaced by a same-named view (the usual table split: move the rows, drop the table, put a
// view under its name) kept its ownership row, so drop-by-absence tried to drop the view as a table and every
// deploy failed from then on. Only a table that is still a base table is a drop candidate.
[Category("Integration")]
public abstract class TableQuench_TableReplacedByViewSharedTests : BaseTableQuenchTests
{
    [Test]
    public void AnOwnedTableReplacedByAView_IsNotDroppedAsATable_AndItsOwnershipGoes()
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"ViewSplit_{uid}";
        var table = $"ViewSplitCustomer_{uid}";
        var keep = $"ViewSplitKeep_{uid}";

        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        try
        {
            RunTableQuenchProc(cmd, Tables(table, keep), productName: product);
            Exec(cmd, $"DROP TABLE `{_mainDb}`.`{table}`");
            Exec(cmd, $"CREATE VIEW `{_mainDb}`.`{table}` AS SELECT `Id` FROM `{_mainDb}`.`{keep}`");

            Exec(cmd, "DELETE FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID()");
            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, Tables(keep), dropTablesRemovedFromProduct: true, whatIf: true, productName: product), "WhatIf");
            Assert.That(Scalar(cmd, $"SELECT COUNT(*) FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID() AND ObjectType = 'table' AND ObjectName = '{table}'"),
                Is.Zero, "a preview does not predict dropping the view");

            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, Tables(keep), dropTablesRemovedFromProduct: true, productName: product), "first deploy");
            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, Tables(keep), dropTablesRemovedFromProduct: true, productName: product), "and every one after");
            Assert.Multiple(() =>
            {
                Assert.That(Scalar(cmd, $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.VIEWS WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = '{table}'"), Is.EqualTo(1), "the view is left alone");
                Assert.That(Scalar(cmd, $"SELECT COUNT(*) FROM SchemaSmith_ProductOwnership WHERE ObjectType = 'TABLE' AND ObjectName = '{table}'"), Is.Zero, "the stale ownership row is pruned");
            });
        }
        finally
        {
            Exec(cmd, $"DROP VIEW IF EXISTS `{_mainDb}`.`{table}`; DROP TABLE IF EXISTS `{_mainDb}`.`{table}`; DROP TABLE IF EXISTS `{_mainDb}`.`{keep}`;");
            TransientRetry.Run(() => Exec(cmd, $"DELETE FROM SchemaSmith_ProductOwnership WHERE ObjectType = 'TABLE' AND ProductName = '{product}'"));
        }
        conn.Close();
    }

    private static string Tables(params string[] names) =>
        "[" + string.Join(",", Array.ConvertAll(names, n => $$"""
          { "Name": "{{n}}",
            "Columns": [ { "Name": "`Id`", "DataType": "int", "Nullable": false } ],
            "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "`Id`" } ] }
          """)) + "]";

    private static void Exec(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static int Scalar(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
