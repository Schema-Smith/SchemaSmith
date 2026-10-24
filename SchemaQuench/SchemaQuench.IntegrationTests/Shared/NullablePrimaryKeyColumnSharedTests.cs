// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.IO;
using NUnit.Framework;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// A primary-key column declared Nullable failed the deploy on every engine (PostgreSQL on every deploy after the
/// first). It now deploys NOT NULL, with a warning ahead of the table step so a column whose existing NULLs refuse the
/// change fails with its reason in view.
/// </summary>
[NonParallelizable]
public abstract class NullablePrimaryKeyColumnSharedTests : ObjectScriptDeployFixture
{
    protected sealed override string ProductName => "NullableKeyProduct";

    private const string Table = "nullable_key_probe";

    /// <summary>The identifier as the engine's packages write it; it also serves in plain SQL, so PostgreSQL leaves the lower-case name bare.</summary>
    protected abstract string Quote(string name);

    /// <summary>The key's name: MySQL and MariaDB call every primary key PRIMARY.</summary>
    protected abstract string PrimaryKeyName { get; }

    /// <summary>The schema the template's tables land in, for INFORMATION_SCHEMA.</summary>
    protected abstract string TableSchema { get; }

    [SetUp]
    [OneTimeTearDown]
    public void DropTable() => Exec($"DROP TABLE IF EXISTS {Quote(Table)}");

    private void WriteTable()
    {
        WritePackage("");
        WriteFile("Tables", $"{Table}.json", $$"""
            { "Name": "{{Quote(Table)}}",
              "Columns": [ { "Name": "{{Quote("code")}}", "DataType": "VARCHAR(20)", "Nullable": true },
                           { "Name": "{{Quote("note")}}", "DataType": "VARCHAR(20)", "Nullable": true } ],
              "Indexes": [ { "Name": "{{PrimaryKeyName}}", "PrimaryKey": true, "Unique": true, "IndexColumns": "{{Quote("code")}}" } ] }
            """);
    }

    private string IsNullable(string column) => (string)Scalar(
        $"SELECT IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = '{TableSchema}' AND TABLE_NAME = '{Table}' AND COLUMN_NAME = '{column}'");

    [Test]
    public void ANullableKeyColumn_DeploysNotNull_AndTheNextDeployWorksToo()
    {
        WriteTable();

        var first = Deploy();
        Deploy();

        Assert.Multiple(() =>
        {
            Assert.That(IsNullable("code"), Is.EqualTo("NO"), "the key column is NOT NULL");
            Assert.That(IsNullable("note"), Is.EqualTo("YES"), "a column outside the key keeps its declaration");
            var warning = first.Log.FindIndex(l => l != null && l.StartsWith("WARN") && l.Contains("of table") && l.Contains("so it is deployed NOT NULL"));
            var tables = first.Log.FindIndex(l => l != null && l.Contains("Quenching missing tables and columns"));
            Assert.That(warning, Is.GreaterThanOrEqualTo(0).And.LessThan(tables), "the warning comes before the table step: " + string.Join(" | ", first.Log));
        });
    }

    [Test]
    public void ExistingNullsInTheKeyColumn_FailTheDeploy_AfterTheWarningThatExplainsIt()
    {
        Exec($"CREATE TABLE {Quote(Table)} ({Quote("code")} VARCHAR(20) NULL, {Quote("note")} VARCHAR(20) NULL)");
        Exec($"INSERT INTO {Quote(Table)} ({Quote("code")}, {Quote("note")}) VALUES (NULL, 'x')");
        WriteTable();

        var run = Deploy(expectSuccess: false);

        Assert.Multiple(() =>
        {
            Assert.That(IsNullable("code"), Is.EqualTo("YES"), "the change was refused");
            var warning = run.Log.FindIndex(l => l != null && l.StartsWith("WARN") && l.Contains("so it is deployed NOT NULL"));
            var error = run.Log.FindIndex(l => l != null && l.StartsWith("ERROR"));
            Assert.That(error, Is.GreaterThanOrEqualTo(0), "the deploy failed: " + string.Join(" | ", run.Log));
            Assert.That(warning, Is.GreaterThanOrEqualTo(0).And.LessThan(error), "the warning comes first: " + string.Join(" | ", run.Log));
        });
    }
}
