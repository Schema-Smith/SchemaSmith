// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using Schema.IntegrationTests.MySQL;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.MySQL;

[Category("MySQL")]
[TestFixture]
public class ServerVersionHelperIntegrationTests : ServerVersionHelperIntegrationTestsSharedTests
{
    protected override Platform Platform => Platform.MySQL;
    protected override string MainConnectionString => FixtureSetup.GetMainDbConnectionString();
    protected override string MainDbName => FixtureSetup.MainDb;

    // The patch-level twin of ServerVersionNum (MySQL family only): must read the same version the C# side does.
    [Test]
    public void ServerVersionPatchNum_MatchesTheServersVersionString()
    {
        using var conn = Schema.DataAccess.DbConnectionFactory.ForPlatform(Platform).GetDbConnection(MainConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT VERSION()";
        var raw = cmd.ExecuteScalar()!.ToString();

        cmd.CommandText = "SELECT SchemaSmith_ServerVersionPatchNum()";
        Assert.That(System.Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(Schema.Utility.VersionHelper.ParsePatchComparable(raw, Platform)));
    }
}
