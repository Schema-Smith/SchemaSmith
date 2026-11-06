// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using Schema.DataAccess;
using Schema.Domain;

namespace Schema.UnitTests.DataAccess;

[TestFixture]
public class ConnectionStringTests
{
    [Test]
    public void Build_SqlServer_WithCredentials_ReturnsCorrectFormat()
    {
        var result = ConnectionString.Build(Platform.SqlServer, "myserver", "mydb", "myuser", "mypass");

        Assert.That(result, Does.Contain("data source=myserver"));
        Assert.That(result, Does.Contain("Initial Catalog=mydb"));
        Assert.That(result, Does.Contain("User ID=myuser"));
        Assert.That(result, Does.Contain("Password=mypass"));
    }

    [Test]
    public void Build_SqlServer_WithoutCredentials_UsesIntegratedSecurity()
    {
        var result = ConnectionString.Build(Platform.SqlServer, "myserver", "mydb", "", "");

        Assert.That(result, Does.Contain("Integrated Security=True"));
        Assert.That(result, Does.Not.Contain("User ID="));
        Assert.That(result, Does.Not.Contain("Password="));
    }

    [Test]
    public void Build_SqlServer_NullCredentials_UsesIntegratedSecurity()
    {
        var result = ConnectionString.Build(Platform.SqlServer, "myserver", "mydb", null, null);

        Assert.That(result, Does.Contain("Integrated Security=True"));
    }

    [Test]
    public void Build_SqlServer_IntegratedSecurityFlag_ForcesIntegrated_EvenWithCredentials()
    {
        // A checked-in settings file may carry "User": "sa"; an operator opting into Windows
        // Authentication via Target:IntegratedSecurity=true must reach integrated auth even though a
        // credential is present — an override cannot clear the file's credential on Windows (setting
        // an env var to empty deletes it), so the credential must be superseded, not cleared.
        var result = ConnectionString.Build(Platform.SqlServer, "myserver", "mydb", "sa", "pw",
            integratedSecurity: true);

        Assert.That(result, Does.Contain("Integrated Security=True"));
        Assert.That(result, Does.Not.Contain("User ID="));
        Assert.That(result, Does.Not.Contain("Password="));
    }

    [Test]
    public void Build_SqlServer_IntegratedSecurityFalse_WithCredentials_UsesCredentials()
    {
        var result = ConnectionString.Build(Platform.SqlServer, "myserver", "mydb", "sa", "pw",
            integratedSecurity: false);

        Assert.That(result, Does.Contain("User ID=sa"));
        Assert.That(result, Does.Not.Contain("Integrated Security"));
    }

    [Test]
    public void Build_SqlServer_EmitsExplicitEncryptTrue_ByDefault()
    {
        var result = ConnectionString.Build(Platform.SqlServer, "myserver", "mydb", "u", "p");

        Assert.That(result, Does.Contain("Encrypt=True"));
    }

    [Test]
    public void Build_SqlServer_OperatorEncryptOverride_Wins_AndNotDuplicated()
    {
        var props = new Dictionary<string, string> { ["Encrypt"] = "False" };

        var result = ConnectionString.Build(Platform.SqlServer, "myserver", "mydb", "u", "p", null, props);

        Assert.That(result, Does.Contain("Encrypt=False"));
        Assert.That(System.Text.RegularExpressions.Regex.Matches(result, "Encrypt=").Count, Is.EqualTo(1),
            "Encrypt must appear exactly once — the operator override replaces the default, not adds to it");
    }

    [Test]
    public void Build_SqlServer_OperatorEncryptOverride_IsCaseInsensitive()
    {
        var props = new Dictionary<string, string> { ["encrypt"] = "False" };

        var result = ConnectionString.Build(Platform.SqlServer, "myserver", "mydb", "u", "p", null, props);

        Assert.That(System.Text.RegularExpressions.Regex.Matches(result, "(?i)encrypt=").Count, Is.EqualTo(1),
            "a lower-case operator 'encrypt' key must still suppress the default, not double up");
    }

    [Test]
    public void Build_PostgreSQL_ReturnsCorrectFormat()
    {
        var result = ConnectionString.Build(Platform.PostgreSQL, "pghost", "pgdb", "pguser", "pgpass");

        Assert.That(result, Does.Contain("Host=pghost"));
        Assert.That(result, Does.Contain("Database=pgdb"));
        Assert.That(result, Does.Contain("Username=pguser"));
        Assert.That(result, Does.Contain("Password=pgpass"));
    }

