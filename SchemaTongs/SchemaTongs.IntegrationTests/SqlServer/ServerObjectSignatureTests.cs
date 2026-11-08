// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace SchemaTongs.IntegrationTests.SqlServer;

/// <summary>
/// A server-object script runs its body only when the object's signature differs from the one it was cast with. Most
/// servers compare text without case, so a change only in case (a job description, an audit path) must still differ.
/// Run against the shared server: the scripts here change nothing.
/// </summary>
[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
public class ServerObjectSignatureTests
{
    private static object RunIfChanged(string signatureSql, string captured)
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var connectionString = ConnectionString.Build(Platform.SqlServer, config["SqlServer:Server"], "master", config["SqlServer:User"], config["SqlServer:Password"],
            config["SqlServer:Port"], ConnectionString.ReadProperties(config, "SqlServer:ConnectionProperties"));
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SqlServerServerObjectCaster.IfChanged(signatureSql, "x", captured, "SELECT N'ran';");
        return cmd.ExecuteScalar();
    }

    [Test]
    public void AChangeOnlyInCase_RunsTheBody_OnACaseInsensitiveServer() =>
        Assert.That(RunIfChanged("SELECT N'two steps' COLLATE SQL_Latin1_General_CP1_CI_AS", "TWO STEPS"), Is.EqualTo("ran"));

    [Test]
    public void TheSameSignature_LeavesTheObjectAlone() =>
        Assert.That(RunIfChanged("SELECT N'two steps' COLLATE SQL_Latin1_General_CP1_CI_AS", "two steps"), Is.Null);
}
