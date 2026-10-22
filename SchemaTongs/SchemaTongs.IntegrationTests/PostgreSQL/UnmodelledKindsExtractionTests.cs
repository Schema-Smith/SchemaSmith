// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using log4net;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using Npgsql;
using NSubstitute;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaTongs.IntegrationTests.PostgreSQL;

/// <summary>
/// Range types, text search configurations, operators, casts and event triggers were not extracted at all, so a package
/// silently lost them. Each is scripted to its own folder from the engine's catalog, and the scripts rebuild the source
/// on an empty database and can be re-run. What the engine generates for a range type (its constructor functions and
/// multirange casts) is not extracted, and an inheritance child or typed table is refused by name (PG-008).
/// </summary>
[Category("PostgreSQL")]
[TestFixture]
[Category("Integration")]
[NonParallelizable]
public class UnmodelledKindsExtractionTests
{
    private const string TemplateName = "Main";
    private const string ProductName = "KindsProduct";

    private string _connectionString = "";
    private string _sourceDb = "";
    private string _targetDb = "";
    private string _productPath = "";
    private readonly List<string> _errors = [];
    private readonly List<string> _warnings = [];
    private int _extractExitCode;
    private int _major;

    private string TemplatePath => Path.Join(_productPath, "Templates", TemplateName);

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        _connectionString = ConnectionString.Build(Platform.PostgreSQL, config["PostgreSQL:Server"], "postgres", config["PostgreSQL:User"], config["PostgreSQL:Password"], config["PostgreSQL:Port"],
            ConnectionString.ReadProperties(config, "PostgreSQL:ConnectionProperties"));
        var unique = $"{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}";
        _sourceDb = $"KindsSrc_{unique}";
        _targetDb = $"KindsDst_{unique}";
        _productPath = Path.Join(Path.GetTempPath(), $"TongsKinds_{unique}");
        Directory.CreateDirectory(_productPath);
        CreateSource();
        Extract();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        using (var conn = Open("postgres"))
        using (var cmd = conn.CreateCommand())
        {
            foreach (var db in new[] { _sourceDb, _targetDb })
            {
                // WITH (FORCE) is PostgreSQL 13; ending the sessions first works on every supported version.
                cmd.CommandText = $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{db}' AND pid <> pg_backend_pid()";
                cmd.ExecuteNonQuery();
                cmd.CommandText = $"DROP DATABASE IF EXISTS \"{db}\"";
                cmd.ExecuteNonQuery();
            }
        }
        try { Directory.Delete(_productPath, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private List<string> Written(string folder)
    {
        var path = Path.Join(TemplatePath, folder);
        return Directory.Exists(path) ? Directory.GetFiles(path, "*.sql").Select(f => Path.GetFileName(f)!).OrderBy(f => f, StringComparer.Ordinal).ToList() : [];
    }

    [Test]
    public void EachKind_IsScriptedToItsOwnFolder()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Written("Range Types"), Is.EqualTo(_major >= 14
                ? new[] { "app.floatrange.sql", "app.span.sql", "app.span8.sql", "app.textrange.sql" }
                : new[] { "app.floatrange.sql", "app.span.sql", "app.textrange.sql" }));
            Assert.That(Written("Text Search Configurations"), Is.EqualTo(new[] { "app.docs.sql" }));
            Assert.That(Written("Operators"), Is.EquivalentTo(new[] { "app.!==(app.cents, app.cents).sql", "app.===(app.cents, app.cents).sql", "app.~~~(NONE, app.cents).sql" }));
            Assert.That(Written("Casts"), Is.EquivalentTo(new[] { "app.cents AS integer.sql", "app.cents AS text.sql" }),
                "the multirange casts a range type brings are the engine's");
            Assert.That(Written("Event Triggers"), Is.EquivalentTo(new[] { "audit_all.sql", "audit_ddl.sql" }));
        });
    }

    [Test]
    public void ARangeTypesConstructorFunctions_AreNotExtracted()
    {
        var functions = Written("Functions");
        Assert.That(functions.Where(f => f.Contains("range") || f.StartsWith("app.span") || f.StartsWith("app.spans")), Is.Empty,
            "a range type's constructors are LANGUAGE internal and created with the type: " + string.Join(", ", functions));
        Assert.That(functions, Does.Contain("app.float_diff.sql"), "the user's own functions are still extracted");
    }

    [Test]
    public void TheTemplateDeclaresEachFolderItUsed()
    {
        var template = JObject.Parse(File.ReadAllText(Path.Join(TemplatePath, "Template.json")));
        var declared = template["ScriptFolders"]!.Select(f => (string)f["ObjectType"]).ToList();
        Assert.That(declared, Is.SupersetOf(new[] { "RangeTypes", "TextSearchConfigurations", "Operators", "Casts", "EventTriggers" }), template.ToString());
    }

    // Skipped with a warning rather than failing the run: inheritance is how older databases partition, and the rest of
    // such a database extracts. A skipped object is exit 1, as every other one SchemaTongs skips, not the failure's 2.
    [Test]
    public void AnInheritanceChildAndATypedTable_AreSkippedWithAWarningByName_AndTheParentIsExtracted()
    {
        var tables = Directory.GetFiles(Path.Join(TemplatePath, "Tables")).Select(Path.GetFileName).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(tables, Does.Contain("app.parent.json"), string.Join(", ", tables));
            Assert.That(tables.Where(t => t!.Contains("child") || t.Contains("typed")), Is.Empty, string.Join(", ", tables));
            Assert.That(_warnings.Any(e => e.Contains("app.child") && e.Contains("inherits")), Is.True, string.Join(" | ", _warnings));
            Assert.That(_warnings.Any(e => e.Contains("app.typed") && e.Contains("typed table")), Is.True, string.Join(" | ", _warnings));
            Assert.That(_errors.Where(e => e.Contains("app.child") || e.Contains("app.typed")), Is.Empty, "a warning, not an error");
            Assert.That(_extractExitCode, Is.EqualTo(1), "skipped, as any object SchemaTongs cannot write, not failed");
            Assert.That(Written("Triggers").Where(t => t.Contains("child") || t.Contains("typed")), Is.Empty, "a refused table's triggers name a table the package lacks");
        });
    }

    [Test]
    public void TheQuenchRebuildsTheSource_OnAnEmptyDatabase_AndConvergesWhenRerun()
    {
        Quench();
        Assert.That(TargetSignature(), Is.EqualTo(SourceSignature()), "rebuilt as the source has it");

        using (var conn = Open(_targetDb))
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                ALTER TEXT SEARCH CONFIGURATION app.docs ALTER MAPPING FOR asciiword WITH simple;
                ALTER TEXT SEARCH CONFIGURATION app.docs ADD MAPPING FOR email WITH simple;
                ALTER EVENT TRIGGER audit_ddl DISABLE;
                ALTER EVENT TRIGGER audit_all ENABLE;
                """;
            cmd.ExecuteNonQuery();
        }
        Quench();
        Assert.That(TargetSignature(), Is.EqualTo(SourceSignature()), "a redeploy puts the mappings and the enabled states back");
    }

    private void Quench()
    {
        var errors = new List<string>();
        var errorLog = Substitute.For<ILog>();
        errorLog.When(l => l.Error(Arg.Any<object>())).Do(ci => errors.Add(ci.ArgAt<object>(0)?.ToString() ?? ""));
        var progressLog = Substitute.For<ILog>();
        progressLog.When(l => l.Error(Arg.Any<object>())).Do(ci => errors.Add(ci.ArgAt<object>(0)?.ToString() ?? ""));
        var environment = Substitute.For<IEnvironment>();
        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Clear();
            LogFactory.Clear();
            var config = BuildConfig(_targetDb);
            config["SchemaPackagePath"] = _productPath;
            FactoryContainer.Register<IConfigurationRoot>(config);
            FactoryContainer.Register(environment);
            LogFactory.Register("ErrorLog", errorLog);
            LogFactory.Register("ProgressLog", progressLog);
            try
            {
                Schema.Checkpointing.FileCheckpointManager.GetFromFactory().DeleteCheckpoints(ProductName);
                SchemaQuench.Program.Main([]);
            }
            finally
            {
                FactoryContainer.Clear();
                LogFactory.Clear();
            }
        }
        Assert.Multiple(() =>
        {
            Assert.That(errors, Is.Empty);
            environment.DidNotReceive().Exit(Arg.Is<int>(c => c != 0));
        });
    }

    private string TargetSignature()
    {
        using var conn = Open(_targetDb);
        using var cmd = conn.CreateCommand();
        return Signature(cmd);
    }

    private string SourceSignature()
    {
        using var conn = Open(_sourceDb);
        using var cmd = conn.CreateCommand();
        return Signature(cmd);
    }

    private static string Signature(IDbCommand cmd)
    {
        cmd.CommandText = """
            SELECT STRING_AGG(s, E'\n' ORDER BY s) FROM (
              SELECT FORMAT('range %s: %s %s %s %s', r.rngtypid::regtype, r.rngsubtype::regtype, (SELECT oc.opcname FROM pg_opclass oc WHERE oc.oid = r.rngsubopc), r.rngcollation, r.rngsubdiff::regproc) AS s
                FROM pg_range r JOIN pg_type t ON t.oid = r.rngtypid JOIN pg_namespace n ON n.oid = t.typnamespace WHERE n.nspname = 'app'
              UNION ALL
              SELECT FORMAT('multirange %s', t.oid::regtype) FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace WHERE n.nspname = 'app' AND t.typtype = 'm'
              UNION ALL
              SELECT FORMAT('ts %s %s %s: %s', c.cfgname, p.prsname, tt.alias, (SELECT STRING_AGG(m.mapdict::regdictionary::text, ',' ORDER BY m.mapseqno) FROM pg_ts_config_map m WHERE m.mapcfg = c.oid AND m.maptokentype = tt.tokid))
                FROM pg_ts_config c JOIN pg_ts_parser p ON p.oid = c.cfgparser JOIN pg_namespace n ON n.oid = c.cfgnamespace, LATERAL ts_token_type(c.cfgparser) tt WHERE n.nspname = 'app'
              UNION ALL
              SELECT FORMAT('op %s(%s,%s) %s com=%s neg=%s %s %s %s %s', o.oprname, o.oprleft::regtype, o.oprright::regtype, o.oprcode, o.oprcom::regoperator, o.oprnegate::regoperator, o.oprrest, o.oprjoin, o.oprcanhash, o.oprcanmerge)
                FROM pg_operator o JOIN pg_namespace n ON n.oid = o.oprnamespace WHERE n.nspname = 'app'
              UNION ALL
              SELECT FORMAT('cast %s->%s %s %s %s', c.castsource::regtype, c.casttarget::regtype, c.castfunc::regprocedure, c.castcontext, c.castmethod) FROM pg_cast c WHERE c.oid >= 16384
              UNION ALL
              SELECT FORMAT('evt %s %s %s %s %s', e.evtname, e.evtevent, e.evtfoid::regproc, e.evtenabled, e.evttags) FROM pg_event_trigger e) q
            """;
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    private IDbConnection Open(string db)
    {
        var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        if (db != "postgres") conn.ChangeDatabase(db);
        return conn;
    }

    private void Extract()
    {
        var errorLog = Substitute.For<ILog>();
        errorLog.When(l => l.Error(Arg.Any<object>())).Do(ci => _errors.Add(ci.ArgAt<object>(0)?.ToString() ?? ""));
        var progressLog = Substitute.For<ILog>();
        progressLog.When(l => l.Error(Arg.Any<object>())).Do(ci => _errors.Add(ci.ArgAt<object>(0)?.ToString() ?? ""));
        progressLog.When(l => l.Warn(Arg.Any<object>())).Do(ci => _warnings.Add(ci.ArgAt<object>(0)?.ToString() ?? ""));
        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Clear();
            LogFactory.Clear();
            FactoryContainer.Register<IConfigurationRoot>(BuildConfig(_sourceDb));
            FactoryContainer.Register(Substitute.For<IEnvironment>());
            LogFactory.Register("ErrorLog", errorLog);
            LogFactory.Register("ProgressLog", progressLog);
            var tongs = new SchemaTongs(Platform.PostgreSQL);
            tongs.CastTemplate();
            _extractExitCode = tongs.ExitCode;
            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    private IConfigurationRoot BuildConfig(string mainDb)
    {
        var root = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var values = new Dictionary<string, string>
        {
            ["Source:Server"] = root["PostgreSQL:Server"],
            ["Source:Port"] = root["PostgreSQL:Port"],
            ["Source:User"] = root["PostgreSQL:User"],
            ["Source:Password"] = root["PostgreSQL:Password"],
            ["Source:Database"] = _sourceDb,
            ["Target:Server"] = root["PostgreSQL:Server"],
            ["Target:Port"] = root["PostgreSQL:Port"],
            ["Target:User"] = root["PostgreSQL:User"],
            ["Target:Password"] = root["PostgreSQL:Password"],
            [$"ScriptTokens:{TemplateName}Db"] = mainDb,
            ["Product:Path"] = _productPath,
            ["Product:Name"] = ProductName,
            ["Template:Name"] = TemplateName,
        };
        foreach (var kind in new[] { "Tables", "Schemas", "Functions", "CompositeTypes", "TableTriggers", "RangeTypes", "TextSearchConfigurations", "Operators", "Casts", "EventTriggers" })
            values[$"ShouldCast:{kind}"] = "true";
        foreach (var kind in new[] { "DomainTypes", "EnumTypes", "Rules", "Collations", "Publications", "Sequences", "Views", "MaterializedViews", "Procedures", "Aggregates" })
            values[$"ShouldCast:{kind}"] = "false";
        foreach (var prop in ConnectionString.ReadProperties(root, "PostgreSQL:ConnectionProperties"))
        {
            values[$"Source:ConnectionProperties:{prop.Key}"] = prop.Value;
            values[$"Target:ConnectionProperties:{prop.Key}"] = prop.Value;
        }
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private void CreateSource()
    {
        using var conn = Open("postgres");
        using var cmd = conn.CreateCommand();
        foreach (var db in new[] { _sourceDb, _targetDb })
        {
            cmd.CommandText = $"CREATE DATABASE \"{db}\"";
            cmd.ExecuteNonQuery();
        }
        conn.ChangeDatabase(_sourceDb);
        cmd.CommandText = "SHOW server_version_num";
        _major = Convert.ToInt32(cmd.ExecuteScalar()) / 10000;
        // A multirange type (14+) named other than the engine's default is part of the declaration.
        if (_major >= 14)
        {
            cmd.CommandText = "CREATE SCHEMA app; CREATE TYPE app.span8 AS RANGE (SUBTYPE = int8, MULTIRANGE_TYPE_NAME = app.spans8)";
            cmd.ExecuteNonQuery();
        }
        else
        {
            cmd.CommandText = "CREATE SCHEMA app";
            cmd.ExecuteNonQuery();
        }
        cmd.CommandText = """
            CREATE FUNCTION app.float_diff(a float8, b float8) RETURNS float8 LANGUAGE sql IMMUTABLE AS 'SELECT a - b';
            CREATE TYPE app.floatrange AS RANGE (SUBTYPE = float8, SUBTYPE_DIFF = app.float_diff);
            CREATE TYPE app.textrange AS RANGE (SUBTYPE = text, COLLATION = "C");
            CREATE TYPE app.span AS RANGE (SUBTYPE = int4);
            CREATE TEXT SEARCH DICTIONARY app.eng_stem (TEMPLATE = snowball, Language = english, StopWords = english);
            CREATE TEXT SEARCH CONFIGURATION app.docs (PARSER = pg_catalog."default");
            ALTER TEXT SEARCH CONFIGURATION app.docs ADD MAPPING FOR asciiword, word WITH app.eng_stem, simple;
            ALTER TEXT SEARCH CONFIGURATION app.docs ADD MAPPING FOR int, uint WITH simple;
            CREATE TYPE app.cents AS (v int);
            CREATE FUNCTION app.cents_eq(a app.cents, b app.cents) RETURNS bool LANGUAGE sql IMMUTABLE AS 'SELECT a.v = b.v';
            CREATE FUNCTION app.cents_ne(a app.cents, b app.cents) RETURNS bool LANGUAGE sql IMMUTABLE AS 'SELECT a.v <> b.v';
            CREATE OPERATOR app.=== (FUNCTION = app.cents_eq, LEFTARG = app.cents, RIGHTARG = app.cents, COMMUTATOR = OPERATOR(app.===), NEGATOR = OPERATOR(app.!==), RESTRICT = eqsel, JOIN = eqjoinsel);
            CREATE OPERATOR app.!== (FUNCTION = app.cents_ne, LEFTARG = app.cents, RIGHTARG = app.cents, NEGATOR = OPERATOR(app.===));
            CREATE FUNCTION app.neg(a app.cents) RETURNS app.cents LANGUAGE sql IMMUTABLE AS 'SELECT ROW(-a.v)::app.cents';
            CREATE OPERATOR app.~~~ (FUNCTION = app.neg, RIGHTARG = app.cents);
            CREATE FUNCTION app.cents_to_int(a app.cents) RETURNS int LANGUAGE sql IMMUTABLE AS 'SELECT a.v';
            CREATE CAST (app.cents AS int) WITH FUNCTION app.cents_to_int(app.cents) AS ASSIGNMENT;
            CREATE CAST (app.cents AS text) WITH INOUT;
            CREATE FUNCTION app.log_ddl() RETURNS event_trigger LANGUAGE plpgsql AS $$ BEGIN NULL; END $$;
            CREATE EVENT TRIGGER audit_ddl ON ddl_command_end WHEN TAG IN ('CREATE TABLE', 'ALTER TABLE') EXECUTE FUNCTION app.log_ddl();
            CREATE EVENT TRIGGER audit_all ON ddl_command_start EXECUTE FUNCTION app.log_ddl();
            ALTER EVENT TRIGGER audit_all DISABLE;
            CREATE FUNCTION app.row_fn() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$;
            CREATE TABLE app.parent (id int NOT NULL PRIMARY KEY);
            CREATE TABLE app.child (extra int) INHERITS (app.parent);
            CREATE TRIGGER child_tr BEFORE INSERT ON app.child FOR EACH ROW EXECUTE FUNCTION app.row_fn();
            CREATE TABLE app.typed OF app.cents;
            CREATE TRIGGER typed_tr BEFORE INSERT ON app.typed FOR EACH ROW EXECUTE FUNCTION app.row_fn();
            """;
        cmd.ExecuteNonQuery();
    }
}
