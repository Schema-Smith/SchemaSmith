// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Linq;
using NUnit.Framework;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.MariaDb;

/// <summary>
/// A MariaDB package's Sequences/*.json goes through the whole deploy: the template loads it, the quench creates
/// the sequence before the tables, a second deploy changes nothing, and a table whose column defaults to the
/// sequence by its bare name takes its numbers from it.
/// </summary>
[Category("MariaDb")]
[Category("Integration")]
[TestFixture]
public class DeclaredSequenceTests : ObjectScriptDeployFixture
{
    protected override Platform Platform => Platform.MariaDb;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => FixtureSetup.GetMainDbConnectionString();
    protected override string ConfigPrefix => "MariaDB";
    protected override string ProductName => "DeclaredSequenceProduct";

    [SetUp]
    public void SetUp()
    {
        if (Convert.ToInt32(Scalar("SELECT SchemaSmith_ServerVersionNum()")) < 1003) Assert.Ignore("Sequences arrived in MariaDB 10.3.");
        Exec("DROP TABLE IF EXISTS `seq_orders`");
        Exec("DROP SEQUENCE IF EXISTS `order_seq`");
    }

    // Below 10.3 there are no sequences, and DROP SEQUENCE is a syntax error, so the teardown failed the fixture.
    [OneTimeTearDown]
    public void DropObjects()
    {
        Exec("DROP TABLE IF EXISTS `seq_orders`");
        if (Convert.ToInt32(Scalar("SELECT SchemaSmith_ServerVersionNum()")) >= 1003) Exec("DROP SEQUENCE IF EXISTS `order_seq`");
    }

    private void WriteSequencePackage(int start)
    {
        WritePackage("""{ "FolderPath": "Procedures", "QuenchSlot": "Objects", "ObjectType": "Procedures" }""", "Procedures", "Sequences");
        WriteFile("Sequences", "order_seq.json", $$"""{ "Name": "order_seq", "Start": {{start}}, "Increment": 1, "Cache": 10 }""");
        WriteFile("Tables", "seq_orders.json", """
            { "Name": "`seq_orders`", "Columns": [ { "Name": "`id`", "DataType": "INT", "Nullable": false, "Default": "nextval(`order_seq`)" }, { "Name": "`v`", "DataType": "INT", "Nullable": true } ],
              "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "`id`" } ] }
            """);
    }

    [Test]
    public void ADeclaredSequence_IsCreatedBeforeTheTablesThatDefaultToIt_AndASecondDeployChangesNothing()
    {
        WriteSequencePackage(start: 500);
        var first = Deploy();
        Assert.Multiple(() =>
        {
            Assert.That(Count("SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'order_seq' AND TABLE_TYPE = 'SEQUENCE'"), Is.EqualTo(1), "the sequence must exist");
            Assert.That(first.Audit.Any(r => r.ObjectType == "sequence" && r.ObjectName == "order_seq" && r.Action == "created"), Is.True, "the manifest records the creation; audit: " + first.DescribeAudit());
        });
        Exec("INSERT INTO `seq_orders` (`v`) VALUES (1)");
        Assert.That(Count("SELECT id FROM `seq_orders`"), Is.EqualTo(500), "the table's default takes its number from the sequence, by its bare name");

        var second = Deploy();
        Assert.Multiple(() =>
        {
            Assert.That(second.Audit.Any(r => r.ObjectType == "sequence" && r.Action is "created" or "modified"), Is.False, "nothing changed, so nothing is created or altered; audit: " + second.DescribeAudit());
            Assert.That(second.Audit.Any(r => r.ObjectType == "column" && r.ObjectName.Contains("seq_orders")), Is.False, "the default compares equal without the database qualifier the catalog adds; audit: " + second.DescribeAudit());
        });
        Exec("INSERT INTO `seq_orders` (`v`) VALUES (2)");
        Assert.That(Count("SELECT MAX(id) FROM `seq_orders`"), Is.EqualTo(501), "a redeploy never moves the sequence");
    }

    [Test]
    public void AChangedStart_IsAlteredInPlace_WithoutMovingTheSequence()
    {
        WriteSequencePackage(start: 10);
        Deploy();
        for (var i = 0; i < 6; i++) Exec("INSERT INTO `seq_orders` (`v`) VALUES (1)");
        Assert.That(Count("SELECT MAX(id) FROM `seq_orders`"), Is.EqualTo(15), "setup: six numbers have been handed out");

        // A LOWER start than the numbers already issued: a restart would hand 5 out again.
        WriteSequencePackage(start: 5);
        var result = Deploy();

        Assert.Multiple(() =>
        {
            Assert.That(result.Audit.Any(r => r.ObjectType == "sequence" && r.ObjectName == "order_seq" && r.Action == "modified"), Is.True, "the start value is converged; audit: " + result.DescribeAudit());
            Assert.That(Count("SELECT start_value FROM `order_seq`"), Is.EqualTo(5));
            // ALTER SEQUENCE discards the session's cached values, so the next number is the first uncached one, never one already issued.
            Assert.That(Count("SELECT NEXTVAL(`order_seq`)"), Is.GreaterThan(15), "converging the start value never restarts the sequence");
        });
    }

    // The engine stores a declared "integer" as "int", so the compare saw a change on every deploy.
    [Test]
    public void ASequenceDeclaredInteger_RedeploysWithoutAnAlter()
    {
        if (Convert.ToInt32(Scalar("SELECT SchemaSmith_ServerVersionNum()")) < 1105) Assert.Ignore("A sequence's data type is MariaDB 11.5.");
        WritePackage("""{ "FolderPath": "Procedures", "QuenchSlot": "Objects", "ObjectType": "Procedures" }""", "Procedures", "Sequences");
        WriteFile("Sequences", "order_seq.json", """{ "Name": "order_seq", "DataType": "integer", "Start": 1, "Increment": 1 }""");
        Deploy();

        var second = Deploy();

        Assert.Multiple(() =>
        {
            Assert.That(Scalar("SELECT LOWER(DATA_TYPE) FROM INFORMATION_SCHEMA.SEQUENCES WHERE SEQUENCE_SCHEMA = DATABASE() AND SEQUENCE_NAME = 'order_seq'")?.ToString(), Is.EqualTo("int"));
            Assert.That(second.Audit.Any(r => r.ObjectType == "sequence" && r.Action is "created" or "modified"), Is.False, "integer and int are one type; audit: " + second.DescribeAudit());
        });
    }
}
