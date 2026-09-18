// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.SqlClient;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.GenuineOldBinary;

// #242 across SQL Server versions. Every engine version renders expressions its own way, and SQL Server also
// freezes a stored expression at the compatibility level it was CREATED under -- so "the expression surfaces are
// idempotent" measured on one version is a claim about one version. This fixture measures every surface the
// expression map covers, on whatever instance it is pointed at, through the ingest encoding production would
// select for it (XML below 2017 or below compat 130, JSON otherwise):
//   * a CHECK constraint, a PERSISTED computed column and a function default, each authored in natural form;
//   * a filtered index and a filtered statistic, through both the full quench and the index-only quench;
//   * a real compatibility-level change (100 -> the server's default), which must RE-BASELINE, not re-apply.
// Identity, never text: a constraint keeps its object_id, a column its column_id (a dropped and re-added column
// moves to the end), an index its hobt_id; a statistic is proven by the absence of its drop message.
//
// [Explicit], no category: it is pointed at an instance deliberately, once per band --
//   SmithySettings_SqlServer__Server=127.0.0.1 SmithySettings_SqlServer__Port=14330 \
//   SmithySettings_SqlServer__User=sa SmithySettings_SqlServer__Password='SchemaSmith!Old2026' \
//   dotnet test Schema/Schema.IntegrationTests --filter FullyQualifiedName~ExpressionChurnAcrossVersions
[Explicit("Measures expression churn on whichever SQL Server the SmithySettings_SqlServer__* env vars point at.")]
[TestFixture]
public class ExpressionChurnAcrossVersionsTests
{
    private const string ProductName = "ExprBands";

    // Natural form throughout -- never the catalog's rendering (Rule 32). Every one of these is rewritten on
    // storage: the CHECK and computed column gain brackets and parentheses, getdate() becomes (getdate()), and
    // the filter predicates are re-parenthesised.
    private const string TableJson = """
        [
          {
            "Schema": "[dbo]",
            "Name": "[ExprBand]",
            "Columns": [
              { "Name": "[Id]", "DataType": "INT" },
              { "Name": "[Qty]", "DataType": "INT", "Nullable": true },
              { "Name": "[Total]", "DataType": "INT", "ComputedExpression": "Qty * 2", "Persisted": true },
              { "Name": "[CreatedAt]", "DataType": "DATETIME", "Nullable": true, "Default": "getdate()" },
              { "Name": "[Status]", "DataType": "INT", "Nullable": true }
            ],
            "Indexes": [
              { "Name": "[PK_ExprBand]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" },
              { "Name": "[IX_ExprBand_Status]", "IndexColumns": "[Status]", "FilterExpression": "Status > 0 AND Qty IS NOT NULL" }
            ],
            "CheckConstraints": [
              { "Name": "[CK_ExprBand_Qty]", "Expression": "Qty >= 0 AND Qty <= 1000" }
            ],
            "Statistics": [
              { "Name": "[ST_ExprBand_Qty]", "Columns": "[Qty]", "FilterExpression": "Qty > 10" }
            ]
          }
        ]
        """;

