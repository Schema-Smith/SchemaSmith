// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.GenuineOldBinary;

// Genuine-old-binary milestone test (Slice E). The modern CI container cannot reproduce a CREATE-time
// BINDING error against a 2016+ catalog column/view — every column exists there — so the only real proof
// that the legacy (XML) helper set, including the E1-version-gated GenerateTableXml / ModifiedTableQuench,
// CREATEs on a genuine pre-2016 binary is to kindle it against one. This fixture does exactly that against
// whatever SQL Server the SqlServer:* config points at (override via SmithySettings_SqlServer__* env vars),
// at BOTH the server's default compatibility level AND compat 100 (the supported floor).
//
// It is [Explicit] and carries NO [Category("SqlServer")], so a normal run — and the CI `Category=SqlServer`
// leg — never touches it; run it deliberately, pointed at a genuine instance:
//   SmithySettings_SqlServer__Server=127.0.0.1 SmithySettings_SqlServer__Port=14331 \
//   SmithySettings_SqlServer__User=sa SmithySettings_SqlServer__Password='SchemaSmith!Old2026' \
//   dotnet test Schema/Schema.IntegrationTests --filter FullyQualifiedName~GenuineOldBinary
// If pointed at a 2016+ binary it Ignores (the CREATE-binding risk it proves only exists below 2016).
[Explicit("Requires a genuine pre-2016 SQL Server instance; run manually via the SmithySettings_SqlServer__* env vars.")]
[TestFixture]
public class OldBinaryXmlKindleTests
{
    private string _masterConnectionString = "";
    private string _server = "", _user = "", _password = "", _port = "";
    private Dictionary<string, string> _connProps = new();
    private int _serverMajor;
    private readonly List<string> _createdDbs = [];

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
        conn.Close();
    }

    [Test]
    public void XmlHelperSet_Kindles_OnGenuineOldBinary_AtServerDefaultCompat()
        => KindleAndAssert(setCompat100: false);

    [Test]
    public void XmlHelperSet_Kindles_OnGenuineOldBinary_AtCompat100()
        => KindleAndAssert(setCompat100: true);

    private void KindleAndAssert(bool setCompat100)
    {
        if (_serverMajor >= 13)
            Assert.Ignore($"Detected SQL Server major {_serverMajor} (2016+); the CREATE-time binding this proves only exists below 2016.");

        var db = CreateDatabase(setCompat100 ? "OldBinXml100" : "OldBinXmlDflt", setCompat100);
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(DbConnectionString(db));
        conn.Open();
        using var cmd = conn.CreateCommand();

        // The whole legacy helper set must kindle without a CREATE-time error. Bake the detected major, as
        // production does — fn_ServerMajorVersion returns it, so the version-gated 2016 reads stay inside their
        // (skipped) dynamic blocks and no static 2016 catalog identifier reaches the compiled body.
        Assert.DoesNotThrow(
            () => ForgeKindler.KindleTheForge(cmd, Platform.SqlServer, forceReKindle: true, IngestEncoding.Xml, _serverMajor, "warn"),
            $"The legacy XML helper set must CREATE on SQL Server major {_serverMajor}" + (setCompat100 ? " at compatibility level 100." : "."));

        Assert.Multiple(() =>
        {
            // The E1-version-gated procs must exist (their 2016 reads are now inside dynamic blocks).
            Assert.That(ObjectExists(cmd, "SchemaSmith.GenerateTableXml", "P"), Is.True, "GenerateTableXml (E1.1)");
            Assert.That(ObjectExists(cmd, "SchemaSmith.ModifiedTableQuench", "P"), Is.True, "ModifiedTableQuench (E1.2)");
            // The kindle-baked helper functions (E1.0 — no SESSION_CONTEXT, which is 2016+).
            Assert.That(ObjectExists(cmd, "SchemaSmith.fn_ServerMajorVersion", "FN"), Is.True, "fn_ServerMajorVersion (E1.0)");
            Assert.That(ObjectExists(cmd, "SchemaSmith.UnsupportedFeaturePolicy", "FN"), Is.True, "UnsupportedFeaturePolicy (E1.0)");
            // The rebuild guard's pre-2016 body omits sys.tables.temporal_type_desc entirely (2016+, and a
            // function body binds at CREATE). Existence alone would pass if the gate produced an empty body,
            // so it is also CALLED: a table that blocks nothing must come back NULL, not an error.
            Assert.That(ObjectExists(cmd, "SchemaSmith.fn_RebuildBlockedReason", "FN"), Is.True, "fn_RebuildBlockedReason (pre-2016 body)");
            Assert.That(FnRebuildBlockedReason(cmd, "SchemaSmith", "ChangeAudit"), Is.Null,
                "The rebuild guard must EXECUTE below 2016, not merely CREATE -- a table with none of the "
                + "blocking states must be reported rebuildable rather than failing on a 2016-only catalog read.");
            // The rest of the shared apply set + the metadata tables.
            Assert.That(ObjectExists(cmd, "SchemaSmith.TableQuench", "P"), Is.True, "TableQuench");
            Assert.That(ObjectExists(cmd, "SchemaSmith.MissingTableAndColumnQuench", "P"), Is.True, "MissingTableAndColumnQuench");
            Assert.That(ObjectExists(cmd, "SchemaSmith.ChangeAudit", "U"), Is.True, "ChangeAudit table");
            // The JSON-only helpers must NOT be kindled on the legacy encoding.
            Assert.That(ObjectExists(cmd, "SchemaSmith.GenerateTableJSON", "P"), Is.False, "JSON GenerateTableJSON must be absent");
            Assert.That(ObjectExists(cmd, "SchemaSmith.fn_FormatJson", "FN"), Is.False, "fn_FormatJson must be absent");
            // fn_ServerMajorVersion resolves the baked major (proves E1.0's bake + that SESSION_CONTEXT is gone).
            Assert.That(FnServerMajorVersion(cmd), Is.EqualTo(_serverMajor), "fn_ServerMajorVersion must return the baked major");
        });

        conn.Close();
    }

    private static bool ObjectExists(IDbCommand cmd, string name, string type)
    {
        cmd.CommandText = $"SELECT CASE WHEN OBJECT_ID('{name}', '{type}') IS NULL THEN 0 ELSE 1 END";
        return Convert.ToInt32(cmd.ExecuteScalar()) == 1;
    }

    private static int FnServerMajorVersion(IDbCommand cmd)
    {
        cmd.CommandText = "SELECT SchemaSmith.fn_ServerMajorVersion()";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static string FnRebuildBlockedReason(IDbCommand cmd, string schema, string table)
    {
        cmd.CommandText = $"SELECT SchemaSmith.fn_RebuildBlockedReason('{schema}', '{table}')";
        var result = cmd.ExecuteScalar();
        return result == null || result == DBNull.Value ? null : result.ToString();
    }

    private string CreateDatabase(string prefix, bool setCompat100)
    {
        var db = $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString()[..8]}";
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_masterConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE [{db}];" + (setCompat100 ? $" ALTER DATABASE [{db}] SET COMPATIBILITY_LEVEL = 100;" : "");
        cmd.ExecuteNonQuery();
        conn.Close();
        _createdDbs.Add(db);
        return db;
    }

    private string DbConnectionString(string db) =>
        ConnectionString.Build(Platform.SqlServer, _server, db, _user, _password, _port, _connProps);

    [OneTimeTearDown]
    public void TearDown()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_masterConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        foreach (var db in _createdDbs)
        {
            Schema.IntegrationTests.SqlServer.TestDatabaseDrop.Drop(cmd, db);
        }
        conn.Close();
    }
}
