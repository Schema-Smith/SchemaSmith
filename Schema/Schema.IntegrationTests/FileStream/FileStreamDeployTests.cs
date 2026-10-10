// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.FileStream;

// FILESTREAM deployment, against a genuine Windows SQL Server instance with FILESTREAM enabled.
//
// [Explicit] with NO [Category("SqlServer")], so CI's Category=SqlServer filter skips it -- the same
// arrangement OldBinaryXmlKindleTests uses, and for the same reason: the thing under test is not
// reachable from CI. FILESTREAM is unsupported on SQL Server on Linux and every CI engine job is a Linux
// container; Windows containers were tried for this repo and did not work out. No arrangement of CI
// certifies this, so it is certified locally and deliberately.
//
// Run against a Windows instance with FILESTREAM enabled (the FilestreamSettings WMI class, then
// sp_configure 'filestream access level'), pointing the usual SmithySettings_SqlServer__* env vars at
// it. It Ignores rather than fails when FILESTREAM is off, so an explicit run against the wrong target
// says why instead of looking broken.
[Explicit("Requires a Windows SQL Server instance with FILESTREAM enabled; run manually via the SmithySettings_SqlServer__* env vars.")]
[TestFixture]
public class FileStreamDeployTests
{
    private IDbConnection _connection;
    private string _db;
    private bool _fileStreamAvailable;
    private IngestEncoding _encoding = IngestEncoding.Json;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var server = config["SqlServer:Server"] ?? "127.0.0.1";
        var user = config["SqlServer:User"];
        var password = config["SqlServer:Password"];
        var port = config["SqlServer:Port"];
        var props = ConnectionString.ReadProperties(config, "SqlServer:ConnectionProperties");

        _connection = DbConnectionFactory.ForPlatform(Platform.SqlServer)
            .GetDbConnection(ConnectionString.Build(Platform.SqlServer, server, "master", user, password, port, props));
        _connection.Open();

        _fileStreamAvailable = Scalar("SELECT CONVERT(INT, ISNULL(SERVERPROPERTY('FilestreamEffectiveLevel'), 0))") > 0;
        if (!_fileStreamAvailable) return;

        // Its own database, because a FILESTREAM filegroup is a create-time property.
        _db = $"SchemaFs_{Guid.NewGuid():N}"[..30];
        var dataPath = ScalarString("SELECT CONVERT(NVARCHAR(400), SERVERPROPERTY('InstanceDefaultDataPath'))");
        // Three filegroups, not one. Placement cannot be proved with a single filestream filegroup and
        // no LOB filegroup: SQL Server binds the default automatically, so an assertion that "a filegroup
        // is bound" passes whether or not the DECLARED name was honoured -- which is exactly how the
        // original assertion in this fixture passed over a property that did nothing at all.
        Exec($"CREATE DATABASE [{_db}] ON PRIMARY (NAME = {_db}_d, FILENAME = '{dataPath}{_db}.mdf'), "
             + $"FILEGROUP FsFg CONTAINS FILESTREAM (NAME = {_db}_fs, FILENAME = '{dataPath}{_db}Fs'), "
             + $"FILEGROUP FsFg2 CONTAINS FILESTREAM (NAME = {_db}_fs2, FILENAME = '{dataPath}{_db}Fs2'), "
             + $"FILEGROUP LobFg (NAME = {_db}_lob, FILENAME = '{dataPath}{_db}Lob.ndf') "
             + $"LOG ON (NAME = {_db}_l, FILENAME = '{dataPath}{_db}.ldf')");
        _connection.ChangeDatabase(_db);

