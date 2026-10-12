// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using System.Text;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.SqlServer;

/// <summary>
/// Declaring a SQL Server ledger table — gap item J3.
/// <para>Structurally this is temporal's shape: the engine generates a history table and a view from one
/// declaration. Keeping those out of extracted packages shipped separately as
/// <see href="https://github.com/Schema-Smith/SchemaSmith/issues/403">#403</see>, which turned out to be
/// four artefact kinds rather than one.</para>
/// <para><b>Create-time only, verified against the engine:</b> <c>ALTER TABLE … SET (LEDGER = ON)</c> is
/// error 102 — not syntax at all — so a table cannot become a ledger table after the fact, and changing
/// <c>Ledger</c> on a deployed table is refused by name.</para>
/// <para><b>And the drop is not a drop.</b> <c>DROP TABLE</c> on a ledger table succeeds but renames it to
/// <c>MSSQL_DroppedLedgerTable_&lt;name&gt;_&lt;guid&gt;</c> and keeps it. So drop-by-absence would report
/// a table removed while the data stayed — which is why it is refused rather than issued.</para>
/// </summary>
[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public class LedgerTableDeployTests
{
    private IDbConnection _connection;
    private string _db;
    private bool _supported;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _db = $"SchemaLedger_{Guid.NewGuid():N}"[..40];
        _connection = DbConnectionFactory.ForPlatform(Platform.SqlServer)
            .GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        _connection.Open();

        _connection.ChangeDatabase("master");
        Exec($"CREATE DATABASE [{_db}]");
        _connection.ChangeDatabase(_db);

        using var cmd = _connection.CreateCommand();
        ForgeKindler.KindleTheForge(cmd, Platform.SqlServer);

        _supported = Scalar("SELECT COUNT(*) FROM sys.all_columns "
                            + "WHERE object_id = OBJECT_ID('sys.tables') AND name = 'ledger_type'") > 0;
    }

    [SetUp]
    public void RequireLedger()
    {
        if (!_supported)
            Assert.Ignore("Ledger tables need SQL Server 2022 (major 16); nothing here applies below it.");
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        if (_connection == null) return;
        try
        {
            _connection.ChangeDatabase("master");
            using var cmd = _connection.CreateCommand();
            cmd.CommandTimeout = 300;
            TestDatabaseDrop.Drop(cmd, _db);
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
        return cmd.ExecuteScalar() as string ?? "";
    }

    private void Deploy(string json)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = "EXEC SchemaSmith.TableQuench @ProductName = 'LedgerTest', "
                          + $"@TableDefinitions = N'{json.Replace("'", "''")}'";
        cmd.ExecuteNonQuery();
    }

    private static string Package(string table, string ledger, bool temporal = false) =>
        "[{ \"Schema\": \"[dbo]\", \"Name\": \"[" + table + "]\""
        + (ledger == null ? "" : ", \"Ledger\": \"" + ledger + "\"")
        + (temporal ? ", \"IsTemporal\": true" : "")
        + ", \"Columns\": [ { \"Name\": \"[Id]\", \"DataType\": \"INT\", \"Nullable\": false },"
        + " { \"Name\": \"[Bal]\", \"DataType\": \"DECIMAL(18,2)\", \"Nullable\": true } ], \"Indexes\": ["
        + " { \"Name\": \"[PK_" + table + "]\", \"IndexColumns\": \"[Id]\", \"PrimaryKey\": true, \"Unique\": true } ] }]";

    private string LedgerKind(string table) =>
        ScalarString($"SELECT ledger_type_desc FROM sys.tables WHERE name = '{table}'");

    [Test]
    public void DeclaringAppendOnly_CreatesAnAppendOnlyLedgerTable()
    {
        Deploy(Package("LAppend", "AppendOnly"));

        Assert.That(LedgerKind("LAppend"), Is.EqualTo("APPEND_ONLY_LEDGER_TABLE"),
            "deploying it as an ordinary table would be a green run that silently ignored the declaration");
    }

    [Test]
    public void DeclaringUpdatable_CreatesAnUpdatableLedgerTable()
    {
        Deploy(Package("LUpdate", "Updatable"));

        Assert.Multiple(() =>
        {
            Assert.That(LedgerKind("LUpdate"), Is.EqualTo("UPDATABLE_LEDGER_TABLE"));
            Assert.That(Scalar("SELECT COUNT(*) FROM sys.tables WHERE name = 'LUpdate' "
                               + "AND history_table_id IS NOT NULL"), Is.EqualTo(1),
                "an updatable ledger table gets an engine-generated history table -- which #403 keeps out "
                + "of extracted packages");
        });
    }

    [Test]
    public void NotDeclaringLedger_CreatesAnOrdinaryTable()
    {
        // The negative half. Without it, an emit that added the clause unconditionally would pass every
        // assertion above while turning every table in every package into a ledger table -- and those
        // cannot be dropped afterwards.
        Deploy(Package("LPlain", null));

        Assert.That(LedgerKind("LPlain"), Is.EqualTo("NON_LEDGER_TABLE"));
    }

    [Test]
    public void ALedgerTable_IsIdempotent()
    {
        // The second deploy is the one that finds bugs, and here it is unusually important: ledger tables
        // cannot be dropped and recreated to recover, so a package that churns them is stuck.
        Deploy(Package("LTwice", "AppendOnly"));
        Deploy(Package("LTwice", "AppendOnly"));

        Assert.That(LedgerKind("LTwice"), Is.EqualTo("APPEND_ONLY_LEDGER_TABLE"));
    }

    [Test]
    public void ALedgerTable_RoundTripsThroughExtraction()
    {
        Deploy(Package("LRoundTrip", "AppendOnly"));

        using var cmd = _connection.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = "EXEC SchemaSmith.GenerateTableJSON @p_Schema = 'dbo', @p_Table = 'LRoundTrip'";
        using var reader = cmd.ExecuteReader();
        var sb = new StringBuilder();
        while (reader.Read())
            if (!reader.IsDBNull(0)) sb.Append(reader.GetString(0));
        var json = sb.ToString();

        Assert.That(json, Does.Contain("\"Ledger\": \"AppendOnly\"").IgnoreCase,
            "an extracted package that drops Ledger re-deploys the table as an ordinary one, and the "
            + "original cannot be dropped to correct it.\n" + json);
    }

    [Test]
    public void ChangingLedger_OnADeployedTable_IsRefusedByName()
    {
        Deploy(Package("LChange", null));

        var ex = Assert.Catch(() => Deploy(Package("LChange", "AppendOnly")));

        Assert.That(ex, Is.Not.Null, "silently leaving the table ordinary would be the worse outcome");
        Assert.Multiple(() =>
        {
            Assert.That(ex.Message, Does.Contain("LChange"), "the message must name the table");
            Assert.That(ex.Message, Does.Contain("Ledger").IgnoreCase, "and the property");
        });
    }

    [Test]
    public void DeclaringBothLedgerAndIsTemporal_IsRefused()
    {
        // An updatable ledger table is created WITH (SYSTEM_VERSIONING = ON, LEDGER = ON), and IsTemporal
        // turns system versioning on separately -- so the two declarations describe overlapping, and
        // partly contradictory, engine state. sys.tables reports a ledger table as NON_TEMPORAL_TABLE, so
        // letting both through would leave the package permanently disagreeing with the target.
        var ex = Assert.Catch(() => Deploy(Package("LBoth", "Updatable", temporal: true)));

        Assert.That(ex, Is.Not.Null);
        Assert.That(ex.Message, Does.Contain("LBoth"), "the message must name the table");
    }
}
