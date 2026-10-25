// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Data;
using System;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;
using log4net;
using NSubstitute;
using System.IO;

namespace SchemaTongs.IntegrationTests.PostgreSQL;

/// <summary>
/// PostgreSQL SchemaTongs casting.
/// <para>The write counts here are a deliberate gate, not incidental: PostgreSQL emits SIX
/// <c>.schema</c> files (products, templates, tables, materializedviews, enumtypes, sequences) plus Product.json
/// and Template.json, so a new declarative type must be accounted for here or it has slipped in
/// unnoticed. Enum types took this from four to five; sequences to six.</para>
/// </summary>
[Category("PostgreSQL")]
public class SchemaTongsTests
{
    private string _integrationDb = "";
    private string _connectionString;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var connProps = ConnectionString.ReadProperties(config, "PostgreSQL:ConnectionProperties");
        _connectionString = ConnectionString.Build(Platform.PostgreSQL, config["PostgreSQL:Server"], "postgres", config["PostgreSQL:User"], config["PostgreSQL:Password"], config["PostgreSQL:Port"], connProps);
        _integrationDb = GenerateUniqueDBName(config["Source:database"] ?? "TongsTest");

        CreateTestDatabases();
    }

    [Test]
    public void ShouldCastTables()
    {
        var errorLog = Substitute.For<ILog>();
        var progressLog = Substitute.For<ILog>();
        var environment = Substitute.For<IEnvironment>();
        var file = Substitute.For<IFile>();
        var directory = Substitute.For<IDirectory>();
        lock (FactoryContainer.SharedLockObject)
        {
            LogFactory.Register("ErrorLog", errorLog);
            LogFactory.Register("ProgressLog", progressLog);
            FactoryContainer.Register(environment);
            FactoryContainer.Register(file);
            FactoryContainer.Register(directory);
            var config = SetupConfig();
            config["ShouldCast:Tables"] = "true";

            var tongs = new SchemaTongs(Platform.PostgreSQL);
            tongs.CastTemplate();

            file.Received(10).WriteAllText(Arg.Any<string>(), Arg.Any<string>());
            file.Received(7).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase(".schema")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("product.json")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("template.json")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase(Path.Join("Tables", "Test.TestTable.json"))), Arg.Any<string>());
            Shared.ExtractedJson.HasNoEmptyValues(file);

            config["ShouldCast:Tables"] = "false";
            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void ShouldCastViews()
    {
        var errorLog = Substitute.For<ILog>();
        var progressLog = Substitute.For<ILog>();
        var environment = Substitute.For<IEnvironment>();
        var file = Substitute.For<IFile>();
        var directory = Substitute.For<IDirectory>();
        lock (FactoryContainer.SharedLockObject)
        {
            LogFactory.Register("ErrorLog", errorLog);
            LogFactory.Register("ProgressLog", progressLog);
            FactoryContainer.Register(environment);
            FactoryContainer.Register(file);
            FactoryContainer.Register(directory);
            var config = SetupConfig();
            config["ShouldCast:Views"] = "true";

            var tongs = new SchemaTongs(Platform.PostgreSQL);
            tongs.CastTemplate();

            file.Received(10).WriteAllText(Arg.Any<string>(), Arg.Any<string>());
            file.Received(7).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase(".schema")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("product.json")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("template.json")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase(Path.Combine("Views", "Test.TestView.sql"))), Arg.Any<string>());

            config["ShouldCast:Views"] = "false";
            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }


    [Test]
    public void ShouldCastProcedures()
    {
        var errorLog = Substitute.For<ILog>();
        var progressLog = Substitute.For<ILog>();
        var environment = Substitute.For<IEnvironment>();
        var file = Substitute.For<IFile>();
        var directory = Substitute.For<IDirectory>();
        lock (FactoryContainer.SharedLockObject)
        {
            LogFactory.Register("ErrorLog", errorLog);
            LogFactory.Register("ProgressLog", progressLog);
            FactoryContainer.Register(environment);
            FactoryContainer.Register(file);
            FactoryContainer.Register(directory);
            var config = SetupConfig();
            config["ShouldCast:Procedures"] = "true";

            var tongs = new SchemaTongs(Platform.PostgreSQL);
            tongs.CastTemplate();

            file.Received(10).WriteAllText(Arg.Any<string>(), Arg.Any<string>());
            file.Received(7).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase(".schema")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("product.json")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("template.json")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase(Path.Combine("Procedures", "Test.TestProcedure.sql"))), Arg.Any<string>());

            config["ShouldCast:Procedures"] = "false";
            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void ShouldCastFunctions()
    {
        var errorLog = Substitute.For<ILog>();
        var progressLog = Substitute.For<ILog>();
        var environment = Substitute.For<IEnvironment>();
        var file = Substitute.For<IFile>();
        var directory = Substitute.For<IDirectory>();
        lock (FactoryContainer.SharedLockObject)
        {
            LogFactory.Register("ErrorLog", errorLog);
            LogFactory.Register("ProgressLog", progressLog);
            FactoryContainer.Register(environment);
            FactoryContainer.Register(file);
            FactoryContainer.Register(directory);
            var config = SetupConfig();
            config["ShouldCast:Functions"] = "true";

            var tongs = new SchemaTongs(Platform.PostgreSQL);
            tongs.CastTemplate();

            // 11, not 10: this is the one test that casts TWO object files (TestFunction + Test_Trigger),
            // so the 6 -> 7 schema-count bump from adding domaintypes lands on top of a base of 10, not 9.
            file.Received(11).WriteAllText(Arg.Any<string>(), Arg.Any<string>());
            file.Received(7).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase(".schema")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("product.json")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("template.json")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase(Path.Combine("Functions", "Test.TestFunction.sql"))), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase(Path.Combine("Functions", "Test.Test_Trigger.sql"))), Arg.Any<string>());

            config["ShouldCast:Functions"] = "false";
            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void ShouldCastDomainTypes()
    {
        var errorLog = Substitute.For<ILog>();
        var progressLog = Substitute.For<ILog>();
        var environment = Substitute.For<IEnvironment>();
        var file = Substitute.For<IFile>();
        var directory = Substitute.For<IDirectory>();
        lock (FactoryContainer.SharedLockObject)
        {
            LogFactory.Register("ErrorLog", errorLog);
            LogFactory.Register("ProgressLog", progressLog);
            FactoryContainer.Register(environment);
            FactoryContainer.Register(file);
            FactoryContainer.Register(directory);
            var config = SetupConfig();
            config["ShouldCast:DomainTypes"] = "true";

            var tongs = new SchemaTongs(Platform.PostgreSQL);
            tongs.CastTemplate();

            file.Received(10).WriteAllText(Arg.Any<string>(), Arg.Any<string>());
            file.Received(7).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase(".schema")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("product.json")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("template.json")), Arg.Any<string>());
            // Domain types are DECLARATIVE now (F5): a .json in Domain Types/, not a guarded .sql in Types/.
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase(Path.Join("Domain Types", "Test.Flag.json"))), Arg.Any<string>());

            config["ShouldCast:DomainTypes"] = "false";
            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void ShouldCastSchemas()
    {
        var errorLog = Substitute.For<ILog>();
        var progressLog = Substitute.For<ILog>();
        var environment = Substitute.For<IEnvironment>();
        var file = Substitute.For<IFile>();
        var directory = Substitute.For<IDirectory>();
        lock (FactoryContainer.SharedLockObject)
        {
            LogFactory.Register("ErrorLog", errorLog);
            LogFactory.Register("ProgressLog", progressLog);
            FactoryContainer.Register(environment);
            FactoryContainer.Register(file);
            FactoryContainer.Register(directory);
            var config = SetupConfig();
            config["ShouldCast:Schemas"] = "true";

            var tongs = new SchemaTongs(Platform.PostgreSQL);
            tongs.CastTemplate();

            file.Received(10).WriteAllText(Arg.Any<string>(), Arg.Any<string>());
            file.Received(7).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase(".schema")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("product.json")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("template.json")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase(Path.Combine("Schemas", "Test.sql"))), Arg.Any<string>());

            config["ShouldCast:Schemas"] = "false";
            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void ShouldCastTriggers()
    {
        var errorLog = Substitute.For<ILog>();
        var progressLog = Substitute.For<ILog>();
        var environment = Substitute.For<IEnvironment>();
        var file = Substitute.For<IFile>();
        var directory = Substitute.For<IDirectory>();
        lock (FactoryContainer.SharedLockObject)
        {
            LogFactory.Register("ErrorLog", errorLog);
            LogFactory.Register("ProgressLog", progressLog);
            FactoryContainer.Register(environment);
            FactoryContainer.Register(file);
            FactoryContainer.Register(directory);
            var config = SetupConfig();
            config["ShouldCast:TableTriggers"] = "true";

            var tongs = new SchemaTongs(Platform.PostgreSQL);
            tongs.CastTemplate();

            file.Received(10).WriteAllText(Arg.Any<string>(), Arg.Any<string>());
            file.Received(7).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase(".schema")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("product.json")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("template.json")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase(Path.Combine("Triggers", "Test.TestTable.TestTrigger.sql"))), Arg.Any<string>());

            config["ShouldCast:TableTriggers"] = "false";
            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    /// <summary>
    /// A column-level CheckExpression must survive an extract instead of coming back table-level —
    /// otherwise a cast → quench → cast cycle is not idempotent at the JSON level.
    /// <para>PostgreSQL records no marker for how a constraint was declared, so the generated
    /// <c>CK_&lt;table&gt;_&lt;column&gt;</c> name is the only evidence a check was authored
    /// column-level. A user-named single-column check must therefore stay table-level and keep its
    /// name — demoting it would rename it on the next apply and churn a drop/recreate every deploy.
    /// Both halves are asserted here because only asserting the first would let that regression in.</para>
    /// </summary>
    [Test]
    public void ShouldRoundTripColumnLevelCheckExpression_AndPreserveUserNamedTableLevelCheck()
    {
        var errorLog = Substitute.For<ILog>();
        var progressLog = Substitute.For<ILog>();
        var environment = Substitute.For<IEnvironment>();
        var file = Substitute.For<IFile>();
        var directory = Substitute.For<IDirectory>();
        lock (FactoryContainer.SharedLockObject)
        {
            ExecuteOnIntegrationDb(@"
ALTER TABLE ""Test"".""TestTable"" ADD CONSTRAINT ""CK_TestTable_Column1"" CHECK (""Column1"" >= 0);
ALTER TABLE ""Test"".""TestTable"" ADD CONSTRAINT ""chk_column2_not_blank"" CHECK (""Column2"" <> '');");
            try
            {
                LogFactory.Register("ErrorLog", errorLog);
                LogFactory.Register("ProgressLog", progressLog);
                FactoryContainer.Register(environment);
                FactoryContainer.Register(file);
                FactoryContainer.Register(directory);
                var config = SetupConfig();
                config["ShouldCast:Tables"] = "true";

                string tableJson = null;
                file.When(f => f.WriteAllText(
                        Arg.Is<string>(s => s.EndsWithIgnoringCase(Path.Join("Tables", "Test.TestTable.json"))),
                        Arg.Any<string>()))
                    .Do(ci => tableJson = ci.ArgAt<string>(1));

                new SchemaTongs(Platform.PostgreSQL).CastTemplate();

                Assert.That(tableJson, Is.Not.Null.And.Not.Empty, "the table JSON was not written");
                var table = (Schema.Domain.PostgreSQL.PostgreSqlTable)PlatformDeserializer.DeserializeTable(tableJson, Platform.PostgreSQL);

                var column1 = table.Columns.OfType<Schema.Domain.PostgreSQL.PostgreSqlColumn>()
                    .Single(c => StringHelper.StripIdentifierWrapper(c.Name) == "Column1");
                Assert.That(column1.CheckExpression, Is.Not.Null.And.Not.Empty,
                    "a CK_<table>_<column> check must round-trip onto its column");
                Assert.That(table.CheckConstraints.Any(c => StringHelper.StripIdentifierWrapper(c.Name) == "CK_TestTable_Column1"),
                    Is.False, "the demoted check must not also remain table-level");

                Assert.That(table.CheckConstraints.Any(c => StringHelper.StripIdentifierWrapper(c.Name) == "chk_column2_not_blank"),
                    Is.True, "a user-named single-column check must stay table-level and keep its name");

                config["ShouldCast:Tables"] = "false";
            }
            finally
            {
                ExecuteOnIntegrationDb(@"
ALTER TABLE ""Test"".""TestTable"" DROP CONSTRAINT IF EXISTS ""CK_TestTable_Column1"";
ALTER TABLE ""Test"".""TestTable"" DROP CONSTRAINT IF EXISTS ""chk_column2_not_blank"";");
                FactoryContainer.Clear();
                LogFactory.Clear();
            }
        }
    }

    private void ExecuteOnIntegrationDb(string sql)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_integrationDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    [OneTimeTearDown]
    public void RunAfterAnyTests()
    {
        DropTestDatabases();
    }

    private IConfigurationRoot SetupConfig()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);

        // Map PostgreSQL-specific config to Source:* keys used by SchemaTongs.GetConnection
        config["Source:Server"] = config["PostgreSQL:Server"] ?? "127.0.0.1";
        config["Source:Port"] = config["PostgreSQL:Port"];
        config["Source:User"] = config["PostgreSQL:User"];
        config["Source:Password"] = config["PostgreSQL:Password"];
        config["Source:database"] = _integrationDb;
        var connProps = ConnectionString.ReadProperties(config, "PostgreSQL:ConnectionProperties");
        foreach (var prop in connProps)
            config[$"Source:ConnectionProperties:{prop.Key}"] = prop.Value;

        config["ShouldCast:Tables"] = "false";
        config["ShouldCast:Schemas"] = "false";
        config["ShouldCast:DomainTypes"] = "false";
        config["ShouldCast:EnumTypes"] = "false";
        config["ShouldCast:CompositeTypes"] = "false";
        config["ShouldCast:Functions"] = "false";
        config["ShouldCast:Aggregates"] = "false";
        config["ShouldCast:Procedures"] = "false";
        config["ShouldCast:Sequences"] = "false";
        config["ShouldCast:Rules"] = "false";
        config["ShouldCast:TableTriggers"] = "false";
        config["ShouldCast:Views"] = "false";
        FactoryContainer.Register<IConfigurationRoot>(config);
        return config;
    }

    private void CreateTestDatabases()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @$"
