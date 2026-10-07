// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.SqlServer;

/// <summary>
/// The temp-table sweep that runs before every query token resolves. It must drop the session's temp tables, and it
/// must run on every supported version: it used STRING_AGG (2017) and DROP TABLE IF EXISTS (2016), so a query token
/// failed on SQL Server 2008 R2 through 2016 (SS-123).
/// </summary>
[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
public class DropTempTablesTests
{
    [Test]
    public void TheSweep_DropsEverySessionTempTable()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE #sweep_a (Id INT); CREATE TABLE #sweep_b (Id INT);";
        cmd.ExecuteNonQuery();

        cmd.CommandText = TokenHelper.GetDropTempTablesScript(Platform.SqlServer);
        cmd.ExecuteNonQuery();

        cmd.CommandText = "SELECT COUNT(*) FROM (SELECT OBJECT_ID('tempdb..#sweep_a') AS Id UNION ALL SELECT OBJECT_ID('tempdb..#sweep_b')) t WHERE Id IS NOT NULL";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.Zero);
    }

    [Test]
    public void TheSweep_RunsWhenThereIsNothingToDrop()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = TokenHelper.GetDropTempTablesScript(Platform.SqlServer);
        Assert.DoesNotThrow(() => cmd.ExecuteNonQuery());
    }
}
