// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Linq;
using log4net;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaTongs.IntegrationTests.Shared;

/// <summary>
/// Shared SchemaTongs integration tests for the MySQL/MariaDb family. The MySQL and MariaDb
/// subclasses supply the platform + config + fixture accessors; every [Test] body here runs on both engines.
/// </summary>
public abstract class SchemaTongsSharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string ConfigPrefix { get; }
    protected abstract string FixtureConnectionString { get; }

    private string _integrationDb = "";
    private string _connectionString = "";

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        _connectionString = FixtureConnectionString;
        _integrationDb = GenerateUniqueDBName("TongsTest");
        CreateTestDatabase();
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

            var tongs = new SchemaTongs(Platform);
            tongs.CastTemplate();

            // Product.json, Template.json, json-schemas, and table JSON
            file.Received().WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("product.json")), Arg.Any<string>());
            file.Received().WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("template.json")), Arg.Any<string>());
            file.Received().WriteAllText(Arg.Is<string>(s => s.Contains("Tables") && s.EndsWithIgnoringCase(".json")), Arg.Any<string>());

            config["ShouldCast:Tables"] = "false";
            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    // MY-A2: a FULLTEXT index's WITH PARSER was deployed but never extracted, so a re-extracted package lost ngram. The
    // engines report it only in SHOW CREATE TABLE; MariaDB ships no full-text parser plugin to declare.
    [Test]
    public void ShouldCastAFullTextIndexWithItsParser()
    {
        if (Platform != Platform.MySQL) Assert.Ignore("MariaDB has no full-text parser plugin installed.");
        using (var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString + $"Database={_integrationDb};"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE NgramDocs (Id INT NOT NULL PRIMARY KEY, Body TEXT, FULLTEXT KEY ft_ngram (Body) WITH PARSER ngram, FULLTEXT KEY ft_plain (Body))";
            cmd.ExecuteNonQuery();
        }
        try
        {
            var file = Substitute.For<IFile>();
            var written = "";
            file.WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("NgramDocs.json")), Arg.Do<string>(c => written = c));
            lock (FactoryContainer.SharedLockObject)
            {
                LogFactory.Register("ErrorLog", Substitute.For<ILog>());
                LogFactory.Register("ProgressLog", Substitute.For<ILog>());
                FactoryContainer.Register(Substitute.For<IEnvironment>());
                FactoryContainer.Register(file);
                FactoryContainer.Register(Substitute.For<IDirectory>());
                var config = SetupConfig();
                config["ShouldCast:Tables"] = "true";
                config["ShouldCast:ObjectList"] = "NgramDocs";
                new SchemaTongs(Platform).CastTemplate();
                config["ShouldCast:Tables"] = "false";
                config["ShouldCast:ObjectList"] = "";
                FactoryContainer.Clear();
                LogFactory.Clear();
            }
            var table = (Schema.Domain.MySQL.MySqlTable)PlatformDeserializer.DeserializeTable(written, Platform);
            Assert.That(table.FullTextIndexes.Select(i => $"{i.Name.Trim('`')}:{i.Parser}"), Is.EquivalentTo(new[] { "ft_ngram:ngram", "ft_plain:" }));
        }
        finally
        {
            using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString + $"Database={_integrationDb};");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DROP TABLE IF EXISTS NgramDocs";
            cmd.ExecuteNonQuery();
        }
    }

    // Extraction used to replace the session mode with PIPES_AS_CONCAT before kindling, so procedures it created ran
    // non-strict and with || as concatenation, and SchemaQuench then found the stamp current and reused them.
    [Test]
    public void ShouldKindleUnderTheServersOwnMode()
    {
        using (var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString + $"Database={_integrationDb};"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM SchemaSmith_KindleStamp";
            cmd.ExecuteNonQuery();
        }

        lock (FactoryContainer.SharedLockObject)
        {
            LogFactory.Register("ErrorLog", Substitute.For<ILog>());
            LogFactory.Register("ProgressLog", Substitute.For<ILog>());
            FactoryContainer.Register(Substitute.For<IEnvironment>());
            FactoryContainer.Register(Substitute.For<IFile>());
            FactoryContainer.Register(Substitute.For<IDirectory>());
            var config = SetupConfig();
            config["ShouldCast:Tables"] = "true";
            new SchemaTongs(Platform).CastTemplate();
            config["ShouldCast:Tables"] = "false";
            FactoryContainer.Clear();
            LogFactory.Clear();
        }

        using var check = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString + $"Database={_integrationDb};");
        check.Open();
        using var query = check.CreateCommand();
        query.CommandText = $"SELECT COUNT(*) FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA = '{_integrationDb}' " +
                            "AND CONCAT(',', SQL_MODE, ',') LIKE '%,PIPES_AS_CONCAT,%'";
        Assert.That(Convert.ToInt32(query.ExecuteScalar()), Is.Zero, "no procedure may be kindled under the extraction session's mode");
        query.CommandText = $"SELECT COUNT(*) FROM SchemaSmith_KindleStamp";
        Assert.That(Convert.ToInt32(query.ExecuteScalar()), Is.EqualTo(1), "the extraction did kindle");
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

            var tongs = new SchemaTongs(Platform);
            tongs.CastTemplate();

            file.Received().WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("product.json")), Arg.Any<string>());
            file.Received().WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("template.json")), Arg.Any<string>());
            file.Received().WriteAllText(Arg.Is<string>(s => s.Contains("Views") && s.EndsWithIgnoringCase("TestView.sql")), Arg.Any<string>());

            config["ShouldCast:Views"] = "false";
            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void ShouldCastStoredProcedures()
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

            string script = null;
            file.When(f => f.WriteAllText(Arg.Is<string>(s => s.Contains("Procedures") && s.EndsWithIgnoringCase("TestProcedure.sql")), Arg.Any<string>()))
                .Do(ci => script = ci.ArgAt<string>(1));

            var tongs = new SchemaTongs(Platform);
            tongs.CastTemplate();

            file.Received().WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("product.json")), Arg.Any<string>());
            file.Received().WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("template.json")), Arg.Any<string>());
            file.Received().WriteAllText(Arg.Is<string>(s => s.Contains("Procedures") && s.EndsWithIgnoringCase("TestProcedure.sql")), Arg.Any<string>());

            // Assert on content, not just that a file was written: a NULL in the script-building
            // concatenation yields an empty file, which the per-row extraction counter reports as a
            // success. The parameter check also guards the signature against a stray return row.
            Assert.That(script, Is.Not.Null.And.Not.Empty, "TestProcedure.sql was written empty");
            Assert.That(script, Does.Contain("CREATE PROCEDURE"));
            Assert.That(script, Does.Contain("(IN param "));

            config["ShouldCast:Procedures"] = "false";
            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void ShouldCastUserDefinedFunctions()
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

            string script = null;
            file.When(f => f.WriteAllText(Arg.Is<string>(s => s.Contains("Functions") && s.EndsWithIgnoringCase("TestFunction.sql")), Arg.Any<string>()))
                .Do(ci => script = ci.ArgAt<string>(1));

            var tongs = new SchemaTongs(Platform);
            tongs.CastTemplate();

            file.Received().WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("product.json")), Arg.Any<string>());
            file.Received().WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("template.json")), Arg.Any<string>());
            file.Received().WriteAllText(Arg.Is<string>(s => s.Contains("Functions") && s.EndsWithIgnoringCase("TestFunction.sql")), Arg.Any<string>());

            // See ShouldCastStoredProcedures. "(param " additionally pins the signature: a function's
            // return value is a parameter row with a NULL name, and including it would emit a phantom
            // leading parameter.
            Assert.That(script, Is.Not.Null.And.Not.Empty, "TestFunction.sql was written empty");
            Assert.That(script, Does.Contain("CREATE FUNCTION"));
            Assert.That(script, Does.Contain("(param "));
            Assert.That(script, Does.Contain("RETURNS "));

            config["ShouldCast:Functions"] = "false";
            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void ShouldCastTableTriggers()
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

            var tongs = new SchemaTongs(Platform);
            tongs.CastTemplate();

            file.Received().WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("product.json")), Arg.Any<string>());
            file.Received().WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("template.json")), Arg.Any<string>());
            file.Received().WriteAllText(Arg.Is<string>(s => s.Contains("Triggers") && s.EndsWithIgnoringCase("TestTrigger.sql")), Arg.Any<string>());

            config["ShouldCast:TableTriggers"] = "false";
            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    // Validation used to begin its transaction while the extraction reader was still open, so the first script
    // killed the run with a protocol error (exit 3) before anything was validated; and the script it would then have
    // run began with the real object's DROP ... IF EXISTS, which no rollback restores on this family.
    [Test]
    public void ShouldValidateScriptsWithoutFailingOrTouchingTheSource()
    {
        var file = Substitute.For<IFile>();
        var written = new List<string>();
        file.When(f => f.WriteAllText(Arg.Any<string>(), Arg.Any<string>())).Do(ci => written.Add(ci.ArgAt<string>(0)));
        lock (FactoryContainer.SharedLockObject)
        {
            LogFactory.Register("ErrorLog", Substitute.For<ILog>());
            LogFactory.Register("ProgressLog", Substitute.For<ILog>());
            FactoryContainer.Register(Substitute.For<IEnvironment>());
            FactoryContainer.Register(file);
            FactoryContainer.Register(Substitute.For<IDirectory>());
            var config = SetupConfig();
            config["ShouldCast:Views"] = "true";
            config["ShouldCast:Functions"] = "true";
            config["ShouldCast:Procedures"] = "true";
            config["ShouldCast:TableTriggers"] = "true";
            config["ShouldCast:ValidateScripts"] = "true";

            Assert.DoesNotThrow(() => new SchemaTongs(Platform).CastTemplate(), "validation must not fail the extraction");

            config["ShouldCast:Views"] = "false";
            config["ShouldCast:Functions"] = "false";
            config["ShouldCast:Procedures"] = "false";
            config["ShouldCast:TableTriggers"] = "false";
            config["ShouldCast:ValidateScripts"] = "false";
            FactoryContainer.Clear();
            LogFactory.Clear();
        }

        Assert.That(written.Where(w => w.EndsWith(".sqlerror", StringComparison.OrdinalIgnoreCase)), Is.Empty,
            "every fixture object is valid, so none may be reported invalid");
        foreach (var name in new[] { "TestView.sql", "TestFunction.sql", "TestProcedure.sql", "TestTrigger.sql" })
            Assert.That(written.Any(w => w.EndsWith(name, StringComparison.OrdinalIgnoreCase)), Is.True, $"{name} was not written");
        file.DidNotReceive().Delete(Arg.Is<string>(s => s.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)));

        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString + $"Database={_integrationDb};");
        conn.Open();
        using var query = conn.CreateCommand();
        query.CommandText = $"SELECT COUNT(*) FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA = '{_integrationDb}' AND ROUTINE_NAME IN ('TestFunction', 'TestProcedure')";
        Assert.That(Convert.ToInt32(query.ExecuteScalar()), Is.EqualTo(2), "validation must not drop the source's own routines");
        query.CommandText = $"SELECT COUNT(*) FROM information_schema.VIEWS WHERE TABLE_SCHEMA = '{_integrationDb}' AND TABLE_NAME = 'TestView'";
        Assert.That(Convert.ToInt32(query.ExecuteScalar()), Is.EqualTo(1), "validation must not drop the source's own view");
        query.CommandText = $"SELECT COUNT(*) FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA = '{_integrationDb}' AND ROUTINE_NAME LIKE '\\_validate\\_%'";
        Assert.That(Convert.ToInt32(query.ExecuteScalar()), Is.Zero, "validation must drop the temporary object it created");
        query.CommandText = $"SELECT COUNT(*) FROM information_schema.VIEWS WHERE TABLE_SCHEMA = '{_integrationDb}' AND TABLE_NAME LIKE '\\_validate\\_%'";
        Assert.That(Convert.ToInt32(query.ExecuteScalar()), Is.Zero, "validation must drop the temporary view it created");
    }

    [Test]
    public void ShouldCastEvents()
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
            config["ShouldCast:Events"] = "true";

            string disabledJson = null;
            string enabledJson = null;
            string slaveDisabledJson = null;
            file.When(f => f.WriteAllText(Arg.Is<string>(s => s.Contains("Events") && s.EndsWithIgnoringCase("TestEvent.json")), Arg.Any<string>()))
                .Do(ci => disabledJson = ci.ArgAt<string>(1));
            file.When(f => f.WriteAllText(Arg.Is<string>(s => s.Contains("Events") && s.EndsWithIgnoringCase("TestEventEnabled.json")), Arg.Any<string>()))
                .Do(ci => enabledJson = ci.ArgAt<string>(1));
            file.When(f => f.WriteAllText(Arg.Is<string>(s => s.Contains("Events") && s.EndsWithIgnoringCase("TestEventSlaveDisabled.json")), Arg.Any<string>()))
                .Do(ci => slaveDisabledJson = ci.ArgAt<string>(1));
            // A package from an earlier release holds the event under the old dotted name.
            file.Exists(Arg.Is<string>(s => System.IO.Path.GetFileName(s) == ".TestEvent.json")).Returns(true);

            var tongs = new SchemaTongs(Platform);
            tongs.CastTemplate();

            file.Received(1).Delete(Arg.Is<string>(s => s.Contains("Events") && System.IO.Path.GetFileName(s) == ".TestEvent.json"));
            file.DidNotReceive().Delete(Arg.Is<string>(s => System.IO.Path.GetFileName(s) == "TestEvent.json"));

            file.Received().WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("product.json")), Arg.Any<string>());
            file.Received().WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("template.json")), Arg.Any<string>());
            // Events are now cast as DECLARATIVE .json rather than raw .sql (F4). The .sql form still
            // DEPLOYS -- a hand-written script in Events/ runs exactly as before -- but extraction now
            // writes the declared form, which is what can be compared and converged.
            file.Received().WriteAllText(Arg.Is<string>(s => s.Contains("Events") && System.IO.Path.GetFileName(s) == "TestEvent.json"), Arg.Any<string>());
            file.Received().WriteAllText(Arg.Is<string>(s => s.Contains("Events") && System.IO.Path.GetFileName(s) == "TestEventEnabled.json"), Arg.Any<string>());
            file.Received().WriteAllText(Arg.Is<string>(s => s.Contains("Events") && System.IO.Path.GetFileName(s) == "TestEventSlaveDisabled.json"), Arg.Any<string>());

            // What this test exists for is UNCHANGED (#391): INFORMATION_SCHEMA.EVENTS.STATUS reports
            // ENABLED / DISABLED / SLAVESIDE_DISABLED, and the package must carry the spelling an author
            // WRITES -- ENABLE / DISABLE / DISABLE ON SLAVE. Only the surface moved, from emitted DDL to
            // a JSON property.
            //
            // The closing quote is load-bearing, exactly as the DDL anchor was: DISABLED contains
            // DISABLE and SLAVESIDE_DISABLED contains DISABLE, so an unanchored check would pass on the
            // raw catalog value this translation exists to replace.
            Assert.That(disabledJson, Does.Contain("\"Status\": \"DISABLE\""), disabledJson);
            Assert.That(slaveDisabledJson, Does.Contain("\"Status\": \"DISABLE ON SLAVE\""), slaveDisabledJson);

            // ENABLE is the DECLARED default now, so an enabled event omits the key entirely rather than
            // restating it -- an omitted Status means ENABLE on the way back in. This still guards the #391
            // translation just as tightly as asserting the literal did: a broken translation emits the raw
            // catalog spelling "ENABLED", which is NOT the default and would therefore be written to the
            // file. So "no Status key at all" can only be produced by a translation that got it right.
            //
            // Assert the file was written FIRST. An absence check passes vacuously against a null string,
            // so without this the whole assertion would go green if extraction stopped emitting the event
            // altogether -- the loudest possible regression, silently.
            Assert.That(enabledJson, Is.Not.Null.And.Contains("\"Name\""),
                "the enabled event must actually have been extracted before its Status can be meaningfully absent");
            Assert.That(enabledJson, Does.Not.Contain("\"Status\""), enabledJson);

            config["ShouldCast:Events"] = "false";
            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    // A sequence script runs on every deploy. Extracted as CREATE OR REPLACE SEQUENCE, each deploy reset the sequence to
    // its START value, so NEXTVAL handed out numbers it had already given (verified on released 2.7.1). The extracted
    // script must leave an existing sequence where it is.
    [Test]
    public void ShouldCastSequencesThatSurviveARedeploy()
    {
        if (Platform != Platform.MariaDb) Assert.Ignore("Sequences are MariaDB-only in the MySQL family.");
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString + $"Database={_integrationDb};");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT VERSION()";
        if (VersionHelper.ParsePatchComparable(cmd.ExecuteScalar()?.ToString(), Platform) < 100300)
            Assert.Ignore("Sequences arrived in MariaDB 10.3.");
        cmd.CommandText = "CREATE OR REPLACE SEQUENCE TestSeq START WITH 100 INCREMENT BY 1";
        cmd.ExecuteNonQuery();

        string json = null;
        string script = null;
        var file = Substitute.For<IFile>();
        file.When(f => f.WriteAllText(Arg.Is<string>(s => s.Contains("Sequences") && s.EndsWithIgnoringCase("TestSeq.json")), Arg.Any<string>()))
            .Do(ci => json = ci.ArgAt<string>(1));
        file.When(f => f.WriteAllText(Arg.Is<string>(s => s.Contains("Sequences") && s.EndsWithIgnoringCase("TestSeq.sql")), Arg.Any<string>()))
            .Do(ci => script = ci.ArgAt<string>(1));
        try
        {
            lock (FactoryContainer.SharedLockObject)
            {
                LogFactory.Register("ErrorLog", Substitute.For<ILog>());
                LogFactory.Register("ProgressLog", Substitute.For<ILog>());
                FactoryContainer.Register(Substitute.For<IEnvironment>());
                FactoryContainer.Register(file);
                FactoryContainer.Register(Substitute.For<IDirectory>());
                SetupConfig();
                new SchemaTongs(Platform).CastTemplate();
                FactoryContainer.Clear();
                LogFactory.Clear();
            }
            Assert.Multiple(() =>
            {
                Assert.That(json, Is.Not.Null, "the sequence must be extracted as declared JSON");
                Assert.That(script, Is.Null, "a sequence is no longer written as a script");
            });
            Assert.That(json, Does.Contain("\"Start\": 100").And.Not.Contain("MinValue").And.Not.Contain("next"),
                "the file carries what the author chose and never the current value: " + json);

            cmd.CommandText = "SELECT NEXTVAL(TestSeq)";
            Assert.That(Convert.ToInt64(cmd.ExecuteScalar()), Is.EqualTo(100));
            cmd.CommandText = "SELECT NEXTVAL(TestSeq)";
            cmd.ExecuteScalar();
            for (var deploy = 0; deploy < 2; deploy++)
            {
                cmd.CommandText = $"CALL SchemaSmith_SequenceQuench('{_integrationDb}', '[{json.Replace("'", "''")}]', 0)";
                cmd.ExecuteNonQuery();
            }
            cmd.CommandText = "SELECT NEXTVAL(TestSeq)";
            Assert.That(Convert.ToInt64(cmd.ExecuteScalar()), Is.EqualTo(102), "deploying the extracted sequence must not reset it: " + json);
        }
        finally
        {
            cmd.CommandText = "DROP SEQUENCE IF EXISTS TestSeq";
            cmd.ExecuteNonQuery();
        }
    }

    [Test]
    public void ShouldExtractMultipleObjectTypes()
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
            config["ShouldCast:Views"] = "true";
            config["ShouldCast:Procedures"] = "true";

            var tongs = new SchemaTongs(Platform);
            tongs.CastTemplate();

            // Should have extracted tables, views, and procedures
            file.Received().WriteAllText(Arg.Is<string>(s => s.Contains("Tables") && s.EndsWithIgnoringCase(".json")), Arg.Any<string>());
            file.Received().WriteAllText(Arg.Is<string>(s => s.Contains("Views") && s.EndsWithIgnoringCase(".sql")), Arg.Any<string>());
            file.Received().WriteAllText(Arg.Is<string>(s => s.Contains("Procedures") && s.EndsWithIgnoringCase(".sql")), Arg.Any<string>());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void ShouldLogProgressMessages()
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

            var tongs = new SchemaTongs(Platform);
            tongs.CastTemplate();

            // Verify progress was logged
            progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Kindling The Forge")));
            progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Casting Table Structures")));
            // Each table on its own line in one form on every engine; MySQL and MariaDB printed "[1/7] Extracting Album...".
            // One line per table: as many as the table files written.
            var castLines = progressLog.ReceivedCalls().Count(c => c.GetMethodInfo().Name == "Info" && c.GetArguments()[0] is string m && m.StartsWith("  Cast Json for "));
            var tableFiles = file.ReceivedCalls().Count(c => c.GetMethodInfo().Name == "WriteAllText" && c.GetArguments()[0] is string path
                                                          && path.Contains("Tables") && path.EndsWith(".json"));
            Assert.That(castLines, Is.GreaterThan(0).And.EqualTo(tableFiles), "a progress line for each table written");
            progressLog.DidNotReceive().Info(Arg.Is<string>(s => s.Contains("] Extracting ")));
            progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Casting Summary")));

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [OneTimeTearDown]
    public void RunAfterAnyTests()
    {
        DropTestDatabase();
    }

    private IConfigurationRoot SetupConfig()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);

        // Map platform-specific config to Source:* keys used by SchemaTongs.GetConnection
        config["Source:Server"] = config[$"{ConfigPrefix}:Server"] ?? "127.0.0.1";
        config["Source:Port"] = config[$"{ConfigPrefix}:Port"];
        config["Source:User"] = config[$"{ConfigPrefix}:User"];
        config["Source:Password"] = config[$"{ConfigPrefix}:Password"];
        config["Source:Database"] = _integrationDb;
        var connProps = ConnectionString.ReadProperties(config, $"{ConfigPrefix}:ConnectionProperties");
        foreach (var prop in connProps)
            config[$"Source:ConnectionProperties:{prop.Key}"] = prop.Value;

        config["ShouldCast:Tables"] = "false";
        config["ShouldCast:Functions"] = "false";
        config["ShouldCast:Views"] = "false";
        config["ShouldCast:Procedures"] = "false";
        config["ShouldCast:TableTriggers"] = "false";
        config["ShouldCast:Events"] = "false";

        FactoryContainer.Register<IConfigurationRoot>(config);
        return config;
    }

    private void CreateTestDatabase()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString + "Database=information_schema;");
        conn.Open();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = $"CREATE DATABASE IF NOT EXISTS `{_integrationDb}`;";
        cmd.ExecuteNonQuery();

        cmd.CommandText = $"USE `{_integrationDb}`;";
        cmd.ExecuteNonQuery();

        ForgeKindler.KindleTheForge(cmd, Platform);

        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS TestTable (
    Column1 INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    Column2 VARCHAR(200) NULL,
    Column3 BIT DEFAULT 0
);";
        cmd.ExecuteNonQuery();

        cmd.CommandText = @"
