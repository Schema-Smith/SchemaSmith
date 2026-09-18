// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using System.Linq;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;

namespace Schema.IntegrationTests.Shared;

/// <summary>
/// The expression mapping table (#242), kindled on every engine. It records, per expression slot, the text the
/// package authored and the text the engine reported straight after applying it — so the next deploy can ask
/// "did either side change?" instead of comparing an authored expression against the engine's rewrite of it,
/// which is never equal and is why an expression-bearing object is dropped and recreated on every deploy.
/// <para><b>Why its own table.</b> <c>ProductOwnership</c> is not universal (SQL Server holds only
/// memory-optimized tables there) and <c>ChangeAudit</c> deletes its own rows as it is drained, so neither can
/// carry a durable per-object mapping.</para>
/// <para><b>Key shape.</b> One row per (schema, table, object kind, object name, slot). The slot is part of the
/// key because one column carries more than one expression — a DEFAULT and a computed expression are different
/// slots on the same column. Every key column is NOT NULL and uses '' for "not applicable", deliberately: the
/// nullable-key trap that made <c>ProductOwnership</c> need NULLS NOT DISTINCT is designed out rather than
/// worked around.</para>
/// </summary>
public abstract class ExpressionMapKindlingSharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string MainDb { get; }
    protected abstract string MainConnectionString { get; }

    private IDbConnection _connection;
    private IDbCommand _command;

    private bool IsMySqlFamily => Platform.GetBasePlatform() == Platform.MySQL;
    private string MapTable => IsMySqlFamily ? "SchemaSmith_ExpressionMap"
        : Platform == Platform.SqlServer ? "SchemaSmith.ExpressionMap" : "\"SchemaSmith\".\"ExpressionMap\"";

    [SetUp]
    public void SetUp()
    {
        _connection = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(MainConnectionString);
        _connection.Open();
        _command = _connection.CreateCommand();
        DeleteProbeRows();
    }

    [TearDown]
    public void TearDown()
    {
        try { DeleteProbeRows(); }
        finally
        {
            _command?.Dispose();
            _connection?.Close();
            _connection?.Dispose();
        }
    }

    private void DeleteProbeRows()
    {
        _command.CommandText = $"DELETE FROM {MapTable} WHERE {Quote("ObjectTable")} = 'MapProbe'";
        _command.ExecuteNonQuery();
    }

    private string Quote(string identifier) => IsMySqlFamily ? identifier
        : Platform == Platform.SqlServer ? $"[{identifier}]" : $"\"{identifier}\"";

    private void InsertRow(string kind = "CHECK", string name = "CK_MapProbe_Qty", string slot = "expression",
        string authored = "Qty > 0", string canonical = "([Qty]>(0))")
    {
        var cols = string.Join(", ", new[] { "ObjectSchema", "ObjectTable", "ObjectKind", "ObjectName", "Slot",
            "AuthoredText", "CanonicalText", "PlatformName", "EngineVersion" }.Select(Quote));
        _command.CommandText = $@"INSERT INTO {MapTable} ({cols})
            VALUES ('dbo', 'MapProbe', '{kind}', '{name}', '{slot}', '{authored}', '{canonical}', 'x', '1')";
        _command.ExecuteNonQuery();
    }

    [Test]
    public void Kindling_CreatesTheExpressionMap()
    {
        _command.CommandText = $"SELECT COUNT(*) FROM {MapTable}";

        Assert.DoesNotThrow(() => _command.ExecuteScalar(),
            "kindling must create the expression map on every engine -- the comparison falls back to today's "
            + "behaviour without it, so a missing table is silent");
    }

    [Test]
    public void OneSlotHoldsOneMapping()
    {
        InsertRow();

        var ex = Assert.Catch<Exception>(() => InsertRow(canonical: "something else"));

        Assert.That(ex, Is.Not.Null,
            "a second row for the same (schema, table, kind, name, slot) must be rejected: two mappings for one "
            + "expression is an ambiguous answer to 'what did we apply?'");
    }

    // A column's DEFAULT and its computed expression are different slots on the same object, so both must fit.
    [Test]
    public void TwoSlotsOnOneObject_AreSeparateRows()
    {
        InsertRow(kind: "COLUMN", name: "Qty", slot: "default", authored: "0", canonical: "((0))");

        Assert.DoesNotThrow(() => InsertRow(kind: "COLUMN", name: "Qty", slot: "computed",
            authored: "Qty * 2", canonical: "([Qty]*(2))"));

        _command.CommandText = $"SELECT COUNT(*) FROM {MapTable} WHERE {Quote("ObjectTable")} = 'MapProbe'";
        Assert.That(Convert.ToInt32(_command.ExecuteScalar()), Is.EqualTo(2));
    }

    [Test]
    public void TheMappingSurvivesReKindling()
    {
        InsertRow();

        Schema.Utility.ForgeKindler.KindleTheForge(_command, Platform, forceReKindle: true);

        _command.CommandText = $"SELECT COUNT(*) FROM {MapTable} WHERE {Quote("ObjectTable")} = 'MapProbe'";
        Assert.That(Convert.ToInt32(_command.ExecuteScalar()), Is.EqualTo(1),
            "re-kindling must not drop what was recorded -- the mapping is the memory of what was applied");
    }
}
