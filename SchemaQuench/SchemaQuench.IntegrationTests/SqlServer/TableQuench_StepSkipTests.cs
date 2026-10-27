// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.Data;
using Microsoft.Data.SqlClient;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.SqlServer;

// A step the package gives nothing to is skipped and the log says so; one it does give something to runs. Each package is
// deployed twice: the first creates the table, which skips the whole modify pass, and the second runs it.
[TestFixture]
public class TableQuench_StepSkipTests : BaseTableQuenchTests
{
    private const string Plain = """
[ { "Schema": "[dbo]", "Name": "[StepSkipProbe]",
    "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false } ],
    "Indexes": [ { "Name": "[PK_StepSkipProbe]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" } ] } ]
""";

    private const string Described = """
[ { "Schema": "[dbo]", "Name": "[StepSkipProbe]", "Description": "probe", "IsTemporal": true,
    "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false } ],
    "Indexes": [ { "Name": "[PK_StepSkipProbe]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" } ] } ]
""";

    [Test]
    public void ADeployThatDeclaresNoneOfTheirInputs_SkipsTheSteps_AndSaysSo()
    {
        var messages = Deploy(Plain);

        Assert.Multiple(() =>
        {
            Assert.That(messages, Does.Contain("Skipping extended properties: none declared"));
            Assert.That(messages, Does.Contain("Skipping period columns: no table is declared temporal"));
            Assert.That(messages, Does.Contain("Skipping constraint enforcement and validation: no foreign key or check constraint declares it"));
            Assert.That(messages, Does.Contain("Skipping index disabled state: no index is declared disabled and none is disabled"));
            Assert.That(messages, Does.Contain("Skipping table modifications: every declared table is new to this database"));
            Assert.That(messages, Does.Contain("Skipping online column forms: OnlineApply is Off for every table"));
            Assert.That(messages, Does.Contain("Skipping history tables: no temporal table has versioning suspended"));
            Assert.That(messages, Does.Not.Contain("Converge extended properties"));
            Assert.That(messages, Does.Not.Contain("Converge declared period columns"));
        });
    }

    [Test]
    public void ADeployThatDeclaresTheirInputs_RunsTheSteps()
    {
        var messages = Deploy(Described);

        Assert.Multiple(() =>
        {
            Assert.That(messages, Does.Contain("Converge extended properties"));
            Assert.That(messages, Does.Contain("Converge declared period columns"));
            Assert.That(messages, Does.Not.Contain("Skipping extended properties: none declared"));
            Assert.That(messages, Does.Not.Contain("Skipping period columns: no table is declared temporal"));
        });
    }

    private List<string> Deploy(string json)
    {
        var messages = new List<string>();
        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.InfoMessage += (_, e) => { foreach (SqlError err in e.Errors) messages.Add(err.Message.Trim()); };
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        try
        {
            DropProbe(cmd);
            RunTableQuenchProc(cmd, json);
            RunTableQuenchProc(cmd, json);
        }
        finally
        {
            DropProbe(cmd);
        }
        return messages;
    }

    private static void DropProbe(IDbCommand cmd)
    {
        cmd.CommandText = """
IF OBJECTPROPERTY(OBJECT_ID('dbo.StepSkipProbe'), 'TableTemporalType') = 2 ALTER TABLE dbo.StepSkipProbe SET (SYSTEM_VERSIONING = OFF);
DROP TABLE IF EXISTS dbo.StepSkipProbe;
DROP TABLE IF EXISTS dbo.StepSkipProbe_Hist;
""";
        cmd.ExecuteNonQuery();
    }
}