    private string _masterConnectionString = "";
    private string _server = "", _user = "", _password = "", _port = "";
    private Dictionary<string, string> _connProps = new();
    private int _serverMajor;
    private int _defaultCompat;
    private readonly List<string> _createdDbs = [];
    private readonly List<string> _messages = [];

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        _server = config["SqlServer:Server"] ?? "127.0.0.1";
        _user = config["SqlServer:User"];
        _password = config["SqlServer:Password"];
        _port = config["SqlServer:Port"];
        _connProps = ConnectionString.ReadProperties(config, "SqlServer:ConnectionProperties");
        _masterConnectionString = ConnectionString.Build(Platform.SqlServer, _server, "master", _user, _password, _port, _connProps);

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_masterConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        _serverMajor = TargetVersionDetector.Detect(cmd, Platform.SqlServer).ServerComparable;
        cmd.CommandText = "SELECT compatibility_level FROM sys.databases WHERE name = 'model'";
        _defaultCompat = Convert.ToInt32(cmd.ExecuteScalar());
        TestContext.Progress.WriteLine($"ExpressionChurnAcrossVersions: SQL Server major {_serverMajor}, default compat {_defaultCompat}");
    }

    [Test]
    public void TheExpressionSurfaces_AreNotReAppliedOnEveryDeploy_AtServerDefaultCompat() => MeasureIdempotence(compat: null);

    [Test]
    public void TheExpressionSurfaces_AreNotReAppliedOnEveryDeploy_AtCompat100() => MeasureIdempotence(compat: 100);

    [Test]
    public void ACompatibilityLevelUpgrade_ReBaselines_AndReAppliesNothing()
    {
        if (_defaultCompat <= 100)
            Assert.Ignore($"The server's default compatibility level is {_defaultCompat}; there is no upgrade from 100 to measure.");

        var db = CreateDatabase("ExprBandUp", 100);
        using var conn = Open(db);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        var encoding = Kindle(cmd, 100);
        Deploy(cmd, encoding, indexOnly: false);
        var before = Identities(cmd);
        Assert.That(ScalarString(cmd, "SELECT TOP 1 CAST(CompatLevel AS VARCHAR(10)) FROM SchemaSmith.ExpressionMap WHERE ObjectTable = 'ExprBand'"),
            Is.EqualTo("100"), "setup: the mapping must be recorded at compat 100");

        conn.ChangeDatabase("master");
        cmd.CommandText = $"ALTER DATABASE [{db}] SET COMPATIBILITY_LEVEL = {_defaultCompat}";
        cmd.ExecuteNonQuery();
        conn.ChangeDatabase(db);

        // The legacy encoding was chosen for compat 100; production re-selects after the upgrade, and so does this.
        var upgraded = Kindle(cmd, _defaultCompat);
        _messages.Clear();
        Deploy(cmd, upgraded, indexOnly: false);

        var reported = _messages.FindAll(m => m.Contains("Re-baselined"));
        var staleRows = ScalarInt(cmd, $"SELECT COUNT(*) FROM SchemaSmith.ExpressionMap WHERE ObjectTable = 'ExprBand' AND CompatLevel <> {_defaultCompat}");
        Assert.Multiple(() =>
        {
            AssertSameIdentities(cmd, before, $"after raising compat 100 -> {_defaultCompat} (major {_serverMajor})");
            Assert.That(staleRows, Is.Zero, "every mapping row must carry the new compatibility level");
            Assert.That(reported, Has.Count.EqualTo(1), "the re-baseline must be reported: " + string.Join(" | ", _messages));
        });
    }

    private void MeasureIdempotence(int? compat)
    {
        var db = CreateDatabase(compat == null ? "ExprBandDflt" : "ExprBand100", compat);
        using var conn = Open(db);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        var encoding = Kindle(cmd, compat ?? _defaultCompat);
        var at = $"SQL Server major {_serverMajor} at compat {compat ?? _defaultCompat}, {encoding} ingest";

        Deploy(cmd, encoding, indexOnly: false);
        var first = Identities(cmd);
        Assert.That(first.CheckId, Is.Not.Zero, $"setup: the table must deploy ({at})");

        foreach (var indexOnly in new[] { false, false, true, true })
        {
            _messages.Clear();
            Deploy(cmd, encoding, indexOnly);
            var mode = indexOnly ? "index-only" : "full";
            Assert.Multiple(() =>
            {
                AssertSameIdentities(cmd, first, $"{mode} re-deploy, {at}");
                // Actions are logged indented under their step heading; a heading such as "Drop Referencing Foreign
                // Keys When Dropping Unique Indexes" runs every time and is not an action.
                Assert.That(_messages.FindAll(m => m.StartsWith("  Dropping") || m.StartsWith("  Altering Column") || m.StartsWith("  Creating statistics")), Is.Empty,
                    $"{mode} re-deploy dropped or re-created something ({at}): " + string.Join(" | ", _messages));
            });
        }
    }

    private sealed record ObjectIdentities(int CheckId, int DefaultId, int ComputedColumnId, long IndexHobt);

    private static ObjectIdentities Identities(IDbCommand cmd) => new(
        ScalarInt(cmd, "SELECT ISNULL(OBJECT_ID('dbo.CK_ExprBand_Qty'), 0)"),
        ScalarInt(cmd, "SELECT ISNULL((SELECT default_object_id FROM sys.columns WHERE object_id = OBJECT_ID('dbo.ExprBand') AND name = 'CreatedAt'), 0)"),
        ScalarInt(cmd, "SELECT ISNULL((SELECT column_id FROM sys.computed_columns WHERE object_id = OBJECT_ID('dbo.ExprBand') AND name = 'Total'), 0)"),
        Convert.ToInt64(Scalar(cmd, @"SELECT ISNULL((SELECT TOP 1 p.hobt_id FROM sys.indexes i JOIN sys.partitions p ON p.object_id = i.object_id AND p.index_id = i.index_id
                                                     WHERE i.object_id = OBJECT_ID('dbo.ExprBand') AND i.name = 'IX_ExprBand_Status'), 0)")));

    private static void AssertSameIdentities(IDbCommand cmd, ObjectIdentities expected, string phase)
    {
        var now = Identities(cmd);
        Assert.That(now.CheckId, Is.EqualTo(expected.CheckId), $"CHECK constraint re-created: {phase}");
        Assert.That(now.DefaultId, Is.EqualTo(expected.DefaultId), $"default re-created: {phase}");
        Assert.That(now.ComputedColumnId, Is.EqualTo(expected.ComputedColumnId), $"computed column dropped and re-added: {phase}");
        Assert.That(now.IndexHobt, Is.EqualTo(expected.IndexHobt), $"filtered index rebuilt: {phase}");
    }

    private IngestEncoding Kindle(IDbCommand cmd, int compat)
    {
        var encoding = CompatEncoding.Select(null, compat, _serverMajor);
        ForgeKindler.KindleTheForge(cmd, Platform.SqlServer, forceReKindle: true, encoding, _serverMajor, "warn");
        return encoding;
    }

    private static void Deploy(IDbCommand cmd, IngestEncoding encoding, bool indexOnly)
    {
        var payload = encoding == IngestEncoding.Xml ? ModelXmlSerializer.ToIngestXml(TableJson, "Tables", "Table") : TableJson;
        cmd.CommandText = indexOnly ? "SchemaSmith.IndexOnlyQuench" : "SchemaSmith.TableQuench";
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.Parameters.Clear();
        AddParam(cmd, "@ProductName", ProductName);
        AddParam(cmd, "@TableDefinitions", payload);
        if (indexOnly)
            AddParam(cmd, "@DropUnknownIndexes", 1);
        cmd.ExecuteNonQuery();
        cmd.Parameters.Clear();
        cmd.CommandType = CommandType.Text;
    }

    private static void AddParam(IDbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    private static object Scalar(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static int ScalarInt(IDbCommand cmd, string sql) => Convert.ToInt32(Scalar(cmd, sql));

    private static string ScalarString(IDbCommand cmd, string sql) => Scalar(cmd, sql) as string;

    private IDbConnection Open(string db)
    {
        var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer)
            .GetDbConnection(ConnectionString.Build(Platform.SqlServer, _server, db, _user, _password, _port, _connProps));
        _messages.Clear();
        ((SqlConnection)conn).InfoMessage += (_, e) =>
        {
            foreach (SqlError err in e.Errors) _messages.Add(err.Message);
        };
        conn.Open();
        return conn;
    }

    private string CreateDatabase(string prefix, int? compat)
    {
        var db = $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString()[..8]}";
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_masterConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE [{db}];" + (compat == null ? "" : $" ALTER DATABASE [{db}] SET COMPATIBILITY_LEVEL = {compat};");
        cmd.ExecuteNonQuery();
        _createdDbs.Add(db);
        return db;
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_masterConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        foreach (var db in _createdDbs)
        {
            // Classic guard, not DROP DATABASE IF EXISTS (2016 syntax): this runs against 2008 R2.
            cmd.CommandText = $@"
IF DB_ID('{db}') IS NOT NULL
BEGIN
  ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
  DROP DATABASE [{db}];
END";
            cmd.ExecuteNonQuery();
        }
    }
}