    [Test]
    public void Build_MySQL_ReturnsCorrectFormat()
    {
        var result = ConnectionString.Build(Platform.MySQL, "myhost", "mydb", "myuid", "mypwd");

        Assert.That(result, Does.Contain("Server=myhost"));
        Assert.That(result, Does.Contain("Database=mydb"));
        Assert.That(result, Does.Contain("Uid=myuid"));
        Assert.That(result, Does.Contain("Pwd=mypwd"));
        Assert.That(result, Does.Contain("AllowUserVariables=true"));
    }

    [Test]
    public void Build_SqlServer_WithPort_AppendsCommaPort()
    {
        var result = ConnectionString.Build(Platform.SqlServer, "myserver", "mydb", "myuser", "mypass", "1440");

        Assert.That(result, Does.Contain("data source=myserver,1440"));
    }

    [Test]
    public void Build_SqlServer_NullPort_NoCommaAppended()
    {
        var result = ConnectionString.Build(Platform.SqlServer, "myserver", "mydb", "myuser", "mypass", null);

        Assert.That(result, Does.Contain("data source=myserver;"));
        Assert.That(result, Does.Not.Contain(","));
    }

    [Test]
    public void Build_PostgreSQL_WithPort_IncludesPortParameter()
    {
        var result = ConnectionString.Build(Platform.PostgreSQL, "pghost", "pgdb", "pguser", "pgpass", "5433");

        Assert.That(result, Does.Contain("Port=5433;"));
        Assert.That(result, Does.Contain("Host=pghost"));
    }

    [Test]
    public void Build_PostgreSQL_NullPort_NoPortParameter()
    {
        var result = ConnectionString.Build(Platform.PostgreSQL, "pghost", "pgdb", "pguser", "pgpass", null);

        Assert.That(result, Does.Not.Contain("Port="));
    }

    [Test]
    public void Build_MySQL_WithPort_IncludesPortParameter()
    {
        var result = ConnectionString.Build(Platform.MySQL, "myhost", "mydb", "myuid", "mypwd", "3307");

        Assert.That(result, Does.Contain("Port=3307;"));
        Assert.That(result, Does.Contain("Server=myhost"));
    }

    [Test]
    public void Build_MySQL_NullPort_NoPortParameter()
    {
        var result = ConnectionString.Build(Platform.MySQL, "myhost", "mydb", "myuid", "mypwd", null);

        Assert.That(result, Does.Not.Contain("Port="));
    }

    [Test]
    public void Build_EmptyPort_TreatedAsNoPort()
    {
        var sqlServer = ConnectionString.Build(Platform.SqlServer, "server", "db", "user", "pass", "");
        var postgres = ConnectionString.Build(Platform.PostgreSQL, "server", "db", "user", "pass", "  ");
        var mysql = ConnectionString.Build(Platform.MySQL, "server", "db", "user", "pass", "");

        Assert.That(sqlServer, Does.Not.Contain(","));
        Assert.That(postgres, Does.Not.Contain("Port="));
        Assert.That(mysql, Does.Not.Contain("Port="));
    }

