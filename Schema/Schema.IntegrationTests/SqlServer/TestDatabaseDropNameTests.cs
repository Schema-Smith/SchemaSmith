// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;

namespace Schema.IntegrationTests.SqlServer;

[TestFixture]
public class TestDatabaseDropNameTests
{
    [TestCase("DeployCdc_ebae287d0b86")]
    [TestCase("Checkpoint_0f4c2a9e1b7d4c3a8e6f5d2c1b0a9e8f")]
    [TestCase("SchemaSmithFreshBench_9a8b7c6d5e4f3a2b1")]
    public void AGuidSuffixedName_IsAGeneratedTestDatabase(string name)
    {
        Assert.That(TestDatabaseDrop.IsGeneratedTestDatabaseName(name), Is.True);
    }

    [TestCase("TestMain")]
    [TestCase("AdventureWorks")]
    [TestCase("Archive_20240101")]
    [TestCase("probe_r585")]
    [TestCase("Sales_ABCDEF12")]
    [TestCase("Team_Data_deadbeef")]
    public void AnyOtherName_IsNot(string name)
    {
        Assert.That(TestDatabaseDrop.IsGeneratedTestDatabaseName(name), Is.False);
    }
}
