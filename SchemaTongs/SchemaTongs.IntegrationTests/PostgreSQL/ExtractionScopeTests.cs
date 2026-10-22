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
/// SchemaTongs enumerates exactly the user's objects: an installed extension's members are not extracted (and the skip
/// is logged), an unlogged table is, a trigger on a partitioned table or one of its partitions is not (the tables are
/// refused), and the engine's own rowtypes never surface as composite types or sequences (#519).
/// </summary>
[Category("PostgreSQL")]
[TestFixture]
[Category("Integration")]
public class ExtractionScopeTests
{
    private string _connectionString = "";
    private string _sourceDb = "";
    private string _targetDb = "";
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _log = [];
    private readonly List<string> _deleted = [];

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        _connectionString = ConnectionString.Build(Platform.PostgreSQL, config["PostgreSQL:Server"], "postgres", config["PostgreSQL:User"], config["PostgreSQL:Password"], config["PostgreSQL:Port"],
            ConnectionString.ReadProperties(config, "PostgreSQL:ConnectionProperties"));
        var unique = $"{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}";
        _sourceDb = $"ScopeSrc_{unique}";
        _targetDb = $"ScopeDst_{unique}";
        CreateSource();
        Extract();
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

    private IEnumerable<string> Written(string folder) =>
        _files.Keys.Where(k => k.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)).Select(k => k[(folder.Length + 1)..]).OrderBy(k => k).ToList();

    [Test]
    public void AnUnloggedTable_IsExtracted_AsUnlogged()
    {
        Assert.That(Written("Tables"), Does.Contain("ulog.json"), "tables written: " + string.Join(", ", Written("Tables")));
        Assert.That(_files["Tables/ulog.json"], Does.Contain("\"PersistenceType\": \"Unlogged\""), _files["Tables/ulog.json"]);
    }

    [Test]
    public void AnInstalledExtensionsMembers_AreNotExtracted_AndTheSkipIsLogged()
    {
        var functions = Written("Functions").ToList();
        Assert.Multiple(() =>
        {
            Assert.That(functions, Does.Contain("public.user_fn.sql"), "functions written: " + string.Join(", ", functions));
            Assert.That(functions.Where(f => f.Contains("trgm") || f.StartsWith("public.similarity") || f.StartsWith("public.word_similarity")), Is.Empty,
                "pg_trgm's functions are the extension's, not the package's: " + string.Join(", ", functions));
            Assert.That(Written("Tables").Where(t => t.Contains("spatial_ref_sys")), Is.Empty, "an extension's table is the extension's");
            Assert.That(_log.Any(l => l.Contains("pg_trgm") && l.Contains("extension")), Is.True, "the skip is said once per extension; log: " + string.Join(" | ", _log.Where(l => l.Contains("xtension"))));
        });
    }

    [Test]
    public void TriggersOnAPartitionedTableAndItsPartitions_AreNotExtracted()
    {
        var triggers = Written("Triggers").ToList();
        Assert.Multiple(() =>
        {
            Assert.That(triggers, Does.Contain("public.plain.plain_tr.sql"), "triggers written: " + string.Join(", ", triggers));
            Assert.That(triggers.Where(t => t.Contains(".pp")), Is.Empty, "the partitioned table and its partitions are refused, so their triggers cannot deploy: " + string.Join(", ", triggers));
        });
    }

    [Test]
    public void EngineRowtypes_AreNotCompositeTypes_AndOwnedSequencesAreNotSequences()
    {
        var composites = Written("Composite Types").ToList();
        var sequences = Written("Sequences").ToList();
        Assert.Multiple(() =>
        {
            Assert.That(composites, Does.Contain("x.ct2.sql"), "a composite type in a schema off the search_path is extracted: " + string.Join(", ", composites));
            Assert.That(composites.Where(c => c.StartsWith("public.")), Is.Empty, "a table, materialized view or sequence rowtype is not a composite type: " + string.Join(", ", composites));
            Assert.That(sequences.Where(s => s.Contains("_id_seq")), Is.Empty, "an identity or serial column's sequence belongs to the column: " + string.Join(", ", sequences));
            Assert.That(sequences, Does.Contain("public.standalone_seq.json"), "a standalone sequence is still extracted: " + string.Join(", ", sequences));
        });
    }

    [Test]
    public void OverloadedRoutines_GetOneFileEach_NamedByTheirSignature_AndBothDeploy()
    {
        var functions = Written("Functions").ToList();
        Assert.Multiple(() =>
        {
            Assert.That(functions, Does.Contain("public.f_over(integer).sql"), "functions written: " + string.Join(", ", functions));
            Assert.That(functions, Does.Contain("public.f_over(text).sql"), "functions written: " + string.Join(", ", functions));
            Assert.That(functions, Does.Not.Contain("public.f_over.sql"), "an overloaded name has no un-suffixed file");
            Assert.That(functions, Does.Contain("public.user_fn.sql"), "a single-signature routine keeps its plain file name");
            Assert.That(_deleted.Any(d => d.Replace('\\', '/').EndsWith("Functions/public.f_over.sql")), Is.True,
                "the single-signature file from before the name was overloaded is retired: " + string.Join(", ", _deleted));
            Assert.That(_log.Any(l => l.Contains("Renamed public.f_over.sql")), Is.True, "the retirement is logged; log: " + string.Join(" | ", _log.Where(l => l.Contains("Renamed"))));
        });

        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_targetDb);
        using var cmd = conn.CreateCommand();
        foreach (var name in new[] { "public.f_over(integer).sql", "public.f_over(text).sql" })
        {
            cmd.CommandText = _files["Functions/" + name];
            cmd.ExecuteNonQuery();
        }
        cmd.CommandText = "SELECT COUNT(*) FROM pg_proc WHERE proname = 'f_over'";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(2), "both overloads deploy from their own files");
    }

    // PG-009's declined kinds need compiled support functions, so they are named in a warning rather than silently absent.
    [Test]
    public void BaseTypesOperatorClassesAndTextSearchParsersAndTemplates_AreNamedInAWarning()
    {
        var skipped = _log.Where(l => l.Contains("Skipping") && l.Contains("compiled into a server library")).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(skipped.Any(l => l.Contains("base type x.mytext")), Is.True, string.Join(" | ", skipped));
            Assert.That(skipped.Any(l => l.Contains("operator class x.int4_alt (btree)")), Is.True, string.Join(" | ", skipped));
            Assert.That(skipped.Any(l => l.Contains("operator family x.fam (btree)")), Is.True, string.Join(" | ", skipped));
            Assert.That(skipped.Any(l => l.Contains("operator class x.int4_own (btree)")), Is.True, string.Join(" | ", skipped));
            Assert.That(skipped.Where(l => l.Contains("operator family x.int4_own")), Is.Empty, "the family a class made for itself is the class's line: " + string.Join(" | ", skipped));
            Assert.That(skipped.Any(l => l.Contains("text search parser x.prs")), Is.True, string.Join(" | ", skipped));
            Assert.That(skipped.Any(l => l.Contains("text search template x.tmpl")), Is.True, string.Join(" | ", skipped));
            Assert.That(skipped.Where(l => l.Contains("_mytext") || l.Contains("ct2") || l.Contains("trgm")), Is.Empty,
                "an array type, a composite type and an extension's members are not declined base types: " + string.Join(" | ", skipped));
        });
    }

    [Test]
    public void ASerialColumn_IsExtractedAsSerial_WithNoDefault_AndAnIdentityColumnStaysIdentity()
    {
        Assert.That(Written("Tables"), Does.Contain("ser.json"), "tables written: " + string.Join(", ", Written("Tables")));
        var ser = _files["Tables/ser.json"];
        var ida = _files["Tables/ida.json"];
        Assert.Multiple(() =>
        {
            Assert.That(ser, Does.Contain("\"DataType\": \"serial\""), "the column is the engine's own serial declaration, which creates and owns the sequence on deploy:\n" + ser);
            Assert.That(ser, Does.Not.Contain("nextval"), "no default naming a sequence the package does not contain:\n" + ser);
            Assert.That(ser.Split("\"Name\": \"id\"").Length - 1, Is.EqualTo(1), "the column is written once, whatever else depends on it (its primary key index does):\n" + ser);
            Assert.That(ida, Does.Contain("GENERATED ALWAYS AS IDENTITY"), "an identity column is untouched:\n" + ida);
            Assert.That(ida, Does.Not.Contain("serial"), ida);
        });
    }

    private void Extract()
    {
        var file = Substitute.For<IFile>();
        // The package was extracted before f_over was overloaded, so its single-signature file is on disk.
        file.Exists(Arg.Is<string>(p => p.Replace('\\', '/').EndsWith("Functions/public.f_over.sql"))).Returns(true);
        file.When(f => f.Delete(Arg.Any<string>())).Do(ci => _deleted.Add(ci.ArgAt<string>(0)));
        file.When(f => f.WriteAllText(Arg.Any<string>(), Arg.Any<string>())).Do(ci =>
        {
            var parts = ci.ArgAt<string>(0).Replace('\\', '/').Split('/');
            if (parts.Length < 2) return;
            _files[$"{parts[^2]}/{parts[^1]}"] = ci.ArgAt<string>(1);
        });
        var progressLog = Substitute.For<ILog>();
        progressLog.When(l => l.Info(Arg.Any<object>())).Do(ci => _log.Add(ci.ArgAt<object>(0)?.ToString() ?? ""));
        progressLog.When(l => l.Warn(Arg.Any<object>())).Do(ci => _log.Add(ci.ArgAt<object>(0)?.ToString() ?? ""));
        lock (FactoryContainer.SharedLockObject)
        {
            LogFactory.Register("ErrorLog", Substitute.For<ILog>());
            LogFactory.Register("ProgressLog", progressLog);
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
            foreach (var kind in new[] { "Tables", "Functions", "TableTriggers", "Sequences", "CompositeTypes", "MaterializedViews", "Views", "Procedures", "Aggregates" })
                config[$"ShouldCast:{kind}"] = "true";
            foreach (var kind in new[] { "Schemas", "DomainTypes", "EnumTypes", "Rules", "Collations", "Publications" })
                config[$"ShouldCast:{kind}"] = "false";
            FactoryContainer.Register<IConfigurationRoot>(config);
            new SchemaTongs(Platform.PostgreSQL).CastTemplate();
            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    private void CreateSource()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        foreach (var db in new[] { _sourceDb, _targetDb })
        {
            cmd.CommandText = $"CREATE DATABASE \"{db}\"";
            cmd.ExecuteNonQuery();
        }
        conn.ChangeDatabase(_sourceDb);
        cmd.CommandText = """
            CREATE EXTENSION pg_trgm;
            CREATE UNLOGGED TABLE public.ulog (id INT NOT NULL PRIMARY KEY, t TEXT);
            CREATE TABLE public.ida (id INT GENERATED ALWAYS AS IDENTITY PRIMARY KEY, n INT);
            CREATE TABLE public.ser (id SERIAL PRIMARY KEY, n INT);
            CREATE SEQUENCE public.standalone_seq;
            CREATE SCHEMA x;
            CREATE TYPE x.ct2 AS (a INT, b TEXT);
            CREATE MATERIALIZED VIEW public.mv1 AS SELECT 1 AS a;
            CREATE FUNCTION public.user_fn() RETURNS INT LANGUAGE sql AS 'SELECT 1';
            CREATE FUNCTION public.f_over(a INT) RETURNS INT LANGUAGE sql AS 'SELECT a';
            CREATE FUNCTION public.f_over(a TEXT) RETURNS TEXT LANGUAGE sql AS 'SELECT a';
            CREATE FUNCTION public.row_fn() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$;
            CREATE TABLE public.plain (id INT NOT NULL PRIMARY KEY);
            CREATE TRIGGER plain_tr BEFORE INSERT ON public.plain FOR EACH ROW EXECUTE FUNCTION public.row_fn();
            CREATE TABLE public.pp (id INT NOT NULL, d DATE NOT NULL) PARTITION BY RANGE (d);
            CREATE TABLE public.pp1 PARTITION OF public.pp FOR VALUES FROM ('2024-01-01') TO ('2025-01-01');
            CREATE TABLE public.pp2 PARTITION OF public.pp FOR VALUES FROM ('2025-01-01') TO ('2026-01-01');
            CREATE TRIGGER pp_tr AFTER INSERT ON public.pp FOR EACH ROW EXECUTE FUNCTION public.row_fn();
            CREATE TYPE x.mytext;
            CREATE FUNCTION x.mytext_in(cstring) RETURNS x.mytext LANGUAGE internal IMMUTABLE STRICT AS 'textin';
            CREATE FUNCTION x.mytext_out(x.mytext) RETURNS cstring LANGUAGE internal IMMUTABLE STRICT AS 'textout';
            CREATE TYPE x.mytext (INPUT = x.mytext_in, OUTPUT = x.mytext_out, LIKE = text);
            CREATE OPERATOR FAMILY x.fam USING btree;
            CREATE OPERATOR CLASS x.int4_alt FOR TYPE int4 USING btree FAMILY x.fam AS
              OPERATOR 1 <, OPERATOR 2 <=, OPERATOR 3 =, OPERATOR 4 >=, OPERATOR 5 >, FUNCTION 1 btint4cmp(int4, int4);
            CREATE OPERATOR CLASS x.int4_own FOR TYPE int4 USING btree AS
              OPERATOR 1 <, OPERATOR 2 <=, OPERATOR 3 =, OPERATOR 4 >=, OPERATOR 5 >, FUNCTION 1 btint4cmp(int4, int4);
            CREATE TEXT SEARCH PARSER x.prs (START = prsd_start, GETTOKEN = prsd_nexttoken, END = prsd_end, LEXTYPES = prsd_lextype);
            CREATE TEXT SEARCH TEMPLATE x.tmpl (INIT = dsimple_init, LEXIZE = dsimple_lexize);
            """;
        cmd.ExecuteNonQuery();
    }
}
