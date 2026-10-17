// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>
/// A partially contained database keeps its catalog in a fixed collation (CATALOG_DEFAULT) while its own default, which
/// its table variables and temp tables take, can be anything. A comparison of a catalog name with one of those was a
/// compile-time collation conflict, so the forge could not even be kindled there.
/// </summary>
[Category("SqlServer")]
[NonParallelizable]
public class ContainedDatabaseTests : BaseTableQuenchTests
{
    [TestCase(IngestEncoding.Json)]
    [TestCase(IngestEncoding.Xml)]
    public void TheForgeKindles_InAPartiallyContainedDatabaseWithItsOwnCollation(IngestEncoding encoding)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = "SELECT CAST(value_in_use AS INT) FROM sys.configurations WHERE [name] = 'contained database authentication'";
        var containmentWas = Convert.ToInt32(cmd.ExecuteScalar());
        var db = $"Contained_{Guid.NewGuid():N}"[..28];
        try
        {
            if (containmentWas == 0) Run(cmd, "EXEC sp_configure 'contained database authentication', 1; RECONFIGURE;");
            Run(cmd, $"CREATE DATABASE [{db}] CONTAINMENT = PARTIAL COLLATE Japanese_CS_AS_KS");
            conn.ChangeDatabase(db);
            Assert.DoesNotThrow(() => ForgeKindler.KindleTheForge(cmd, Platform.SqlServer, forceReKindle: true, encoding));
            cmd.CommandText = "SELECT COUNT(*) FROM sys.procedures WHERE SCHEMA_NAME([schema_id]) = 'SchemaSmith' AND [name] IN ('BootstrapTableQuench', 'IndexedViewQuench', 'TableQuench')";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(3), "the procedures that failed to compile there exist");
        }
        finally
        {
            conn.ChangeDatabase("master");
            Schema.IntegrationTests.SqlServer.TestDatabaseDrop.Drop(cmd, db);
            if (containmentWas == 0) Run(cmd, "EXEC sp_configure 'contained database authentication', 0; RECONFIGURE;");
        }
    }

    private static void Run(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();
    }
}
