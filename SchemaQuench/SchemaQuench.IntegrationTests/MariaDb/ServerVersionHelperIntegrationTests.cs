// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using Schema.IntegrationTests.MariaDb;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.MariaDb;

[Category("MariaDb")]
[TestFixture]
public class ServerVersionHelperIntegrationTests : ServerVersionHelperIntegrationTestsSharedTests
{
    protected override Platform Platform => Platform.MariaDb;
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

    // RENAME COLUMN and RENAME INDEX arrived in MariaDB 10.5.2. Before the patch-level comparable the gates had to
    // say 10.6, so 10.5.2-10.5.x took the fallback path.
    [TestCase(100501, 0)]
    [TestCase(100502, 1)]
    public void RenameGates_TurnOnAtExactly10_5_2(int patchNum, int expected)
    {
        using var conn = Schema.DataAccess.DbConnectionFactory.ForPlatform(Platform).GetDbConnection(MainConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        try
        {
            cmd.CommandText = $"SET @schemasmith_version_patch_override = {patchNum}";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "SELECT SchemaSmith_SupportsRenameColumn(), SchemaSmith_SupportsRenameIndex()";
            using var reader = cmd.ExecuteReader();
            reader.Read();
            Assert.That(new[] { System.Convert.ToInt32(reader.GetValue(0)), System.Convert.ToInt32(reader.GetValue(1)) },
                Is.EqualTo(new[] { expected, expected }));
        }
        finally
        {
            cmd.CommandText = "SET @schemasmith_version_patch_override = NULL";
            cmd.ExecuteNonQuery();
        }
    }
}
