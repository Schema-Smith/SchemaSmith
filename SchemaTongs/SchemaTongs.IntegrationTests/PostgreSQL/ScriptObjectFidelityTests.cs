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

namespace SchemaTongs.IntegrationTests.PostgreSQL;

/// <summary>
/// Triggers, views, aggregates, composite types, collations and publications are written from the engine's own
/// definitions with every clause the catalog fragments lost. Each test extracts from the source database, deploys the
/// written script into an empty target database the way the Objects slot does, and asserts the target's catalog.
/// </summary>
[Category("PostgreSQL")]
[TestFixture]
[Category("Integration")]
public class ScriptObjectFidelityTests
{
    private string _connectionString = "";
    private string _sourceDb = "";
    private string _targetDb = "";
    private int _version;
    private readonly Dictionary<string, string> _scripts = new(StringComparer.OrdinalIgnoreCase);

    private bool AtLeast(int versionNum) => _version >= versionNum;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        _connectionString = ConnectionString.Build(Platform.PostgreSQL, config["PostgreSQL:Server"], "postgres", config["PostgreSQL:User"], config["PostgreSQL:Password"], config["PostgreSQL:Port"],
            ConnectionString.ReadProperties(config, "PostgreSQL:ConnectionProperties"));
        var unique = $"{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}";
        _sourceDb = $"FidelitySrc_{unique}";
        _targetDb = $"FidelityDst_{unique}";
        CreateDatabases();
        ExtractEverything();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        foreach (var db in new[] { _sourceDb, _targetDb })
        {
            // WITH (FORCE) is PostgreSQL 13; ending the sessions first works on every supported version.
            cmd.CommandText = $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{db}' AND pid <> pg_backend_pid()";
            cmd.ExecuteNonQuery();
            cmd.CommandText = $"DROP DATABASE IF EXISTS \"{db}\"";
            cmd.ExecuteNonQuery();
        }
    }

    // ---- triggers --------------------------------------------------------------------------------------------

    [Test]
    public void Trigger_DeploysOnTheFloor_TwiceForAConstraintTrigger_AndKeepsItsEnabledState()
    {
        using var conn = Open(_targetDb);
        using var cmd = conn.CreateCommand();
        foreach (var name in new[] { "tr_plain", "tr_dis", "tr_rep", "tr_alw", "tr_con" })
        {
            var script = Script("Triggers", $"pgp.t.{name}");
            Assert.That(script, Does.Not.Contain("CREATE OR REPLACE TRIGGER"), "CREATE OR REPLACE TRIGGER is 14+ and fails on the 12 floor: " + script);
            Assert.That(script, Does.StartWith($"DROP TRIGGER IF EXISTS {name} ON pgp.t;"), script);
            Deploy(cmd, script);
            Deploy(cmd, script);
        }

        cmd.CommandText = "SELECT STRING_AGG(tgname || '=' || tgenabled::text, ',' ORDER BY tgname) FROM pg_trigger WHERE tgrelid = 'pgp.t'::regclass AND NOT tgisinternal";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("tr_alw=A,tr_con=O,tr_dis=D,tr_plain=O,tr_rep=R"), "a disabled, replica or always trigger must deploy in that state");
        cmd.CommandText = "SELECT COUNT(*) FROM pg_trigger WHERE tgrelid = 'pgp.t'::regclass AND tgname = 'tr_con' AND tgconstraint <> 0";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1), "the constraint trigger must deploy as one");
    }

    // ---- views -----------------------------------------------------------------------------------------------

    [Test]
    public void View_KeepsSecurityBarrierSecurityInvokerAndCheckOption_AcrossARedeploy()
    {
        using var conn = Open(_targetDb);
        using var cmd = conn.CreateCommand();
        var views = new List<string> { "v_sb", "v_co" };
        if (AtLeast(150000)) views.Add("v_si");
        foreach (var name in views)
        {
            var script = Script("Views", $"pgp.{name}");
            Assert.That(script, Does.Contain(" WITH ("), script);
            Deploy(cmd, script);
            Deploy(cmd, script);
        }

        cmd.CommandText = "SELECT c.relname || '=' || ARRAY_TO_STRING(c.reloptions, ';') FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'pgp' AND c.relkind = 'v' ORDER BY 1";
        var options = new List<string>();
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) options.Add(reader.GetString(0));
        var expected = new List<string> { "v_co=check_option=local", "v_sb=security_barrier=true" };
        if (AtLeast(150000)) expected.Add("v_si=security_invoker=true");
        Assert.That(options, Is.EqualTo(expected), "the view options were lost or reset on redeploy");

        cmd.CommandText = "INSERT INTO pgp.v_co (a, b) VALUES (1, -1)";
        Assert.Throws<Npgsql.PostgresException>(() => cmd.ExecuteNonQuery(), "the CHECK OPTION must still reject a row the view cannot see");
    }

    // ---- aggregates ------------------------------------------------------------------------------------------

    [Test]
    public void Aggregate_KeepsArgumentsFinalCombineInitcondAndParallel()
    {
        using var conn = Open(_targetDb);
        using var cmd = conn.CreateCommand();
        var script = Script("Aggregates", "pgp.agg_full");
        Assert.That(script, Does.StartWith("CREATE OR REPLACE AGGREGATE pgp.agg_full(integer) ("), script);
        Assert.That(script, Does.Contain("INITCOND = '7'"), "the initial condition must be quoted: " + script);
        Deploy(cmd, script);
        Deploy(cmd, Script("Aggregates", "pgp.agg_sort"));
        Deploy(cmd, Script("Aggregates", "pgp.agg_ord"));

        cmd.CommandText = "INSERT INTO pgp.t (a, b) VALUES (1, 1), (2, 2)";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "SELECT pgp.agg_full(a) FROM pgp.t";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("10"), "final function (text) and initcond 7 must apply: 7 + 1 + 2");
        cmd.CommandText = "SELECT a.aggcombinefn::regproc::text || '|' || p.proparallel::text || '|' || a.aggsortop::regoperator::text FROM pg_aggregate a JOIN pg_proc p ON p.oid = a.aggfnoid WHERE p.proname = 'agg_full'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("pgp.cf|s|0"), "combine function and PARALLEL SAFE were lost");
        cmd.CommandText = "SELECT a.aggsortop::regoperator::text FROM pg_aggregate a JOIN pg_proc p ON p.oid = a.aggfnoid WHERE p.proname = 'agg_sort'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo(">(integer,integer)"));
        cmd.CommandText = "SELECT a.aggkind FROM pg_aggregate a JOIN pg_proc p ON p.oid = a.aggfnoid WHERE p.proname = 'agg_ord'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("h"), "the hypothetical-set aggregate lost its kind");
    }

    // ---- composite types -------------------------------------------------------------------------------------

    [Test]
    public void CompositeType_InANamedSchema_KeepsQuotedAttributesAndCollation()
    {
        using var conn = Open(_targetDb);
        using var cmd = conn.CreateCommand();
        var script = Script("Composite Types", "pgp.ct");
        Deploy(cmd, script);
        cmd.CommandText = "SELECT STRING_AGG(a.attname || ':' || COALESCE((SELECT collname FROM pg_collation WHERE oid = a.attcollation), '-'), ',' ORDER BY a.attnum) FROM pg_attribute a WHERE a.attrelid = 'pgp.ct'::regclass AND a.attnum > 0";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("Order:-,name:C,plain:default"), "the reserved-word attribute or the COLLATE was lost: " + script);
    }

    // ---- collations ------------------------------------------------------------------------------------------

    [Test]
    public void Collation_RoundTripsEachProviderTheServerHas()
    {
        using var conn = Open(_targetDb);
        using var cmd = conn.CreateCommand();
        var names = new List<string> { "coll_libc", "coll_icu" };
        if (AtLeast(160000)) names.Add("coll_rules");
        if (AtLeast(170000)) names.Add("coll_builtin");
        foreach (var name in names)
        {
            var script = Script("Collations", $"pgp.{name}");
            Assert.That(script, Does.Not.Contain("LOCALE = NULL").And.Not.Contain("= ''"), "an empty locale means the wrong catalog column was read: " + script);
            Deploy(cmd, script);
        }

        cmd.CommandText = "SELECT collname || '=' || collprovider::text || '|' || collisdeterministic::text FROM pg_collation WHERE collnamespace = 'pgp'::regnamespace AND collname = 'coll_icu'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("coll_icu=i|false"), "the ICU collation lost its provider or DETERMINISTIC = false");
        if (AtLeast(160000))
        {
            cmd.CommandText = "SELECT collicurules FROM pg_collation WHERE collnamespace = 'pgp'::regnamespace AND collname = 'coll_rules'";
            Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("&V << w"), "RULES were lost");
        }
        if (AtLeast(170000))
        {
            cmd.CommandText = "SELECT collprovider FROM pg_collation WHERE collnamespace = 'pgp'::regnamespace AND collname = 'coll_builtin'";
            Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("b"), "the builtin provider was lost");
        }
    }

    // ---- publications ----------------------------------------------------------------------------------------

    [Test]
    public void Publication_EmptyOne_DeploysWithoutAForClause()
    {
        using var conn = Open(_targetDb);
        using var cmd = conn.CreateCommand();
        var script = Script("Publications", "pub_empty");
        Assert.That(script, Does.Not.Contain("FOR TABLE "), script);
        Deploy(cmd, script);
        cmd.CommandText = "SELECT pubinsert::text || pubupdate::text FROM pg_publication WHERE pubname = 'pub_empty'";
        Assert.That(cmd.ExecuteScalar()?.ToString().ToLowerInvariant(), Is.EqualTo("truefalse"));
    }

    [Test]
    public void Publication_KeepsColumnListRowFilterSchemaAndOptions()
    {
        if (!AtLeast(150000)) Assert.Ignore("Row filters, column lists and TABLES IN SCHEMA are PostgreSQL 15+.");
        using var conn = Open(_targetDb);
        using var cmd = conn.CreateCommand();
        Deploy(cmd, Script("Publications", "pub_tbl"));
        Deploy(cmd, Script("Publications", "pub_sch"));

        cmd.CommandText = "SELECT ARRAY_TO_STRING(attnames, ',') || '|' || COALESCE(rowfilter, '') FROM pg_publication_tables WHERE pubname = 'pub_tbl'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("a,b|(a > 10)"), "the column list or the row filter was lost");
        cmd.CommandText = "SELECT pubviaroot FROM pg_publication WHERE pubname = 'pub_tbl'";
        Assert.That(cmd.ExecuteScalar()?.ToString().ToLowerInvariant(), Is.EqualTo("true"), "publish_via_partition_root was lost");
        cmd.CommandText = "SELECT COUNT(*) FROM pg_publication_namespace pn JOIN pg_publication p ON p.oid = pn.pnpubid WHERE p.pubname = 'pub_sch'";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1), "TABLES IN SCHEMA was lost");

        if (AtLeast(180000))
        {
            Deploy(cmd, Script("Publications", "pub_gen"));
            cmd.CommandText = "SELECT pubgencols::text FROM pg_publication WHERE pubname = 'pub_gen'";
            Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("s"), "publish_generated_columns was lost");
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
        foreach (var statement in PostgreSqlStatementSplitter.Split(script))
        {
            cmd.CommandText = statement;
            cmd.ExecuteNonQuery();
        }
    }

    private IDbConnection Open(string database)
    {
        var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(database);
        return conn;
    }

    private void ExtractEverything()
    {
        var file = Substitute.For<IFile>();
        file.When(f => f.WriteAllText(Arg.Any<string>(), Arg.Any<string>())).Do(ci =>
        {
            var parts = ci.ArgAt<string>(0).Replace('\\', '/').Split('/');
            if (parts.Length < 2) return;
            var folder = parts[^2];
            if (folder is "Views" or "Triggers" or "Aggregates" or "Composite Types" or "Collations" or "Publications")
                _scripts[$"{folder}/{System.IO.Path.GetFileNameWithoutExtension(parts[^1])}"] = ci.ArgAt<string>(1);
        });
        lock (FactoryContainer.SharedLockObject)
        {
            LogFactory.Register("ErrorLog", Substitute.For<ILog>());
            LogFactory.Register("ProgressLog", Substitute.For<ILog>());
            FactoryContainer.Register(Substitute.For<IEnvironment>());
            FactoryContainer.Register(file);
            FactoryContainer.Register(Substitute.For<IDirectory>());
            var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
            config["Source:Server"] = config["PostgreSQL:Server"] ?? "127.0.0.1";
            config["Source:Port"] = config["PostgreSQL:Port"];
            config["Source:User"] = config["PostgreSQL:User"];
            config["Source:Password"] = config["PostgreSQL:Password"];
            config["Source:database"] = _sourceDb;
            foreach (var prop in ConnectionString.ReadProperties(config, "PostgreSQL:ConnectionProperties"))
                config[$"Source:ConnectionProperties:{prop.Key}"] = prop.Value;
            foreach (var kind in new[] { "Tables", "Schemas", "DomainTypes", "EnumTypes", "Functions", "Procedures", "Sequences", "Rules", "MaterializedViews" })
                config[$"ShouldCast:{kind}"] = "false";
            foreach (var kind in new[] { "Views", "TableTriggers", "Aggregates", "CompositeTypes", "Collations", "Publications" })
                config[$"ShouldCast:{kind}"] = "true";
            FactoryContainer.Register<IConfigurationRoot>(config);
            new SchemaTongs(Platform.PostgreSQL).CastTemplate();
            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    private void CreateDatabases()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT current_setting('server_version_num')::int";
        _version = Convert.ToInt32(cmd.ExecuteScalar());
        foreach (var db in new[] { _sourceDb, _targetDb })
        {
            cmd.CommandText = $"CREATE DATABASE \"{db}\"";
            cmd.ExecuteNonQuery();
        }

        // The target has the schema, the table and the support functions the scripts reference, and nothing else.
        foreach (var db in new[] { _targetDb, _sourceDb })
        {
            conn.ChangeDatabase(db);
            Run(cmd, "CREATE SCHEMA pgp");
            Run(cmd, "CREATE TABLE pgp.t (a int PRIMARY KEY, b int, c text)");
            Run(cmd, "CREATE FUNCTION pgp.tf() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$");
            Run(cmd, "CREATE FUNCTION pgp.sf(int, int) RETURNS int LANGUAGE sql IMMUTABLE AS 'SELECT $1 + $2'");
            Run(cmd, "CREATE FUNCTION pgp.ff(int) RETURNS text LANGUAGE sql IMMUTABLE AS 'SELECT $1::text'");
            Run(cmd, "CREATE FUNCTION pgp.cf(int, int) RETURNS int LANGUAGE sql IMMUTABLE AS 'SELECT $1 + $2'");
        }

        ForgeKindler.KindleTheForge(cmd, Platform.PostgreSQL);
        Run(cmd, "SET search_path = pgp, public");
        Run(cmd, "CREATE TRIGGER tr_plain BEFORE INSERT ON t FOR EACH ROW EXECUTE PROCEDURE tf()");
        Run(cmd, "CREATE TRIGGER tr_dis BEFORE UPDATE ON t FOR EACH ROW EXECUTE PROCEDURE tf()");
        Run(cmd, "ALTER TABLE t DISABLE TRIGGER tr_dis");
        Run(cmd, "CREATE TRIGGER tr_rep AFTER DELETE ON t FOR EACH ROW EXECUTE PROCEDURE tf()");
        Run(cmd, "ALTER TABLE t ENABLE REPLICA TRIGGER tr_rep");
        Run(cmd, "CREATE TRIGGER tr_alw AFTER INSERT ON t FOR EACH ROW EXECUTE PROCEDURE tf()");
        Run(cmd, "ALTER TABLE t ENABLE ALWAYS TRIGGER tr_alw");
        Run(cmd, "CREATE CONSTRAINT TRIGGER tr_con AFTER INSERT ON t DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE PROCEDURE tf()");
        Run(cmd, "CREATE VIEW v_sb WITH (security_barrier = true) AS SELECT a, b FROM t WHERE b > 0");
        Run(cmd, "CREATE VIEW v_co AS SELECT a, b FROM t WHERE b > 0 WITH LOCAL CHECK OPTION");
        if (AtLeast(150000)) Run(cmd, "CREATE VIEW v_si WITH (security_invoker = true) AS SELECT a FROM t");
        Run(cmd, "CREATE AGGREGATE agg_full(int) (SFUNC = sf, STYPE = int, FINALFUNC = ff, COMBINEFUNC = cf, INITCOND = '7', PARALLEL = safe)");
        Run(cmd, "CREATE AGGREGATE agg_sort(int) (SFUNC = sf, STYPE = int, SORTOP = >)");
        Run(cmd, "CREATE AGGREGATE agg_ord(int ORDER BY int) (SFUNC = sf, STYPE = int, HYPOTHETICAL)");
        Run(cmd, "CREATE TYPE ct AS (\"Order\" integer, name text COLLATE \"C\", plain text)");
        Run(cmd, "CREATE COLLATION coll_libc (LC_COLLATE = 'C', LC_CTYPE = 'C')");
        Run(cmd, "CREATE COLLATION coll_icu (PROVIDER = icu, LOCALE = 'und-u-ks-level2', DETERMINISTIC = false)");
        if (AtLeast(160000)) Run(cmd, "CREATE COLLATION coll_rules (PROVIDER = icu, LOCALE = 'und', RULES = '&V << w')");
        if (AtLeast(170000)) Run(cmd, "CREATE COLLATION coll_builtin (PROVIDER = builtin, LOCALE = 'C.UTF-8')");
        Run(cmd, "CREATE PUBLICATION pub_empty WITH (publish = 'insert')");
        if (AtLeast(150000))
        {
            Run(cmd, "CREATE PUBLICATION pub_tbl FOR TABLE t (a, b) WHERE (a > 10) WITH (publish = 'insert, update', publish_via_partition_root = true)");
            Run(cmd, "CREATE PUBLICATION pub_sch FOR TABLES IN SCHEMA pgp");
        }
        if (AtLeast(180000)) Run(cmd, "CREATE PUBLICATION pub_gen FOR TABLE t WITH (publish_generated_columns = stored)");
    }

    private static void Run(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