CREATE DATABASE ""{_integrationDb}"";
";
        cmd.ExecuteNonQuery();

        conn.ChangeDatabase(_integrationDb);
        ForgeKindler.KindleTheForge(cmd, Platform.PostgreSQL);

        cmd.CommandText = @"
CREATE SCHEMA ""Test"";
CREATE DOMAIN ""Test"".""Flag"" AS BOOLEAN NOT NULL;

CREATE TABLE ""Test"".""TestTable"" (""Column1"" INT NOT NULL, ""Column2"" VARCHAR(200) NULL, ""Column3"" ""Test"".""Flag"");
CREATE UNIQUE INDEX UDX_Key ON ""Test"".""TestTable"" (""Column1"");

CREATE FUNCTION ""Test"".""Test_Trigger""()
  RETURNS TRIGGER
AS $$
DECLARE id INT;
BEGIN
    INSERT INTO ""Test"".""TestLog"" (Msg) VALUES ('Trigger fired for ID: ' || NEW.""Column1"");
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER ""TestTrigger""
AFTER INSERT ON ""Test"".""TestTable""
FOR EACH ROW
EXECUTE FUNCTION ""Test"".""Test_Trigger""();

CREATE VIEW ""Test"".""TestView""
AS
SELECT *
  FROM ""Test"".""TestTable"";