CREATE INDEX idx_TestTable_Column2 ON TestTable (Column2);";
        cmd.ExecuteNonQuery();

        cmd.CommandText = $@"
DROP VIEW IF EXISTS TestView;
CREATE VIEW TestView AS SELECT * FROM TestTable;";
        cmd.ExecuteNonQuery();

        cmd.CommandText = $@"
DROP FUNCTION IF EXISTS TestFunction;
CREATE FUNCTION TestFunction(param INT) RETURNS INT
DETERMINISTIC
RETURN param;";
        cmd.ExecuteNonQuery();

        cmd.CommandText = $@"
DROP PROCEDURE IF EXISTS TestProcedure;
CREATE PROCEDURE TestProcedure(IN param INT)
BEGIN
    SELECT param;
END;";
        cmd.ExecuteNonQuery();

        cmd.CommandText = $@"
DROP TRIGGER IF EXISTS TestTrigger;
CREATE TRIGGER TestTrigger AFTER INSERT ON TestTable
FOR EACH ROW
BEGIN
    SET @dummy = 1;
END;";
        cmd.ExecuteNonQuery();

        cmd.CommandText = $@"
DROP EVENT IF EXISTS TestEvent;
CREATE EVENT TestEvent
  ON SCHEDULE EVERY 1 DAY
  ON COMPLETION PRESERVE
  DISABLE
  COMMENT 'Test event for integration tests'
  DO BEGIN
    SET @dummy = 1;
  END;";
        cmd.ExecuteNonQuery();

        cmd.CommandText = $@"
