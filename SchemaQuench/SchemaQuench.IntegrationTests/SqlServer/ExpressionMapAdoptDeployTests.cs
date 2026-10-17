// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Linq;
using NUnit.Framework;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>
/// #508 through SchemaQuench's own deploy path, not the TableQuench wrapper the procedure tests use: the deploy runs its
/// steps itself, and the scratch-copy check was first wired only into the wrapper, so a real deploy skipped it.
/// </summary>
[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
public class ExpressionMapAdoptDeployTests : ObjectScriptDeployFixture
{
    protected override Platform Platform => Platform.SqlServer;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => TestConnection("master");
    protected override string ConfigPrefix => "SqlServer";
    protected override string ProductName => "AdoptDeployProduct";

    private const string Table = "adopt_deploy";

    private void BuildByHand()
    {
        Exec($"DROP TABLE IF EXISTS dbo.[{Table}]; DELETE FROM SchemaSmith.ExpressionMap WHERE [ObjectTable] = '{Table}'");
        Exec($"""
            CREATE TABLE dbo.[{Table}] ([Id] INT NOT NULL CONSTRAINT [PK_{Table}] PRIMARY KEY, [ParkedReason] VARCHAR(40) NULL,
              [Created] DATETIME NULL CONSTRAINT [DF_{Table}_Created] DEFAULT (GETDATE()),
              CONSTRAINT [CK_{Table}_Parked] CHECK ([ParkedReason] IN ('AwaitingCompanyMapping', 'AwaitingRoleMapping')))
            """);
        WritePackage("", []);
        WriteFile("Tables", $"dbo.{Table}.json", $$"""
            { "Schema": "[dbo]", "Name": "[{{Table}}]",
              "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false },
                           { "Name": "[ParkedReason]", "DataType": "VARCHAR(40)", "Nullable": true },
                           { "Name": "[Created]", "DataType": "DATETIME", "Nullable": true, "Default": "GETDATE()" } ],
              "Indexes": [ { "Name": "[PK_{{Table}}]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" } ],
              "CheckConstraints": [ { "Name": "[CK_{{Table}}_Parked]", "Expression": "[ParkedReason] IN ('AwaitingCompanyMapping', 'AwaitingRoleMapping')" } ] }
            """);
    }

    [TearDown]
    public void DropTable() => Exec($"DROP TABLE IF EXISTS dbo.[{Table}]; DELETE FROM SchemaSmith.ExpressionMap WHERE [ObjectTable] = '{Table}'");

    [Test]
    public void ADeploy_LeavesExpressionsTheDatabaseAlreadyHoldsAlone()
    {
        BuildByHand();
        var result = Deploy();
        Assert.Multiple(() =>
        {
            Assert.That(result.Audit.Where(r => r.ObjectName.Contains(Table)).Select(r => $"{r.ObjectType} {r.Action}"), Is.Empty, result.DescribeAudit());
            Assert.That(result.Log, Has.Some.Contains("Verified 2 expression(s) on a scratch copy"), string.Join("\n", result.Log));
        });
    }

    [Test]
    public void AWhatIfDeploy_LeavesTheMapAsItWas()
    {
        BuildByHand();
        Deploy(whatIf: true);
        Assert.That(Count($"SELECT COUNT(*) FROM SchemaSmith.ExpressionMap WHERE [ObjectTable] = '{Table}'"), Is.Zero);
    }
}
