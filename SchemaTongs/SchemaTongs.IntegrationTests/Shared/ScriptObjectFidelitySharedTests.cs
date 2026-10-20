// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
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
/// Views, routines, triggers and events are extracted from the engine's own DDL (SHOW CREATE / the EVENTS catalog), so
/// the clauses the INFORMATION_SCHEMA fragments lost come back and the package deploys to a differently named
/// database. Each test extracts from the source database, deploys the written script into an empty target database
/// the way the Objects slot does (DELIMITER-split batches), and asserts the target's catalog or behaviour.
/// </summary>
public abstract class ScriptObjectFidelitySharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string ConfigPrefix { get; }
    protected abstract string FixtureConnectionString { get; }

    private string _connectionString = "";
    private string _sourceDb = "";
    private string _targetDb = "";
    private string _otherDb = "";
    private int _version;
    private readonly Dictionary<string, string> _scripts = new(StringComparer.OrdinalIgnoreCase);
    private string _templateJson;

    private bool IsMariaDb => Platform == Platform.MariaDb;
    private bool AtLeast(int patchComparable) => _version >= patchComparable;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        _connectionString = FixtureConnectionString;
        var unique = $"{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}";
        _sourceDb = $"FidelitySrc_{unique}";
        _targetDb = $"FidelityDst_{unique}";
        _otherDb = $"FidelityOther_{unique}";
        CreateDatabases();
        ExtractEverything();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        using var conn = Open("information_schema");
        using var cmd = conn.CreateCommand();
        foreach (var db in new[] { _sourceDb, _targetDb, _otherDb })
        {
            cmd.CommandText = $"DROP DATABASE IF EXISTS `{db}`";
            cmd.ExecuteNonQuery();
        }
    }

    // ---- views -----------------------------------------------------------------------------------------------

    [Test]
    public void View_IsCreateOrReplace_WithoutADefiner()
    {
        var script = Script("Views", "v_unq");
        Assert.That(script, Does.StartWith("CREATE OR REPLACE "), script);
        Assert.That(script, Does.Not.Contain("DEFINER="), script);
        Assert.That(script, Does.Not.Contain("DROP VIEW"), script);
    }

    // The view read the SOURCE database after a deploy elsewhere, because VIEW_DEFINITION qualifies every reference.
    [Test]
    public void View_ReadsTheTargetDatabasesTable()
    {
        using var conn = Open(_targetDb);
        using var cmd = conn.CreateCommand();
        Run(cmd, "DELETE FROM t");
        Run(cmd, "INSERT INTO t (x, w) VALUES (41, 1), (42, 2)");
        Deploy(cmd, Script("Views", "v_unq"));

        cmd.CommandText = "SELECT SUM(x) FROM v_unq";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(83), "the deployed view must read the target's rows, not the source's");
        Assert.That(Script("Views", "v_unq"), Does.Not.Contain(_sourceDb), "no same-database qualifier may survive extraction");
    }

    [Test]
    public void View_CrossDatabaseReferenceStaysQualified()
    {
        var script = Script("Views", "v_cross");
        Assert.That(script, Does.Contain($"`{_otherDb}`.`o`"), script);
    }

    [Test]
    public void View_KeepsAlgorithmSecurityCheckOptionAndColumnList()
    {
        using var conn = Open(_targetDb);
        using var cmd = conn.CreateCommand();
        Deploy(cmd, Script("Views", "v_opts"));

        cmd.CommandText = $"SELECT CHECK_OPTION, SECURITY_TYPE FROM INFORMATION_SCHEMA.VIEWS WHERE TABLE_SCHEMA = '{_targetDb}' AND TABLE_NAME = 'v_opts'";
        using (var reader = cmd.ExecuteReader())
        {
            Assert.That(reader.Read(), Is.True, "v_opts was not deployed");
            Assert.That(reader.GetString(0), Is.EqualTo("CASCADED"), "WITH CHECK OPTION was lost");
            Assert.That(reader.GetString(1), Is.EqualTo("INVOKER"), "SQL SECURITY INVOKER was lost");
        }
        cmd.CommandText = $"SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = '{_targetDb}' AND TABLE_NAME = 'v_opts'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("a"), "the view's column list was lost");
        Assert.That(Script("Views", "v_opts"), Does.Contain("ALGORITHM=MERGE"));
    }

    [Test]
    public void View_Duality_RoundTrips()
    {
        if (IsMariaDb || !AtLeast(90400)) Assert.Ignore("JSON duality views arrived in MySQL 9.4.");
        using var conn = Open(_targetDb);
        using var cmd = conn.CreateCommand();
        var script = Script("Views", "dv");
        Assert.That(script, Does.Contain("JSON RELATIONAL DUALITY VIEW"), script);
        Deploy(cmd, script);
        cmd.CommandText = $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.JSON_DUALITY_VIEWS WHERE TABLE_SCHEMA = '{_targetDb}' AND TABLE_NAME = 'dv'";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1), "the duality view did not deploy as one");
    }

    // ---- routines --------------------------------------------------------------------------------------------

    [Test]
    public void Procedure_KeepsModesCharsetReservedWordNameCommentAndSecurity()
    {
        using var conn = Open(_targetDb);
        using var cmd = conn.CreateCommand();
        var script = Script("Procedures", "p_full");
        Assert.That(script, Does.Not.Contain("DEFINER="), script);
        Deploy(cmd, script);

        cmd.CommandText = $"SELECT PARAMETER_NAME, PARAMETER_MODE, CHARACTER_SET_NAME FROM INFORMATION_SCHEMA.PARAMETERS WHERE SPECIFIC_SCHEMA = '{_targetDb}' AND SPECIFIC_NAME = 'p_full' ORDER BY ORDINAL_POSITION";
        var parameters = new List<(string Name, string Mode, string Charset)>();
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                parameters.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        Assert.That(parameters.Select(p => (p.Name, p.Mode)), Is.EqualTo(new[] { ("order", "IN"), ("b", "OUT"), ("c", "INOUT") }), "parameter modes or names were lost");
        Assert.That(parameters[1].Charset, Is.EqualTo("latin1"), "the parameter's CHARACTER SET was lost");

        cmd.CommandText = $"SELECT ROUTINE_COMMENT, SECURITY_TYPE, SQL_DATA_ACCESS FROM INFORMATION_SCHEMA.ROUTINES WHERE ROUTINE_SCHEMA = '{_targetDb}' AND ROUTINE_NAME = 'p_full'";
        using (var reader = cmd.ExecuteReader())
        {
            Assert.That(reader.Read(), Is.True);
            Assert.That(reader.GetString(0), Is.EqualTo("hello"), "COMMENT was lost");
            Assert.That(reader.GetString(1), Is.EqualTo("INVOKER"), "SQL SECURITY INVOKER was lost");
            Assert.That(reader.GetString(2), Is.EqualTo("READS SQL DATA"));
        }

        cmd.CommandText = "INSERT INTO t (x, w) VALUES (7, 0)";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "CALL p_full(1, @b, @c)";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "SELECT @b";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("x"), "the OUT parameter did not come back: it was redeployed as IN");
    }

    // SHOW CREATE renders a routine in its own sql_mode (double-quoted identifiers under ANSI_QUOTES), so the script
    // has to set that mode before the CREATE, and the deployed routine must keep it: under the server's default
    // mode `||` is OR, and the function would return 0 instead of 'ax'.
    [Test]
    public void Function_KeepsItsOwnSqlMode()
    {
        using var conn = Open(_targetDb);
        using var cmd = conn.CreateCommand();
        var script = Script("Functions", "f_mode");
        Assert.That(script, Does.Contain("SET SESSION sql_mode = '"), script);
        Assert.That(script, Does.Contain("ANSI_QUOTES"), script);
        Assert.That(script, Does.Contain("SET SESSION sql_mode = @ss_saved_sql_mode"), script);
        Deploy(cmd, script);

        cmd.CommandText = "SELECT @@SESSION.sql_mode";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Does.Not.Contain("ANSI_QUOTES"), "the script must restore the session's mode after the CREATE");
        cmd.CommandText = "SELECT f_mode('a')";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("ax"), "the function lost PIPES_AS_CONCAT");
        cmd.CommandText = $"SELECT SQL_MODE FROM INFORMATION_SCHEMA.ROUTINES WHERE ROUTINE_SCHEMA = '{_targetDb}' AND ROUTINE_NAME = 'f_mode'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Does.Contain("ANSI_QUOTES").And.Contains("PIPES_AS_CONCAT"));
    }

    [Test]
    public void Function_KeepsReturnsCharsetAndDeterministic()
    {
        using var conn = Open(_targetDb);
        using var cmd = conn.CreateCommand();
        Deploy(cmd, Script("Functions", "f_full"));
        cmd.CommandText = $"SELECT CHARACTER_SET_NAME, IS_DETERMINISTIC, ROUTINE_COMMENT FROM INFORMATION_SCHEMA.ROUTINES WHERE ROUTINE_SCHEMA = '{_targetDb}' AND ROUTINE_NAME = 'f_full'";
        using var reader = cmd.ExecuteReader();
        Assert.That(reader.Read(), Is.True);
        Assert.That(reader.GetString(0), Is.EqualTo("latin1"), "the RETURNS charset was lost");
        Assert.That(reader.GetString(1), Is.EqualTo("YES"));
        Assert.That(reader.GetString(2), Is.EqualTo("fn"));
    }

    [Test]
    public void Function_Aggregate_RoundTrips()
    {
        if (!IsMariaDb || !AtLeast(100300)) Assert.Ignore("Aggregate functions are MariaDB 10.3+.");
        using var conn = Open(_targetDb);
        using var cmd = conn.CreateCommand();
        Deploy(cmd, Script("Functions", "f_agg"));
        Run(cmd, "DELETE FROM t");
        Run(cmd, "INSERT INTO t (x, w) VALUES (1, 0), (2, 0), (3, 0)");
        cmd.CommandText = "SELECT f_agg(x) FROM t";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(6), "the aggregate was redeployed as a plain function");
    }

    [Test]
    public void Function_OutParameter_RoundTrips()
    {
        if (!IsMariaDb || !AtLeast(100800)) Assert.Ignore("OUT parameters on functions are MariaDB 10.8+.");
        using var conn = Open(_targetDb);
        using var cmd = conn.CreateCommand();
        Deploy(cmd, Script("Functions", "f_out"));
        // A function with an OUT parameter may only be called from SET, not SELECT (MariaDB 4187).
        Run(cmd, "SET @r = f_out(21, @o)");
        cmd.CommandText = "SELECT @o";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(42), "the OUT parameter was redeployed as IN");
    }

    [Test]
    public void Procedure_ParameterDefault_RoundTrips()
    {
        if (!IsMariaDb || !AtLeast(110800)) Assert.Ignore("Parameter defaults are MariaDB 11.8+.");
        using var conn = Open(_targetDb);
        using var cmd = conn.CreateCommand();
        Deploy(cmd, Script("Procedures", "p_def"));
        cmd.CommandText = "CALL p_def()";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(5), "the parameter DEFAULT was lost");
    }

    [Test]
    public void Function_ReturnsRow_RoundTrips()
    {
        if (!IsMariaDb || !AtLeast(110700)) Assert.Ignore("RETURNS ROW is MariaDB 11.7+.");
        using var conn = Open(_targetDb);
        using var cmd = conn.CreateCommand();
        Deploy(cmd, Script("Functions", "f_row"));
        cmd.CommandText = $"SELECT DTD_IDENTIFIER FROM INFORMATION_SCHEMA.ROUTINES WHERE ROUTINE_SCHEMA = '{_targetDb}' AND ROUTINE_NAME = 'f_row'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("ROW"));
    }

    [Test]
    public void Function_OracleMode_RoundTrips()
    {
        if (!IsMariaDb || !AtLeast(100300)) Assert.Ignore("Oracle-mode routines are MariaDB 10.3+.");
        using var conn = Open(_targetDb);
        using var cmd = conn.CreateCommand();
        Deploy(cmd, Script("Functions", "f_ora"));
        cmd.CommandText = "SELECT f_ora(1)";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(2));
        cmd.CommandText = $"SELECT SQL_MODE FROM INFORMATION_SCHEMA.ROUTINES WHERE ROUTINE_SCHEMA = '{_targetDb}' AND ROUTINE_NAME = 'f_ora'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Does.Contain("ORACLE"), "the routine lost its Oracle mode");
    }

    // ---- packages (MariaDB) -----------------------------------------------------------------------------------

    // A package and its body are one file, each half under the sql_mode it was created in, so a redeploy replaces both.
    [Test]
    public void Package_OracleMode_RoundTrips_SpecAndBody_AndRedeploys()
    {
        if (!IsMariaDb || !AtLeast(100300)) Assert.Ignore("Packages are MariaDB 10.3+.");
        var script = Script("Packages", "pkg_ora");
        Assert.That(script, Does.Contain("DROP PACKAGE IF EXISTS `pkg_ora`;").And.Not.Contain("DEFINER="), script);
        using var conn = Open(_targetDb);
        using var cmd = conn.CreateCommand();
        Deploy(cmd, script);
        Deploy(cmd, script);
        // Before 11.4 a package member is called from an Oracle-mode session only.
        Run(cmd, "SET SESSION sql_mode = 'ORACLE'");
        cmd.CommandText = "SELECT pkg_ora.next_id() + pkg_ora.next_id()";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(3), "the body's state and both members came back");
        cmd.CommandText = $"SELECT GROUP_CONCAT(ROUTINE_TYPE, ':', LOCATE('ORACLE', SQL_MODE) > 0 ORDER BY ROUTINE_TYPE) FROM INFORMATION_SCHEMA.ROUTINES WHERE ROUTINE_SCHEMA = '{_targetDb}' AND ROUTINE_NAME = 'pkg_ora'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("PACKAGE:1,PACKAGE BODY:1"), "both halves, each in Oracle mode");

        // The orphan cleanup line drops it from a session in the default mode.
        Run(cmd, "SET SESSION sql_mode = DEFAULT");
        Run(cmd, CleanupScriptGenerator.GenerateDropStatement("pkg_ora.sql", ScriptObjectType.Packages, Platform.MariaDb));
        cmd.CommandText = $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.ROUTINES WHERE ROUTINE_SCHEMA = '{_targetDb}' AND ROUTINE_NAME = 'pkg_ora'";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.Zero, "the cleanup line dropped the package and its body");
    }

    [Test]
    public void Packages_AreDeclaredInTheTemplate_OnlyOnMariaDb()
    {
        if (IsMariaDb && AtLeast(100300))
            Assert.That(_templateJson, Does.Contain("\"ObjectType\": \"Packages\""), _templateJson);
        else
            Assert.That(_templateJson ?? "", Does.Not.Contain("Packages"), "MySQL has no packages");
    }

    [Test]
    public void Package_DefaultMode_AndSpecOnly_RoundTrip()
    {
        if (!IsMariaDb || !AtLeast(110400)) Assert.Ignore("A package outside Oracle mode is MariaDB 11.4+.");
        using var conn = Open(_targetDb);
        using var cmd = conn.CreateCommand();
        Deploy(cmd, Script("Packages", "pkg_def"));
        Deploy(cmd, Script("Packages", "pkg_spec"));
        cmd.CommandText = "SELECT pkg_def.seven()";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(7));
        cmd.CommandText = $"SELECT GROUP_CONCAT(ROUTINE_NAME, ':', ROUTINE_TYPE, ':', LOCATE('ORACLE', SQL_MODE) > 0 ORDER BY ROUTINE_NAME, ROUTINE_TYPE) FROM INFORMATION_SCHEMA.ROUTINES WHERE ROUTINE_SCHEMA = '{_targetDb}' AND ROUTINE_NAME IN ('pkg_def', 'pkg_spec')";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("pkg_def:PACKAGE:0,pkg_def:PACKAGE BODY:0,pkg_spec:PACKAGE:0"), "a package with no body is written without one");
    }

    // ---- triggers --------------------------------------------------------------------------------------------

    [Test]
    public void Trigger_BareStatementStaysBare_AndCarriesNoDefiner()
    {
        var script = Script("Triggers", "tr1");
        Assert.That(script, Does.Not.Contain("DEFINER="), script);
        Assert.That(script, Does.Not.Contain("BEGIN"), "a single-statement body was wrapped in BEGIN ... END: " + script);
        Assert.That(script, Does.Contain("DROP TRIGGER IF EXISTS `tr1`"), script);
    }

    // FOLLOWS is not in any SHOW CREATE output; it is rebuilt from ACTION_ORDER. Deployed in reverse file order, the
    // follower fails until its predecessor exists, which is what the Objects slot's retry loop handles.
    [Test]
    public void Trigger_FollowsIsRebuilt_AndOrderSurvivesADeploy()
    {
        using var conn = Open(_targetDb);
        using var cmd = conn.CreateCommand();
        var tr2 = Script("Triggers", "tr2");
        Assert.That(tr2, Does.Contain("FOLLOWS `tr1`"), tr2);
        Assert.That(Script("Triggers", "tr1"), Does.Not.Contain("FOLLOWS"));

        MySqlConnector.MySqlException first = null;
        try { Deploy(cmd, tr2); } catch (MySqlConnector.MySqlException ex) { first = ex; }
        Assert.That(first, Is.Not.Null, "the follower cannot exist before its predecessor");
        // MariaDB reports this as error 4031, the number MySqlConnector reserves for "disconnected by the server", so
        // the driver marks the session broken although the server kept it. The deploy's script runner reopens a
        // connection that a failed script left in that state before its retry pass; this mirrors it.
        if (conn.State != ConnectionState.Open) { conn.Close(); conn.Open(); }
        try
        {
            Deploy(cmd, Script("Triggers", "tr1"));
            Deploy(cmd, tr2);

            cmd.CommandText = $"SELECT TRIGGER_NAME, ACTION_ORDER FROM INFORMATION_SCHEMA.TRIGGERS WHERE TRIGGER_SCHEMA = '{_targetDb}' AND EVENT_OBJECT_TABLE = 't' AND ACTION_TIMING = 'BEFORE' AND EVENT_MANIPULATION = 'INSERT' ORDER BY ACTION_ORDER";
            var order = new List<string>();
            using (var reader = cmd.ExecuteReader())
                while (reader.Read()) order.Add($"{reader.GetString(0)}={reader.GetValue(1)}");
            Assert.That(order, Is.EqualTo(new[] { "tr1=1", "tr2=2" }));
        }
        finally
        {
            Run(cmd, "DROP TRIGGER IF EXISTS tr2");
            Run(cmd, "DROP TRIGGER IF EXISTS tr1");
        }
    }

    [Test]
    public void Trigger_MultiEvent_RoundTrips()
    {
        if (!IsMariaDb || !AtLeast(120000)) Assert.Ignore("Multi-event triggers are MariaDB 12.0+.");
        using var conn = Open(_targetDb);
        using var cmd = conn.CreateCommand();
        var script = Script("Triggers", "tr_multi");
        Assert.That(script, Does.Contain("INSERT OR UPDATE"), script);
        try
        {
            Deploy(cmd, script);
            cmd.CommandText = $"SELECT EVENT_MANIPULATION FROM INFORMATION_SCHEMA.TRIGGERS WHERE TRIGGER_SCHEMA = '{_targetDb}' AND TRIGGER_NAME = 'tr_multi'";
            Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("INSERT,UPDATE"));
        }
        finally
        {
            Run(cmd, "DROP TRIGGER IF EXISTS tr_multi");
        }
    }

    [Test]
    public void Trigger_UpdateOf_RoundTrips()
    {
        if (!IsMariaDb || !AtLeast(110800)) Assert.Ignore("UPDATE OF triggers are MariaDB 11.8+.");
        using var conn = Open(_targetDb);
        using var cmd = conn.CreateCommand();
        var script = Script("Triggers", "tr_of");
        Assert.That(script, Does.Contain("UPDATE OF w"), script);
        try
        {
            Deploy(cmd, script);
            Run(cmd, "DELETE FROM t");
            Run(cmd, "INSERT INTO t (x, w) VALUES (10, 0)");
            Run(cmd, "UPDATE t SET x = 11 WHERE x = 10");
            cmd.CommandText = "SELECT x FROM t WHERE w = 0";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(11), "the trigger fired on an update that did not touch w");
            Run(cmd, "UPDATE t SET w = 1 WHERE x = 11");
            cmd.CommandText = "SELECT x FROM t WHERE w = 1";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(3), "the UPDATE OF trigger did not fire");
        }
        finally
        {
            Run(cmd, "DROP TRIGGER IF EXISTS tr_of");
        }
    }

    // ---- events ----------------------------------------------------------------------------------------------

    [Test]
    public void Event_CarriesTimeZoneSqlModeAndAnExplicitStarts()
    {
        var json = Script("Events", "e_tz");
        Assert.That(json, Does.Contain("\"TimeZone\": \"+05:00\""), json);
        Assert.That(json, Does.Contain("\"Starts\": \"2030-01-01 00:00:00\""), json);
        Assert.That(json, Does.Contain("\"Ends\": \"2031-01-01 00:00:00\""), json);
        Assert.That(json, Does.Contain("\"SqlMode\": \""), json);
        Assert.That(json, Does.Not.Contain("DEFINER"), json);
    }

    // A STARTS the author never wrote is materialised to the creation second; pinning it would walk the schedule
    // forward on every re-extract, so it is the one clause deliberately left out.
    [Test]
    public void Event_ImplicitStartsIsNotEmitted_AndSystemZoneIsNotEmitted()
    {
        var json = Script("Events", "e_implicit");
        Assert.That(json, Does.Contain("\"Name\": \"e_implicit\""), json);
        Assert.That(json, Does.Not.Contain("\"Starts\""), json);
        Assert.That(json, Does.Not.Contain("\"TimeZone\""), json);
    }

    // ---- privileges ------------------------------------------------------------------------------------------

    // An account that cannot see a definition gets a warning and a skipped object, not a 0-byte script and not a
    // failed run: SHOW CREATE PROCEDURE / FUNCTION returns a NULL body without global SELECT (or SHOW_ROUTINE) and
    // SHOW CREATE VIEW / TRIGGER refuse without SHOW VIEW / TRIGGER.
    [Test]
    public void Extraction_AsAnAccountThatCannotSeeDefinitions_WarnsAndSkipsInsteadOfFailing()
    {
        var user = $"fidelity_limited_{Guid.NewGuid().ToString("N")[..8]}";
        const string password = "Limited_Pa55word";
        using var admin = Open("information_schema");
        using var cmd = admin.CreateCommand();
        Run(cmd, $"CREATE USER '{user}'@'%' IDENTIFIED BY '{password}'");
        try
        {
            Run(cmd, $"GRANT SELECT, EXECUTE ON `{_sourceDb}`.* TO '{user}'@'%'");

            var warnings = new List<string>();
            var progressLog = Substitute.For<ILog>();
            progressLog.When(l => l.Warn(Arg.Any<object>())).Do(ci => warnings.Add(ci.ArgAt<object>(0)?.ToString()));
            var written = new List<string>();
            var file = Substitute.For<IFile>();
            file.When(f => f.WriteAllText(Arg.Any<string>(), Arg.Any<string>())).Do(ci => written.Add(ci.ArgAt<string>(0).Replace('\\', '/')));
            lock (FactoryContainer.SharedLockObject)
            {
                LogFactory.Register("ErrorLog", Substitute.For<ILog>());
                LogFactory.Register("ProgressLog", progressLog);
                FactoryContainer.Register(Substitute.For<IEnvironment>());
                FactoryContainer.Register(file);
                FactoryContainer.Register(Substitute.For<IDirectory>());
                var config = SourceConfig();
                config["Source:User"] = user;
                config["Source:Password"] = password;
                config["ShouldCast:Functions"] = "true";
                config["ShouldCast:Views"] = "true";
                config["ShouldCast:Procedures"] = "true";
                config["ShouldCast:TableTriggers"] = "true";
                FactoryContainer.Register<IConfigurationRoot>(config);
                Assert.DoesNotThrow(() => new SchemaTongs(Platform).CastTemplate(), "a definition the account cannot see is skipped, not fatal");
                FactoryContainer.Clear();
                LogFactory.Clear();
            }

            Assert.That(written.Where(w => w.Contains("/Procedures/") || w.Contains("/Functions/") || w.Contains("/Views/") || w.Contains("/Triggers/")), Is.Empty,
                "nothing may be written for a definition the account cannot see: " + string.Join(", ", written));
            foreach (var name in new[] { "p_full", "f_full", "v_unq" })
                Assert.That(warnings.Any(w => w.Contains(name)), Is.True, $"{name} must be reported as skipped; warnings: {string.Join(" | ", warnings)}");
            // Without the TRIGGER privilege the catalog lists no triggers at all, so the gap is reported once.
            Assert.That(warnings.Any(w => w.Contains("TRIGGER privilege")), Is.True, $"the invisible triggers must be reported; warnings: {string.Join(" | ", warnings)}");
        }
        finally
        {
            Run(cmd, $"DROP USER IF EXISTS '{user}'@'%'");
        }
    }

    // ---- fixture ---------------------------------------------------------------------------------------------

    private string Script(string folder, string name)
    {
        Assert.That(_scripts.TryGetValue($"{folder}/{name}", out var script), Is.True, $"{folder}/{name} was not extracted; extracted: {string.Join(", ", _scripts.Keys)}");
        return script;
    }

    private static void Deploy(IDbCommand cmd, string script)
    {
        foreach (var batch in BatchSplitter.Split(script))
        {
            cmd.CommandText = batch;
            cmd.ExecuteNonQuery();
        }
    }

    private IDbConnection Open(string database)
    {
        var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString + $"Database={database};");
        conn.Open();
        return conn;
    }

    private void ExtractEverything()
    {
        var file = Substitute.For<IFile>();
        // An existing Template.json, so a folder written on use is declared in it.
        file.Exists(Arg.Is<string>(p => p.EndsWith("Template.json"))).Returns(true);
        file.ReadAllText(Arg.Is<string>(p => p.EndsWith("Template.json"))).Returns(_ => _templateJson ?? "{ \"Name\": \"Main\" }");
        file.When(f => f.WriteAllText(Arg.Any<string>(), Arg.Any<string>())).Do(ci =>
        {
            var path = ci.ArgAt<string>(0).Replace('\\', '/');
            if (path.EndsWith("Template.json")) _templateJson = ci.ArgAt<string>(1);
            var parts = path.Split('/');
            if (parts.Length < 2) return;
            var folder = parts[^2];
            if (folder is "Views" or "Functions" or "Procedures" or "Triggers" or "Events" or "Packages")
                _scripts[$"{folder}/{System.IO.Path.GetFileNameWithoutExtension(parts[^1])}"] = ci.ArgAt<string>(1);
        });
        lock (FactoryContainer.SharedLockObject)
        {
            LogFactory.Register("ErrorLog", Substitute.For<ILog>());
            LogFactory.Register("ProgressLog", Substitute.For<ILog>());
            FactoryContainer.Register(Substitute.For<IEnvironment>());
            FactoryContainer.Register(file);
            FactoryContainer.Register(Substitute.For<IDirectory>());
            var config = SourceConfig();
            config["ShouldCast:Functions"] = "true";
            config["ShouldCast:Views"] = "true";
            config["ShouldCast:Procedures"] = "true";
            config["ShouldCast:TableTriggers"] = "true";
            config["ShouldCast:Events"] = "true";
            config["ShouldCast:Packages"] = "true";
            FactoryContainer.Register<IConfigurationRoot>(config);
            new SchemaTongs(Platform).CastTemplate();
            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    private IConfigurationRoot SourceConfig()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        config["Source:Server"] = config[$"{ConfigPrefix}:Server"] ?? "127.0.0.1";
        config["Source:Port"] = config[$"{ConfigPrefix}:Port"];
        config["Source:User"] = config[$"{ConfigPrefix}:User"];
        config["Source:Password"] = config[$"{ConfigPrefix}:Password"];
        config["Source:Database"] = _sourceDb;
        foreach (var prop in ConnectionString.ReadProperties(config, $"{ConfigPrefix}:ConnectionProperties"))
            config[$"Source:ConnectionProperties:{prop.Key}"] = prop.Value;
        foreach (var kind in new[] { "Tables", "Functions", "Views", "Procedures", "TableTriggers", "Events" })
            config[$"ShouldCast:{kind}"] = "false";
        return config;
    }

    private void CreateDatabases()
    {
        using var conn = Open("information_schema");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT VERSION()";
        _version = VersionHelper.ParsePatchComparable(cmd.ExecuteScalar()?.ToString(), Platform) ?? 0;

        foreach (var db in new[] { _sourceDb, _targetDb, _otherDb })
        {
            cmd.CommandText = $"CREATE DATABASE `{db}`";
            cmd.ExecuteNonQuery();
        }
        cmd.CommandText = $"CREATE TABLE `{_otherDb}`.o (y INT)";
        cmd.ExecuteNonQuery();
        foreach (var db in new[] { _sourceDb, _targetDb })
        {
            cmd.CommandText = $"CREATE TABLE `{db}`.t (x INT PRIMARY KEY, w INT, `order` INT)";
            cmd.ExecuteNonQuery();
        }

        cmd.CommandText = $"USE `{_sourceDb}`";
        cmd.ExecuteNonQuery();
        ForgeKindler.KindleTheForge(cmd, Platform);

        Run(cmd, "CREATE VIEW v_unq AS SELECT x FROM t");
        Run(cmd, $"CREATE VIEW v_cross AS SELECT y FROM `{_otherDb}`.o");
        Run(cmd, "CREATE ALGORITHM=MERGE SQL SECURITY INVOKER VIEW v_opts (a) AS SELECT x FROM t WHERE x > 0 WITH CHECK OPTION");
        if (!IsMariaDb && AtLeast(90400))
            Run(cmd, "CREATE JSON DUALITY VIEW dv AS SELECT JSON_DUALITY_OBJECT('_id': x, 'w': w) FROM t");

        Run(cmd, @"CREATE PROCEDURE p_full (IN `order` INT, OUT b VARCHAR(20) CHARACTER SET latin1, INOUT c INT)
  COMMENT 'hello' SQL SECURITY INVOKER NOT DETERMINISTIC READS SQL DATA
BEGIN
  SELECT x INTO c FROM t LIMIT 1;
  SET b = 'x';
END");
        Run(cmd, "CREATE FUNCTION f_full (p VARCHAR(20) CHARACTER SET latin1) RETURNS VARCHAR(20) CHARACTER SET latin1 DETERMINISTIC COMMENT 'fn' RETURN p");
        Run(cmd, "SET SESSION sql_mode = 'ANSI_QUOTES,PIPES_AS_CONCAT,STRICT_TRANS_TABLES'");
        Run(cmd, "CREATE FUNCTION f_mode (a VARCHAR(10)) RETURNS VARCHAR(20) DETERMINISTIC RETURN a || 'x'");
        Run(cmd, "SET SESSION sql_mode = DEFAULT");
        if (IsMariaDb && AtLeast(100300))
        {
            Run(cmd, @"CREATE AGGREGATE FUNCTION f_agg (x INT) RETURNS INT
BEGIN
  DECLARE s INT DEFAULT 0;
  DECLARE CONTINUE HANDLER FOR NOT FOUND RETURN s;
  LOOP FETCH GROUP NEXT ROW; SET s = s + x; END LOOP;
END");
            Run(cmd, "SET SESSION sql_mode = 'ORACLE'");
            Run(cmd, "CREATE FUNCTION f_ora (a INT) RETURN INT AS BEGIN RETURN a + 1; END");
            Run(cmd, "CREATE PACKAGE pkg_ora AS FUNCTION next_id RETURN INT; END");
            Run(cmd, "CREATE PACKAGE BODY pkg_ora AS current_id INT := 0; FUNCTION next_id RETURN INT AS BEGIN current_id := current_id + 1; RETURN current_id; END; END");
            Run(cmd, "SET SESSION sql_mode = DEFAULT");
        }
        if (IsMariaDb && AtLeast(110400))
        {
            Run(cmd, "CREATE PACKAGE pkg_def FUNCTION seven() RETURNS INT; END");
            Run(cmd, "CREATE PACKAGE BODY pkg_def FUNCTION seven() RETURNS INT BEGIN RETURN 7; END; END");
            Run(cmd, "CREATE PACKAGE pkg_spec FUNCTION unused() RETURNS INT; END");
        }
        // tr1 and tr2 come first: the order of BEFORE INSERT triggers is creation order, and a multi-event trigger made
        // ahead of them (12.0+) would be tr1's predecessor, so tr1 would rightly be extracted FOLLOWS it.
        Run(cmd, "CREATE TRIGGER tr1 BEFORE INSERT ON t FOR EACH ROW SET NEW.x = COALESCE(NEW.x, 0)");
        Run(cmd, "CREATE TRIGGER tr2 BEFORE INSERT ON t FOR EACH ROW FOLLOWS tr1 SET NEW.w = 1");
        if (IsMariaDb && AtLeast(100800))
            Run(cmd, "CREATE FUNCTION f_out (IN a INT, OUT b INT) RETURNS INT DETERMINISTIC BEGIN SET b = a * 2; RETURN a; END");
        if (IsMariaDb && AtLeast(110700))
            Run(cmd, "CREATE FUNCTION f_row (a INT) RETURNS ROW(a INT, b INT) DETERMINISTIC RETURN ROW(1, 2)");
        if (IsMariaDb && AtLeast(110800))
        {
            Run(cmd, "CREATE PROCEDURE p_def (IN a INT DEFAULT 5) SELECT a");
            Run(cmd, "CREATE TRIGGER tr_of BEFORE UPDATE OF w ON t FOR EACH ROW SET NEW.x = 3");
        }
        if (IsMariaDb && AtLeast(120000))
            Run(cmd, "CREATE TRIGGER tr_multi BEFORE INSERT OR UPDATE ON t FOR EACH ROW SET NEW.w = 2");

        Run(cmd, "SET SESSION time_zone = '+05:00'");
        Run(cmd, "CREATE EVENT e_tz ON SCHEDULE EVERY 1 DAY STARTS '2030-01-01 00:00:00' ENDS '2031-01-01 00:00:00' ON COMPLETION PRESERVE DISABLE DO SELECT 2");
        Run(cmd, "SET SESSION time_zone = 'SYSTEM'");
        Run(cmd, "CREATE EVENT e_implicit ON SCHEDULE EVERY 1 HOUR ON COMPLETION PRESERVE DISABLE DO SELECT 3");
    }

    private static void Run(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
