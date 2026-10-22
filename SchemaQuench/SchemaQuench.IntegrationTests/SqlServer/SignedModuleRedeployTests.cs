// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Linq;
using NUnit.Framework;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>
/// ALTER strips a module's signature, and the private key behind it cannot be scripted. When the certificate's
/// private key is encrypted by the database master key, the deploy signs the changed module again; when it is
/// password-protected, the deploy warns by name that the module needs signing by hand. An unchanged module is
/// never altered, so its signature is never touched.
/// </summary>
[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
public class SignedModuleRedeployTests : ObjectScriptDeployFixture
{
    protected override Platform Platform => Platform.SqlServer;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => TestConnection("master");
    protected override string ConfigPrefix => "SqlServer";
    protected override string ProductName => "SignedModuleProduct";

    private const string SignatureCount = "SELECT COUNT(*) FROM sys.crypt_properties WHERE class = 1 AND major_id = OBJECT_ID('[dbo].[p_signed]')";

    [OneTimeSetUp]
    public void CreateKeys()
    {
        Exec("IF NOT EXISTS (SELECT 1 FROM sys.symmetric_keys WHERE name = '##MS_DatabaseMasterKey##') CREATE MASTER KEY ENCRYPTION BY PASSWORD = 'Sb-master-key-1!'");
        Exec("IF NOT EXISTS (SELECT 1 FROM sys.certificates WHERE name = 'sb_sign_cert') CREATE CERTIFICATE sb_sign_cert WITH SUBJECT = 'SchemaSmith re-sign test'");
        Exec("IF NOT EXISTS (SELECT 1 FROM sys.certificates WHERE name = 'sb_pw_cert') CREATE CERTIFICATE sb_pw_cert ENCRYPTION BY PASSWORD = 'Cert-pw-1!' WITH SUBJECT = 'password-protected private key'");
    }

    [SetUp]
    public void SetUp()
    {
        Exec("DROP PROCEDURE IF EXISTS [dbo].[p_signed]");
    }

    [OneTimeTearDown]
    public void DropKeys()
    {
        Exec("DROP PROCEDURE IF EXISTS [dbo].[p_signed]");
        Exec("IF EXISTS (SELECT 1 FROM sys.certificates WHERE name = 'sb_sign_cert') DROP CERTIFICATE sb_sign_cert");
        Exec("IF EXISTS (SELECT 1 FROM sys.certificates WHERE name = 'sb_pw_cert') DROP CERTIFICATE sb_pw_cert");
    }

    private void WriteProcedure(int n)
    {
        WritePackage("""{ "FolderPath": "Procedures", "QuenchSlot": "Objects", "ObjectType": "Procedures" }""", "Procedures");
        WriteFile("Procedures", "p_signed.sql", $"SET ANSI_NULLS ON\r\nSET QUOTED_IDENTIFIER ON\r\nGO\r\n\r\nCREATE OR ALTER PROCEDURE [dbo].[p_signed] AS SELECT {n}\r\nGO\r\n");
    }

    [Test]
    public void AChangedModule_SignedByACertificateTheMasterKeyProtects_IsSignedAgain()
    {
        WriteProcedure(1);
        Deploy();
        Exec("ADD SIGNATURE TO OBJECT::[dbo].[p_signed] BY CERTIFICATE sb_sign_cert");
        Assert.That(Count(SignatureCount), Is.EqualTo(1), "setup: the procedure is signed");

        WriteProcedure(2);
        var second = Deploy();

        Assert.Multiple(() =>
        {
            Assert.That(second.Outcomes["Procedures"], Is.EqualTo(ScriptOutcome.Applied), second.Describe());
            Assert.That(Count(SignatureCount), Is.EqualTo(1), "ALTER strips the signature; the deploy has to sign the module again");
            Assert.That(second.Audit.Any(r => r.ObjectType == "procedure" && r.ObjectName == "[dbo].[p_signed]" && r.Action == "resigned"), Is.True,
                "the manifest has to say the module was signed again; audit: " + second.DescribeAudit());
        });

        var third = Deploy();
        Assert.Multiple(() =>
        {
            Assert.That(third.Outcomes["Procedures"], Is.EqualTo(ScriptOutcome.Unchanged), third.Describe());
            Assert.That(Count(SignatureCount), Is.EqualTo(1), "an unchanged module is not altered, so its signature stays");
            Assert.That(third.Audit.Any(r => r.Action == "resigned"), Is.False, "nothing was signed again; audit: " + third.DescribeAudit());
        });
    }

    [Test]
    public void AChangedModule_SignedByAPasswordProtectedCertificate_IsAppliedAndTheRunWarnsByName()
    {
        WriteProcedure(1);
        Deploy();
        Exec("ADD SIGNATURE TO OBJECT::[dbo].[p_signed] BY CERTIFICATE sb_pw_cert WITH PASSWORD = 'Cert-pw-1!'");
        Assert.That(Count(SignatureCount), Is.EqualTo(1), "setup: the procedure is signed");

        WriteProcedure(2);
        var second = Deploy();

        Assert.Multiple(() =>
        {
            Assert.That(second.Outcomes["Procedures"], Is.EqualTo(ScriptOutcome.Applied), "the change still applies: " + second.Describe());
            Assert.That(Count(SignatureCount), Is.EqualTo(0), "nothing can sign with a password-protected key the deploy does not hold");
            Assert.That(second.Log.Any(l => l.StartsWith("WARN") && l.Contains("[dbo].[p_signed]") && l.Contains("sb_pw_cert")), Is.True,
                "the run has to warn, naming the module and the certificate; log: " + string.Join("\n", second.Log.Where(l => l.StartsWith("WARN"))));
        });
    }

    [Test]
    public void WhatIf_ReportsTheModuleItWouldSignAgain_AndTouchesNothing()
    {
        WriteProcedure(1);
        Deploy();
        Exec("ADD SIGNATURE TO OBJECT::[dbo].[p_signed] BY CERTIFICATE sb_sign_cert");
        WriteProcedure(2);

        var whatIf = Deploy(whatIf: true);

        Assert.Multiple(() =>
        {
            Assert.That(whatIf.Audit.Any(r => r.ObjectType == "procedure" && r.ObjectName == "[dbo].[p_signed]" && r.Action == "wouldResign"), Is.True,
                "the preview says the real run would sign the module again; audit: " + whatIf.DescribeAudit());
            Assert.That(Count(SignatureCount), Is.EqualTo(1), "WhatIf touches nothing");
        });
    }
}
