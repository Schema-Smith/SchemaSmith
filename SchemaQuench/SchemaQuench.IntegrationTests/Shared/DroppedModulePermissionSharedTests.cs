// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Linq;
using NUnit.Framework;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// A script that drops its module and creates it again loses the module's grants on every engine, and the deploy
/// puts them back: grant EXECUTE on a procedure deployed in that form, change the script, deploy again, and the grant
/// is still there with a manifest row saying it was put back. The subclass supplies the engine's DDL and its grant probe.
/// </summary>
[NonParallelizable]
public abstract class DroppedModulePermissionSharedTests : ObjectScriptDeployFixture
{
    protected sealed override string ProductName => "DroppedModuleProduct";

    /// <summary>The procedure script in its DROP + CREATE form, with the given body.</summary>
    protected abstract string ProcedureScript(string body);
    /// <summary>Creates the grantee when it is not there yet.</summary>
    protected abstract string CreateGrantee { get; }
    protected abstract string GrantExecute { get; }
    /// <summary>Counts 1 while the grantee holds EXECUTE on the procedure.</summary>
    protected abstract string ExecuteGrantCount { get; }
    protected abstract string[] DropStatements { get; }
    /// <summary>The procedure as the change audit names it.</summary>
    protected abstract string AuditedName { get; }

    [SetUp]
    public void SetUp()
    {
        foreach (var drop in DropStatements) Exec(drop);
        Exec(CreateGrantee);
        WriteProcedure("SELECT 1");
    }

    [OneTimeTearDown]
    public void DropObjects()
    {
        foreach (var drop in DropStatements) Exec(drop);
    }

    private void WriteProcedure(string body)
    {
        WritePackage("""{ "FolderPath": "Procedures", "QuenchSlot": "Objects", "ObjectType": "Procedures" }""", "Procedures");
        WriteFile("Procedures", "p_perm.sql", ProcedureScript(body));
    }

    [Test]
    public void RedeployOfADroppedModule_KeepsItsGrant_AndAuditsTheReapply()
    {
        var first = Deploy();
        Assert.That(first.Outcomes["Procedures"], Is.EqualTo(ScriptOutcome.Applied), first.Describe());
        Exec(GrantExecute);
        Assert.That(Count(ExecuteGrantCount), Is.EqualTo(1), "setup: the grant must be in place before the redeploy, or the assertion below is vacuous");

        WriteProcedure("SELECT 2");
        var second = Deploy();

        Assert.Multiple(() =>
        {
            Assert.That(second.Outcomes["Procedures"], Is.EqualTo(ScriptOutcome.Applied), "the changed script must run: " + second.Describe());
            Assert.That(Count(ExecuteGrantCount), Is.EqualTo(1), "the script dropped the procedure, which took the EXECUTE grant with it; the deploy has to put it back");
            Assert.That(second.Audit.Any(r => r.ObjectType == "procedure" && r.ObjectName == AuditedName && r.Action == "reapplied"), Is.True,
                "the run manifest has to say the grant was put back; audit: " + second.DescribeAudit());
        });
    }

    [Test]
    public void WhatIf_ReportsTheGrantItWouldReapply_AndRunsNothing()
    {
        Deploy();
        Exec(GrantExecute);
        WriteProcedure("SELECT 2");

        var whatIf = Deploy(whatIf: true);

        Assert.Multiple(() =>
        {
            Assert.That(whatIf.Audit.Any(r => r.ObjectType == "procedure" && r.ObjectName == AuditedName && r.Action == "wouldReapply"), Is.True,
                "the preview lists the grant the real run would put back; audit: " + whatIf.DescribeAudit());
            Assert.That(Count(ExecuteGrantCount), Is.EqualTo(1), "WhatIf runs no script, so the grant is untouched");
        });
    }
}