    [Test]
    public void Build_InvalidPlatform_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(() =>
            ConnectionString.Build((Platform)999, "server", "db", "user", "pass"));
    }

    [Test]
    public void Build_SqlServer_WithConnectionProperties_AppendsProperties()
    {
        var props = new Dictionary<string, string>
        {
            { "TrustServerCertificate", "True" },
            { "Column Encryption Setting", "Enabled" }
        };
        var result = ConnectionString.Build(Platform.SqlServer, "myserver", "mydb", "myuser", "mypass",
            connectionProperties: props);
        Assert.That(result, Does.Contain("TrustServerCertificate=True"));
        Assert.That(result, Does.Contain("Column Encryption Setting=Enabled"));
    }

    [Test]
    public void Build_PostgreSQL_WithConnectionProperties_AppendsProperties()
    {
        var props = new Dictionary<string, string>
        {
            { "Trust Server Certificate", "True" },
            { "SSL Mode", "Require" }
        };
        var result = ConnectionString.Build(Platform.PostgreSQL, "pghost", "pgdb", "pguser", "pgpass",
            connectionProperties: props);
        Assert.That(result, Does.Contain("Trust Server Certificate=True"));
        Assert.That(result, Does.Contain("SSL Mode=Require"));
    }

    [Test]
    public void Build_MySQL_WithConnectionProperties_AppendsProperties()
    {
        var props = new Dictionary<string, string> { { "SslMode", "Required" } };
        var result = ConnectionString.Build(Platform.MySQL, "myhost", "mydb", "myuid", "mypwd",
            connectionProperties: props);
        Assert.That(result, Does.Contain("SslMode=Required"));
    }

    [Test]
    public void Build_NullConnectionProperties_ProducesValidConnectionString()
    {
        var result = ConnectionString.Build(Platform.SqlServer, "myserver", "mydb", "myuser", "mypass",
            connectionProperties: null);
        Assert.That(result, Does.Contain("data source=myserver"));
        Assert.That(result, Does.Contain("Initial Catalog=mydb"));
    }

    [Test]
    public void Build_EmptyConnectionProperties_ProducesValidConnectionString()
    {
        var result = ConnectionString.Build(Platform.SqlServer, "myserver", "mydb", "myuser", "mypass",
            connectionProperties: new Dictionary<string, string>());
        Assert.That(result, Does.Contain("data source=myserver"));
    }

    [Test]
    public void Build_MySQL_StructuralPropertySkipped_WhenUserTriesToOverride()
    {
        var props = new Dictionary<string, string> { { "AllowUserVariables", "false" } };
        var result = ConnectionString.Build(Platform.MySQL, "myhost", "mydb", "myuid", "mypwd",
            connectionProperties: props);
        Assert.That(result, Does.Contain("AllowUserVariables=true"));
        var count = result.Split("AllowUserVariables").Length - 1;
        Assert.That(count, Is.EqualTo(1), "AllowUserVariables should appear exactly once");
    }

    [Test]
    public void Build_SqlServer_NoHardcodedTrustServerCertificate()
    {
        var result = ConnectionString.Build(Platform.SqlServer, "myserver", "mydb", "myuser", "mypass");
        Assert.That(result, Does.Not.Contain("TrustServerCertificate"));
        Assert.That(result, Does.Not.Contain("ApplicationIntent"));
    }

    [Test]
    public void Build_PostgreSQL_NoHardcodedTrustServerCertificate()
    {
        var result = ConnectionString.Build(Platform.PostgreSQL, "pghost", "pgdb", "pguser", "pgpass");
        Assert.That(result, Does.Not.Contain("Trust Server Certificate"));
    }

    // ---- RetargetDatabase — issue #248 ----------------------------------------------------------
    //
    // Schema-template discovery iterates multiple databases per server, but a user-supplied
    // --ConnectionString override embeds whatever database the operator put in it (commonly an
    // admin DB: master / postgres / a privileged login DB). Without retargeting, every per-DB
    // operation runs against the override's embedded DB instead of the discovered target. The
    // helper rebuilds the override using the platform's ConnectionStringBuilder, swapping the
    // database/catalog property to the discovered target while preserving host, auth, and any
    // other connection options.

    [Test]
    public void RetargetDatabase_SqlServer_ReplacesInitialCatalog()
    {
        var original = "data source=myserver;Initial Catalog=master;User ID=u;Password=p;";

        var result = ConnectionString.RetargetDatabase(original, "tenant1", Platform.SqlServer);

        Assert.Multiple(() =>
        {
            Assert.That(result, Does.Contain("Initial Catalog=tenant1"));
            Assert.That(result, Does.Not.Contain("Initial Catalog=master"));
            Assert.That(result, Does.Contain("myserver"), "host preserved");
            Assert.That(result, Does.Contain("User ID=u").IgnoreCase, "user preserved");
            Assert.That(result, Does.Contain("Password=p").IgnoreCase, "password preserved");
        });
    }

    [Test]
    public void RetargetDatabase_PostgreSQL_ReplacesDatabase()
    {
        var original = "Host=pghost;Port=5432;Database=postgres;Username=pguser;Password=pgpass;";

        var result = ConnectionString.RetargetDatabase(original, "tenant1", Platform.PostgreSQL);

        Assert.Multiple(() =>
        {
            Assert.That(result, Does.Contain("Database=tenant1"));
            Assert.That(result, Does.Not.Contain("Database=postgres"));
            Assert.That(result, Does.Contain("pghost"), "host preserved");
            Assert.That(result, Does.Contain("Port=5432"), "port preserved");
            Assert.That(result, Does.Contain("pguser").IgnoreCase, "user preserved");
        });
    }

    [Test]
    public void RetargetDatabase_MySQL_ReplacesDatabase()
    {
        var original = "Server=myhost;Database=admin;Uid=myuid;Pwd=mypwd;AllowUserVariables=true;";

        var result = ConnectionString.RetargetDatabase(original, "tenant1", Platform.MySQL);

        Assert.Multiple(() =>
        {
            Assert.That(result, Does.Contain("Database=tenant1").IgnoreCase);
            Assert.That(result, Does.Not.Contain("Database=admin").IgnoreCase);
            Assert.That(result, Does.Contain("myhost"), "host preserved");
            // MySqlConnectionStringBuilder normalizes the key to "Allow User Variables" (canonical with spaces).
            // Semantically equivalent — the connector parses both forms — but the round-tripped output
            // uses the canonical spacing. Asserting against the canonical form keeps the test honest.
            Assert.That(result, Does.Contain("Allow User Variables=True").IgnoreCase, "structural property preserved");
        });
    }

    [Test]
    public void RetargetDatabase_NullConnectionString_ReturnedAsIs()
    {
        var result = ConnectionString.RetargetDatabase(null, "tenant1", Platform.SqlServer);
        Assert.That(result, Is.Null);
    }

    [Test]
    public void RetargetDatabase_EmptyConnectionString_ReturnedAsIs()
    {
        var result = ConnectionString.RetargetDatabase("", "tenant1", Platform.SqlServer);
        Assert.That(result, Is.EqualTo(""));
    }

    [Test]
    public void RetargetDatabase_NullDatabaseName_ReturnedAsIs()
    {
        const string original = "data source=myserver;Initial Catalog=master;User ID=u;Password=p;";
        var result = ConnectionString.RetargetDatabase(original, null, Platform.SqlServer);
        Assert.That(result, Is.EqualTo(original));
    }

    [Test]
    public void RetargetDatabase_EmptyDatabaseName_ReturnedAsIs()
    {
        const string original = "data source=myserver;Initial Catalog=master;User ID=u;Password=p;";
        var result = ConnectionString.RetargetDatabase(original, "", Platform.SqlServer);
        Assert.That(result, Is.EqualTo(original));
    }

    [Test]
    public void RetargetDatabase_AlreadyTargetingThatDb_StillProducesCorrectTarget()
    {
        // No-op-shaped call: the override already targets the right DB. The result must still
        // produce a connection string that targets that DB (the caller should be free to invoke
        // RetargetDatabase without first checking whether retargeting is needed).
        var original = "data source=myserver;Initial Catalog=tenant1;User ID=u;Password=p;";
        var result = ConnectionString.RetargetDatabase(original, "tenant1", Platform.SqlServer);
        Assert.That(result, Does.Contain("Initial Catalog=tenant1"));
    }

    [Test]
    public void RetargetDatabase_InvalidPlatform_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(() =>
            ConnectionString.RetargetDatabase("data source=s;Initial Catalog=db;", "tenant1", (Platform)999));
    }

    // ---- RetargetServer: --ConnectionString on a secondary server (B18) ----------------------------------------------

    [TestCase("Data Source=primary,1440;Initial Catalog=db;User ID=u;Password=p", "secondary", "secondary,1440")]
    [TestCase("Data Source=primary;Initial Catalog=db;User ID=u;Password=p", "secondary", "secondary")]
    [TestCase("Data Source=primary,1440;Initial Catalog=db;User ID=u;Password=p", "secondary,2000", "secondary,2000")]
    public void RetargetServer_SqlServer_ChangesOnlyTheServer(string original, string server, string dataSource)
    {
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(ConnectionString.RetargetServer(original, server, Platform.SqlServer));
        Assert.Multiple(() =>
        {
            Assert.That(builder.DataSource, Is.EqualTo(dataSource));
            Assert.That(builder.UserID, Is.EqualTo("u"));
            Assert.That(builder.Password, Is.EqualTo("p"));
            Assert.That(builder.InitialCatalog, Is.EqualTo("db"));
        });
    }

    [Test]
    public void RetargetServer_PostgreSqlAndMySql_ChangeOnlyTheHost()
    {
        var pg = new Npgsql.NpgsqlConnectionStringBuilder(ConnectionString.RetargetServer("Host=primary;Port=5433;Database=db;Username=u;Password=p", "secondary", Platform.PostgreSQL));
        var my = new MySqlConnector.MySqlConnectionStringBuilder(ConnectionString.RetargetServer("Server=primary;Port=3307;Database=db;Uid=u;Pwd=p;AllowUserVariables=true", "secondary", Platform.MySQL));
        Assert.Multiple(() =>
        {
            Assert.That((pg.Host, pg.Port, pg.Username), Is.EqualTo(("secondary", 5433, "u")));
            Assert.That((my.Server, my.Port, my.UserID, my.AllowUserVariables), Is.EqualTo(("secondary", 3307u, "u", true)));
            Assert.That(ConnectionString.RetargetServer("", "secondary", Platform.MySQL), Is.EqualTo(""));
        });
    }
}