CREATE FUNCTION ""Test"".""TestFunction""(param INT) RETURNS INT
AS $$
BEGIN
    RETURN param;
END;
$$ LANGUAGE plpgsql;

CREATE PROCEDURE ""Test"".""TestProcedure""(param INT)
AS $$
DECLARE junk INT;
BEGIN
    SELECT param INTO junk;
END;
$$ LANGUAGE plpgsql;

";
        cmd.ExecuteNonQuery();

        conn.Close();
    }

    private static string GenerateUniqueDBName(string dbName)
    {
        dbName = dbName ?? throw new ArgumentNullException(nameof(dbName));
        var uniqueSegment = Guid.NewGuid().ToString().Replace(" - ", "_").Substring(0, 8);
        return $"{dbName}_Test_{DateTime.Now:yyyyMMdd_HHmmss}_{uniqueSegment}";
    }

    private void DropTestDatabases()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        DropOneDatabase(cmd, _integrationDb);

        conn.Close();
    }

    private static void DropOneDatabase(IDbCommand cmd, string dbName)
    {
        cmd.CommandText = @$"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{dbName}' AND pid <> pg_backend_pid();";
        cmd.ExecuteNonQuery();
        cmd.CommandText = @$"DROP DATABASE IF EXISTS ""{dbName}"";";
        cmd.ExecuteNonQuery();
    }
}