        using var cmd = _connection.CreateCommand();
        // Encoding by server version, not by default. The JSON ingest path uses STRING_AGG (2017+), and
        // the newest Windows instance available here is 2016 -- which is exactly the situation the XML
        // tier exists for. A happy side effect: this certifies FILESTREAM through the legacy XML parse
        // and generate scripts, while the degrade fixture on the modern Linux container certifies the
        // JSON ones.
        var serverMajor = TargetVersionDetector.Detect(cmd, Platform.SqlServer).ServerComparable;
        _encoding = serverMajor < 14 ? IngestEncoding.Xml : IngestEncoding.Json;
        ForgeKindler.KindleTheForge(cmd, Platform.SqlServer, forceReKindle: true, _encoding, serverMajor, "warn");
    }

    [SetUp]
    public void RequireFileStream()
    {
        if (!_fileStreamAvailable)
            Assert.Ignore("FILESTREAM is not enabled on the target instance; nothing here can be proven against it.");
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        if (_connection == null) return;
        try
        {
            if (_db != null && Environment.GetEnvironmentVariable("FS_KEEP_DB") == null)
            {
                _connection.ChangeDatabase("master");
                using var cmd = _connection.CreateCommand();
                cmd.CommandTimeout = 300;
                Schema.IntegrationTests.SqlServer.TestDatabaseDrop.Drop(cmd, _db);
            }
        }
        finally
        {
            _connection.Close();
            _connection.Dispose();
        }
    }

    private void Exec(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();
    }

    private int Scalar(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        var r = cmd.ExecuteScalar();
        return r == null || r == DBNull.Value ? 0 : Convert.ToInt32(r);
    }

    private string ScalarString(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar() as string;
    }

    private void Deploy(string json)
    {
        // The XML tier inlines the XML parser into TableQuench, so @TableDefinitions must be XML there --
        // handing it JSON creates the table and silently drops every column detail, which is exactly how
        // this fixture first failed. ModelXmlSerializer is the same conversion the product uses.
        var definitions = _encoding == IngestEncoding.Xml
            ? ModelXmlSerializer.ToIngestXml(json, "Tables", "Table")
            : json;

        using var cmd = _connection.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = "EXEC SchemaSmith.TableQuench @ProductName = 'FsTest', @TableDefinitions = @TableDefinitions";
        var p = cmd.CreateParameter();
        p.ParameterName = "@TableDefinitions";
        p.Value = definitions;
        cmd.Parameters.Add(p);
        cmd.ExecuteNonQuery();
    }

    private static string Package(string table, string guidIndexKind) =>
        "[{ \"Schema\": \"[dbo]\", \"Name\": \"[" + table + "]\", \"Columns\": ["
        + " { \"Name\": \"[Id]\", \"DataType\": \"INT\", \"Nullable\": false },"
        + " { \"Name\": \"[G]\", \"DataType\": \"UNIQUEIDENTIFIER ROWGUIDCOL\", \"Nullable\": false, \"Default\": \"NEWID()\" },"
        + " { \"Name\": \"[Doc]\", \"DataType\": \"VARBINARY(MAX)\", \"Nullable\": true, \"FileStream\": true } ], \"Indexes\": ["
        + " { \"Name\": \"[PK_" + table + "]\", \"IndexColumns\": \"[Id]\", \"PrimaryKey\": true, \"Unique\": true },"
        + " { \"Name\": \"[UQ_" + table + "_G]\", \"IndexColumns\": \"[G]\", " + guidIndexKind + " } ] }]";

    [Test]
    public void AFileStreamColumn_Deploys_WhenTheRowGuidColIsCoveredByAUniqueConstraint()
    {
        Deploy(Package("FsOk", "\"UniqueConstraint\": true"));

        Assert.Multiple(() =>
        {
            Assert.That(Scalar("SELECT CONVERT(INT, is_filestream) FROM sys.columns "
                               + "WHERE [object_id] = OBJECT_ID('dbo.FsOk') AND name = 'Doc'"),
                Is.EqualTo(1), "the column has to actually be FILESTREAM, not merely present");

            Assert.That(ScalarString("SELECT ds.name FROM sys.tables t "
                               + "JOIN sys.data_spaces ds ON ds.data_space_id = t.filestream_data_space_id "
                               + "WHERE t.name = 'FsOk'"),
                Is.EqualTo("FsFg"),
                "and the table is bound to a FILESTREAM filegroup BY NAME. Asserting only that the id is non-null passes automatically -- SQL Server binds the default whenever a FILESTREAM column exists.");
        });
    }

    [Test]
    public void AFileStreamColumn_IsIdempotent()
    {
        // The second deploy is the one that finds bugs: a pass that adds the column whenever it is
        // declared, rather than only when new, fails here on a duplicate column.
        Deploy(Package("FsTwice", "\"UniqueConstraint\": true"));
        Deploy(Package("FsTwice", "\"UniqueConstraint\": true"));

        Assert.That(Scalar("SELECT COUNT(*) FROM sys.columns WHERE [object_id] = OBJECT_ID('dbo.FsTwice') "
                           + "AND name = 'Doc'"), Is.EqualTo(1));
    }

    [Test]
    public void AUniqueIndexOnTheRowGuidCol_IsRefusedWithTheFixInTheMessage()
    {
        // The trap this feature had to be probed to discover: 5505 demands a unique CONSTRAINT, and a
        // unique INDEX does not satisfy it. SQL Server's own message never says so, so SchemaSmith must.
        var ex = Assert.Catch(() => Deploy(Package("FsIndexOnly", "\"Unique\": true")));

        Assert.That(ex, Is.Not.Null, "declaring FILESTREAM with only a unique index must not deploy quietly");
        Assert.Multiple(() =>
        {
            Assert.That(ex.Message, Does.Contain("ROWGUIDCOL"),
                "the message must name the property that is missing");
            Assert.That(ex.Message, Does.Contain("UniqueConstraint"),
                "and must name the exact package change that fixes it -- naming the problem without the "
                + "remedy just leaves the user with SQL Server's message, which is what this replaces");
        });
    }

    [Test]
    public void AFileStreamColumn_RoundTripsThroughExtraction()
    {
        Deploy(Package("FsRoundTrip", "\"UniqueConstraint\": true"));

        using var cmd = _connection.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = _encoding == IngestEncoding.Xml
            ? "EXEC SchemaSmith.GenerateTableXml @p_Schema = 'dbo', @p_Table = 'FsRoundTrip'"
            : "EXEC SchemaSmith.GenerateTableJson @p_Schema = 'dbo', @p_Table = 'FsRoundTrip'";
        var json = cmd.ExecuteScalar() as string ?? "";

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("FileStream"),
                "an extracted package that drops FileStream re-deploys the column as in-row storage -- a "
                + "silent storage change on every round trip");
            Assert.That(json, Does.Contain("ROWGUIDCOL"),
                "and the ROWGUIDCOL column has to come back carrying it -- it rides the DataType string "
                + "(\"UNIQUEIDENTIFIER ROWGUIDCOL\"), the same way IDENTITY does, so a round trip that "
                + "drops it produces a package that cannot satisfy 5505 on re-deploy");
        });
    }

    [Test]
    public void ADeclaredFileStreamFileGroup_IsHonoured()
    {
        // FsFg2 is deliberately NOT the default: the database declares FsFg first, so a package that is
        // ignored lands on FsFg and this fails. That is what makes the test worth having -- the previous
        // assertion in this fixture could not tell the two apart.
        Deploy(PackageOn("FsPlaced", "\"UniqueConstraint\": true", ", \"FileStreamFileGroup\": \"[FsFg2]\""));

        Assert.That(ScalarString("SELECT ds.name FROM sys.tables t "
                                 + "JOIN sys.data_spaces ds ON ds.data_space_id = t.filestream_data_space_id "
                                 + "WHERE t.name = 'FsPlaced'"),
            Is.EqualTo("FsFg2"),
            "a declared FILESTREAM filegroup has to reach the CREATE TABLE. Extracting it while never "
            + "deploying it tells the reader SchemaSmith manages placement when it does not.");
    }

    [Test]
    public void ADeclaredTextImageFileGroup_IsHonoured()
    {
        // A genuine non-FILESTREAM LOB column. A FILESTREAM varbinary(max) does NOT satisfy
        // TEXTIMAGE_ON -- error 1709 names "non-FILESTREAM varbinary(max)" explicitly -- so the LOB
        // guard has to exclude FILESTREAM columns, and this fixture has to prove it does.
        Deploy(PackageOn("LobPlaced", "\"UniqueConstraint\": true", ", \"TextImageFileGroup\": \"[LobFg]\"",
            ", { \"Name\": \"[Notes]\", \"DataType\": \"NVARCHAR(MAX)\", \"Nullable\": true }"));

        Assert.That(ScalarString("SELECT ds.name FROM sys.tables t "
                                 + "JOIN sys.data_spaces ds ON ds.data_space_id = t.lob_data_space_id "
                                 + "WHERE t.name = 'LobPlaced'"),
            Is.EqualTo("LobFg"),
            "TEXTIMAGE_ON is the third filegroup clause alongside ON and FILESTREAM_ON, both of which are "
            + "already honoured");
    }

    [Test]
    public void TextImageFileGroup_OnATableWithNoLargeObjectColumn_IsRefusedByName()
    {
        // SQL Server rejects TEXTIMAGE_ON with error 1709 on a table that has no LOB column, and that
        // message names neither the table nor the property. Emitting it unconditionally would break every
        // non-LOB table in a package that set it at the template level.
        var json = "[{ \"Schema\": \"[dbo]\", \"Name\": \"[NoLob]\", \"TextImageFileGroup\": \"[LobFg]\", \"Columns\": ["
                   + " { \"Name\": \"[Id]\", \"DataType\": \"INT\", \"Nullable\": false } ], \"Indexes\": ["
                   + " { \"Name\": \"[PK_NoLob]\", \"IndexColumns\": \"[Id]\", \"PrimaryKey\": true, \"Unique\": true } ] }]";

        var ex = Assert.Catch(() => Deploy(json));

        Assert.That(ex, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(ex.Message, Does.Contain("NoLob"), "the message must name the table. " + ex.Message);
            Assert.That(ex.Message, Does.Contain("TextImageFileGroup"), "and the property");
        });
    }

    [Test]
    public void ChangingAPlacementFileGroup_OnADeployedTable_IsRefused()
    {
        // Neither clause has an ALTER -- placement is fixed at CREATE -- so a changed declaration is
        // refused by name rather than silently ignored, the same posture FileGroup takes.
        Deploy(PackageOn("FsMove", "\"UniqueConstraint\": true", ", \"FileStreamFileGroup\": \"[FsFg]\""));

        var ex = Assert.Catch(() => Deploy(PackageOn("FsMove", "\"UniqueConstraint\": true",
            ", \"FileStreamFileGroup\": \"[FsFg2]\"")));

        Assert.That(ex, Is.Not.Null, "silently leaving the table where it is would be the worse outcome");
        Assert.That(ex.Message, Does.Contain("FsMove"), ex.Message);
    }

    [Test]
    public void BothPlacementFileGroups_RoundTripThroughExtraction()
    {
        // The round trip is the assertion that matters most here: FileStreamFileGroup was EXTRACT-ONLY --
        // read back faithfully while doing nothing on deploy -- which is worse than not supporting it,
        // because the package reads as though placement is managed. Extraction alone proves nothing.
        Deploy(PackageOn("FsRound", "\"UniqueConstraint\": true",
            ", \"FileStreamFileGroup\": \"[FsFg2]\", \"TextImageFileGroup\": \"[LobFg]\"",
            ", { \"Name\": \"[Notes]\", \"DataType\": \"NVARCHAR(MAX)\", \"Nullable\": true }"));

        var json = ExtractTable("FsRound");

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("FsFg2"),
                "an extracted package that drops the FILESTREAM filegroup re-deploys the table onto the "
                + "default one. " + json);
            Assert.That(json, Does.Contain("LobFg"),
                "and the same for large-object placement. " + json);
        });
    }

    /// <summary>
    /// Extraction goes through whichever encoding this server uses -- the newest Windows instance
    /// available here is 2016, so in practice that is the XML tier, which is exactly the half a
    /// modern-container test cannot reach.
    /// </summary>
    private string ExtractTable(string table)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = _encoding == IngestEncoding.Xml
            ? $"EXEC SchemaSmith.GenerateTableXml @p_Schema = 'dbo', @p_Table = '{table}'"
            : $"EXEC SchemaSmith.GenerateTableJson @p_Schema = 'dbo', @p_Table = '{table}'";
        using var reader = cmd.ExecuteReader();
        var sb = new System.Text.StringBuilder();
        while (reader.Read())
            if (!reader.IsDBNull(0)) sb.Append(reader.GetString(0));
        return sb.ToString();
    }

    private static string PackageOn(string table, string guidIndexKind, string placement, string extraColumn = "") =>
        "[{ \"Schema\": \"[dbo]\", \"Name\": \"[" + table + "]\"" + placement + ", \"Columns\": ["
        + " { \"Name\": \"[Id]\", \"DataType\": \"INT\", \"Nullable\": false },"
        + " { \"Name\": \"[G]\", \"DataType\": \"UNIQUEIDENTIFIER ROWGUIDCOL\", \"Nullable\": false, \"Default\": \"NEWID()\" },"
        + " { \"Name\": \"[Doc]\", \"DataType\": \"VARBINARY(MAX)\", \"Nullable\": true, \"FileStream\": true }" + extraColumn + " ], \"Indexes\": ["
        + " { \"Name\": \"[PK_" + table + "]\", \"IndexColumns\": \"[Id]\", \"PrimaryKey\": true, \"Unique\": true },"
        + " { \"Name\": \"[UQ_" + table + "_G]\", \"IndexColumns\": \"[G]\", " + guidIndexKind + " } ] }]";

}
