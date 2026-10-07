// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.MariaDb;

/// <summary>
/// MariaDB accounts that authenticate with ed25519 (10.1.22+) or PARSEC (11.6+). The connector supports neither until
/// their plugins are installed, so a user on such an account could not connect at all. Each test creates a
/// throwaway account, connects through SchemaSmith's own connection factory, and drops the account.
/// </summary>
[Category("MariaDb")]
[Category("Integration")]
[TestFixture]
public class AuthenticationPluginTests
{
    private IDbConnection _admin = null!;
    private string _server = "", _port = "";

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        _server = config["MariaDB:Server"];
        _port = config["MariaDB:Port"];
        _admin = DbConnectionFactory.ForPlatform(Platform.MariaDb).GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        _admin.Open();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown() => _admin?.Dispose();

    private void Exec(string sql)
    {
        using var cmd = _admin.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private object Scalar(string sql)
    {
        using var cmd = _admin.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private void ConnectsAs(string plugin, string userName, string password, string createUser)
    {
        Exec($"DROP USER IF EXISTS {userName}@'%'");
        Exec(createUser);
        try
        {
            var cs = ConnectionString.Build(Platform.MariaDb, _server, "information_schema", userName, password, _port, new());
            using var conn = DbConnectionFactory.ForPlatform(Platform.MariaDb).GetDbConnection(cs);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT CURRENT_USER()";
            Assert.That(cmd.ExecuteScalar()?.ToString(), Does.StartWith(userName), $"connected through {plugin}");
        }
        finally
        {
            Exec($"DROP USER IF EXISTS {userName}@'%'");
        }
    }

    [Test]
    public void AnEd25519Account_CanConnect()
    {
        if (Convert.ToInt64(Scalar("SELECT COUNT(*) FROM information_schema.PLUGINS WHERE PLUGIN_NAME = 'ed25519' AND PLUGIN_STATUS = 'ACTIVE'")) == 0)
            Exec("INSTALL SONAME 'auth_ed25519'");
        ConnectsAs("ed25519", "ss_auth_ed25519", "Ed25519!Probe",
            "CREATE USER ss_auth_ed25519@'%' IDENTIFIED VIA ed25519 USING PASSWORD('Ed25519!Probe')");
    }

    [Test]
    public void AParsecAccount_CanConnect()
    {
        var version = VersionHelper.ParsePatchComparable(Scalar("SELECT VERSION()")?.ToString(), Platform.MariaDb) ?? 0;
        if (version < 110600)
            Assert.Ignore("PARSEC arrived in MariaDB 11.6.");
        if (Convert.ToInt64(Scalar("SELECT COUNT(*) FROM information_schema.PLUGINS WHERE PLUGIN_NAME = 'parsec' AND PLUGIN_STATUS = 'ACTIVE'")) == 0)
            Exec("INSTALL SONAME 'auth_parsec'");
        ConnectsAs("parsec", "ss_auth_parsec", "Parsec!Probe",
            "CREATE USER ss_auth_parsec@'%' IDENTIFIED VIA parsec USING PASSWORD('Parsec!Probe')");
    }
}