DROP EVENT IF EXISTS TestEventEnabled;
CREATE EVENT TestEventEnabled
  ON SCHEDULE EVERY 1 DAY
  ON COMPLETION PRESERVE
  ENABLE
  COMMENT 'Enabled test event for integration tests'
  DO BEGIN
    SET @dummy = 1;
  END;";
        cmd.ExecuteNonQuery();

        cmd.CommandText = $@"
DROP EVENT IF EXISTS TestEventSlaveDisabled;
CREATE EVENT TestEventSlaveDisabled
  ON SCHEDULE EVERY 1 DAY
  ON COMPLETION PRESERVE
  DISABLE ON SLAVE
  COMMENT 'Slave-disabled test event for integration tests'
  DO BEGIN
    SET @dummy = 1;
  END;";
        cmd.ExecuteNonQuery();

        conn.Close();
    }

    private static string GenerateUniqueDBName(string dbName)
    {
        var uniqueSegment = Guid.NewGuid().ToString().Replace("-", "_").Substring(0, 8);
        return $"{dbName}_Test_{DateTime.Now:yyyyMMdd_HHmmss}_{uniqueSegment}";
    }

    private void DropTestDatabase()
    {
        try
        {
            using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString + "Database=information_schema;");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DROP DATABASE IF EXISTS `{_integrationDb}`;";
            cmd.ExecuteNonQuery();
            conn.Close();
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}
