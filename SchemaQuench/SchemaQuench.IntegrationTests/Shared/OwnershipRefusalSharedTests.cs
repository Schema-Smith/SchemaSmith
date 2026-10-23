// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// A deploy refused because another product owns a declared table must leave that table as it found it. The refusal
/// used to come from the modified-table step, after the missing-table step had already renamed the other product's
/// columns and added the refused product's ones.
/// </summary>
[NonParallelizable]
public abstract class OwnershipRefusalSharedTests : ObjectScriptDeployFixture
{
    private const string Owner = "OwnershipRefusalOwner";
    private const string Intruder = "OwnershipRefusalIntruder";
    private const string Table = "ownership_refusal_probe";
    private const string Renamed = "ownership_refusal_renamed";

    private string _productName = Owner;
    protected sealed override string ProductName => _productName;

    /// <summary>The identifier as the engine's packages write it; it also serves in plain SQL, so PostgreSQL leaves the lower-case name bare.</summary>
    protected abstract string Quote(string name);

    /// <summary>The schema the template's tables land in, for INFORMATION_SCHEMA.</summary>
    protected abstract string TableSchema { get; }

    /// <summary>Removes both products' ownership rows, where the engine keeps them outside the table.</summary>
    protected abstract void ClearOwnership(string first, string second);

    /// <summary>An index over an expression that reads the column, as a JSON index entry, where the engine drops one ahead of a column rename.</summary>
    protected virtual string ExpressionIndex(string column) => "";

    /// <summary>How many indexes named ix_lower the table has, where <see cref="ExpressionIndex"/> declares one.</summary>
    protected virtual long ExpressionIndexCount() => 0;

    [SetUp]
    [OneTimeTearDown]
    public void DropTableAndOwnership()
    {
        Exec($"DROP TABLE IF EXISTS {Quote(Table)}");
        Exec($"DROP TABLE IF EXISTS {Quote(Renamed)}");
        ClearOwnership(Owner, Intruder);
        _productName = Owner;
    }

    private void WriteTable(string product, string columns, string indexes, string name = Table, string oldName = null)
    {
        _productName = product;
        WritePackage("");
        WriteFile("Tables", $"{name}.json", $$"""
            { "Name": "{{Quote(name)}}",{{(oldName == null ? "" : $" \"OldName\": \"{Quote(oldName)}\",")}}
              "Columns": [ { "Name": "{{Quote("id")}}", "DataType": "INT", "Nullable": false }{{columns}} ],
              "Indexes": [ { "Name": "{{Quote("ix_id")}}", "IndexColumns": "{{Quote("id")}}" }{{indexes}} ] }
            """);
    }

    private long TableCount(string table) => Count(
        $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = '{TableSchema}' AND TABLE_NAME = '{table}'");

    private long ColumnCount(string column) => Count(
        $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = '{TableSchema}' AND TABLE_NAME = '{Table}' AND COLUMN_NAME = '{column}'");

    [Test]
    public void ADeployRefusedForAnotherProductsTable_LeavesThatTableAsItWas()
    {
        WriteTable(Owner, $$""", { "Name": "{{Quote("email")}}", "DataType": "VARCHAR(100)", "Nullable": true }""", ExpressionIndex("email"));
        Deploy();
        var expressionIndexes = ExpressionIndexCount();

        WriteTable(Intruder,
            $$""", { "Name": "{{Quote("mail")}}", "OldName": "{{Quote("email")}}", "DataType": "VARCHAR(100)", "Nullable": true }""" +
            $$""", { "Name": "{{Quote("extra")}}", "DataType": "INT", "Nullable": true }""",
            ExpressionIndex("mail"));
        var refused = Deploy(expectSuccess: false);

        Assert.Multiple(() =>
        {
            Assert.That(refused.Log, Has.Some.Contains("owned by"), "the deploy is refused for the ownership: " + string.Join(" | ", refused.Log));
            Assert.That(ColumnCount("email"), Is.EqualTo(1), "the owner's column keeps its name");
            Assert.That(ColumnCount("mail"), Is.Zero, "the refused rename is not applied");
            Assert.That(ColumnCount("extra"), Is.Zero, "the refused product's column is not added");
            Assert.That(ExpressionIndexCount(), Is.EqualTo(expressionIndexes), "the owner's expression index is still there");
        });
    }

    // The check looked the table up by its declared name only, which a rename has not created yet: the other product's
    // table was renamed and converged, and only the next deploy was refused.
    [Test]
    public void ADeployRefusedForAnotherProductsTable_DeclaredUnderANewName_LeavesItUnrenamed()
    {
        WriteTable(Owner, "", "");
        Deploy();

        WriteTable(Intruder, "", "", Renamed, Table);
        var refused = Deploy(expectSuccess: false);

        Assert.Multiple(() =>
        {
            Assert.That(refused.Log, Has.Some.Contains("owned by"), "the deploy is refused for the ownership: " + string.Join(" | ", refused.Log));
            Assert.That(TableCount(Table), Is.EqualTo(1), "the owner's table keeps its name");
            Assert.That(TableCount(Renamed), Is.Zero, "the refused rename is not applied");
        });
    }
}
