// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.IntegrationTests.MySQL;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.MySQL;

// B16: MySQL names CHECK constraints per schema, so a name two tables shared failed mid-deploy with a bare 3822 that
// named neither table, after the first table's check had already been created.
[Category("MySQL")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_CheckNameCollisionTests : BaseTableQuenchTests
{
    protected override Platform Platform => Platform.MySQL;
    protected override string MainConnectionString => FixtureSetup.GetMainDbConnectionString();
    protected override string MainDbName => FixtureSetup.MainDb;

    private static string Table(string name, string check) => $$"""
        {
            "Name": "`{{name}}`",
            "Columns": [ { "Name": "`Id`", "DataType": "INT", "Nullable": false }, { "Name": "`Val`", "DataType": "INT", "Nullable": true } ],
            "CheckConstraints": [ { "Name": "`{{check}}`", "Expression": "`Val` > `Id`" } ]
        }
        """;

    [Test]
    public void TwoTablesDeclaringOneCheckName_AreRefusedBeforeEitherCheckIsCreated()
    {
        if (!TargetSupportsCheckConstraints()) Assert.Ignore("CHECK constraints require MySQL 8.0.16.");
        var token = Guid.NewGuid().ToString("N")[..6];
        string a = $"ChkDupA_{token}", b = $"ChkDupB_{token}", check = $"CK_Dup_{token}";
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        try
        {
            var ex = Assert.Catch(() => RunTableQuenchProc(cmd, $"[{Table(a, check)}, {Table(b, check)}]"));
            Assert.That(ex!.Message, Does.Contain("unique per schema"), ex.Message);
            cmd.CommandText = $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = '{_mainDb}' AND CONSTRAINT_NAME = '{check}'";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.Zero, "neither table's check was created");
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS `{a}`, `{b}`";
            cmd.ExecuteNonQuery();
        }
    }

    [Test]
    public void ACheckNameAnotherTableHolds_IsRefusedByName()
    {
        if (!TargetSupportsCheckConstraints()) Assert.Ignore("CHECK constraints require MySQL 8.0.16.");
        var token = Guid.NewGuid().ToString("N")[..6];
        string other = $"ChkHeld_{token}", mine = $"ChkMine_{token}", check = $"CK_Held_{token}";
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        try
        {
            cmd.CommandText = $"CREATE TABLE `{other}` (`Id` INT NOT NULL, `Val` INT, CONSTRAINT `{check}` CHECK (`Val` > `Id`))";
            cmd.ExecuteNonQuery();

            var ex = Assert.Catch(() => RunTableQuenchProc(cmd, $"[{Table(mine, check)}]"));
            Assert.That(ex!.Message, Does.Contain("unique per schema"), ex.Message);
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS `{other}`, `{mine}`";
            cmd.ExecuteNonQuery();
        }
    }
}
