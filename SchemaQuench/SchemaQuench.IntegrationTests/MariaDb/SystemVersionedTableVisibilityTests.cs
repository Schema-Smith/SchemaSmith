// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.IntegrationTests.MariaDb;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.MariaDb;

/// <summary>
/// MariaDB reports a system-versioned table as <c>SYSTEM VERSIONED</c> rather than <c>BASE TABLE</c>.
/// The existing-table snapshot the deploy builds is what decides whether a table is new, so filtering it
/// on <c>BASE TABLE</c> alone hid such a table and the deploy issued a CREATE for something that was
/// already there — a hard failure on a table SchemaSmith had itself just been asked to manage.
/// <para>
/// MariaDB-only by nature: MySQL has no system versioning, so this shape cannot occur there.
/// The package declares <c>IsSystemVersioned: true</c> so it matches the live table's versioning: this
/// test is only about the table being <em>visible</em> (recognised as existing, not re-created). Declaring
/// it false or omitting it against an already-versioned table is a data-loss refuse, covered separately by
/// <see cref="SystemVersioningDeployTests.AnExistingSystemVersionedTableRefusesToDropVersioningWhenNoLongerDeclared"/>.
/// </para>
/// </summary>
[Category("MariaDb")]
[TestFixture]
public class SystemVersionedTableVisibilityTests : BaseTableQuenchTests
{
    protected override Platform Platform => Platform.MariaDb;
    protected override string MainDbName => FixtureSetup.MainDb;
    protected override string MainConnectionString => FixtureSetup.GetMainDbConnectionString();

    private const string TableName = "SysVersionedVisible";

    private static string TableJson() => $$"""
        [{
            "Name": "{{TableName}}",
            "IsSystemVersioned": true,
            "Columns": [
                { "Name": "Id", "DataType": "INT", "Nullable": false },
                { "Name": "Val", "DataType": "INT", "Nullable": true }
            ],
            "Indexes": [ { "Name": "PRIMARY", "IndexColumns": "Id", "PrimaryKey": true, "Unique": true } ]
        }]
        """;

    [Test]
    public void ASystemVersionedTableIsNotTreatedAsNew()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.MariaDb).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        // MariaDB gained system versioning in 10.3; on the 10.2 floor the CREATE below is a hard
        // syntax error, so the state under test cannot exist here at all.
        cmd.CommandText = "SELECT VERSION()";
        var serverVersion = cmd.ExecuteScalar()?.ToString() ?? "";
        if (!FixtureSetup.SupportsSystemVersioning(serverVersion))
            Assert.Ignore($"MariaDB {serverVersion} predates system-versioned tables (10.3), so this state cannot be created on the supported floor.");


        cmd.CommandText = $"DROP TABLE IF EXISTS `{TableName}`";
        cmd.ExecuteNonQuery();
        cmd.CommandText = $"CREATE TABLE `{TableName}` (Id INT NOT NULL, Val INT NULL, PRIMARY KEY (Id)) WITH SYSTEM VERSIONING";
        cmd.ExecuteNonQuery();

        // Guards the premise rather than assuming it: if a future MariaDB stopped reporting this
        // TABLE_TYPE the test below would pass for the wrong reason.
        cmd.CommandText = $"SELECT TABLE_TYPE FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = '{TableName}'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("SYSTEM VERSIONED"));

        // Before the fix this threw: the table looked new, so the deploy emitted CREATE TABLE for it.
        Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, TableJson()),
            "a system-versioned table must be recognised as existing rather than created again");

        cmd.CommandText = $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = '{TableName}' AND TABLE_TYPE = 'SYSTEM VERSIONED'";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1),
            "and the deploy must leave its versioning alone");

        cmd.CommandText = $"DROP TABLE IF EXISTS `{TableName}`";
        cmd.ExecuteNonQuery();
        conn.Close();
    }
}
