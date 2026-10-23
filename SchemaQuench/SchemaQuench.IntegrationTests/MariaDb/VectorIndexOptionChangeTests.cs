// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.IO;
using System.Linq;
using NUnit.Framework;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.MariaDb;

/// <summary>
/// #561: a deployed VECTOR index whose declared M or DISTANCE changed is rebuilt with the new options. MariaDB has no ALTER
/// for either and reports them only in SHOW CREATE TABLE, so the change went unnoticed and the index kept its old options.
/// </summary>
[Category("MariaDb")]
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public class VectorIndexOptionChangeTests : ObjectScriptDeployFixture
{
    protected override Platform Platform => Platform.MariaDb;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => FixtureSetup.GetMainDbConnectionString();
    protected override string ConfigPrefix => "MariaDB";
    protected override string ProductName => "VectorIndexOptionChangeProduct";

    private const string Table = "vec_option_change";

    [SetUp]
    public void RequireVectorIndexes()
    {
        var parts = (Scalar("SELECT VERSION()")?.ToString() ?? "").Split('.', '-');
        if (parts.Length < 2 || int.Parse(parts[0]) * 100 + int.Parse(parts[1]) < 1107)
            Assert.Ignore("Vector indexes arrived in MariaDB 11.7.");
        Exec($"DROP TABLE IF EXISTS `{Table}`");
    }

    [OneTimeTearDown]
    public void DropTable() => Exec($"DROP TABLE IF EXISTS `{Table}`");

    private void WriteTable(string options, string tableOptions = "")
    {
        WritePackage("""{ "FolderPath": "Before Scripts", "QuenchSlot": "Before" }""", "Before Scripts");
        File.WriteAllText(Path.Join(TemplateDir, "Tables", $"{Table}.json"), $$"""
            {
                "Name": "`{{Table}}`",{{tableOptions}}
                "Columns": [
                    { "Name": "`id`", "DataType": "INT", "Nullable": false },
                    { "Name": "`v`", "DataType": "VECTOR(3)", "Nullable": false }
                ],
                "Indexes": [
                    { "Name": "`PRIMARY`", "PrimaryKey": true, "Unique": true, "IndexColumns": "`id`" },
                    { "Name": "`vx`", "IndexType": "VECTOR", "IndexColumns": "`v`"{{options}} }
                ]
            }
            """);
    }

    private string CreateTable()
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = $"SHOW CREATE TABLE `{Table}`";
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? reader.GetString(1) : "";
    }

    private static string IndexActions(DeployResult result) =>
        string.Join(",", result.Audit.Where(r => r.ObjectType == "index" && r.ObjectName.EndsWith(".vx")).Select(r => r.Action));

    [Test]
    public void AChangedMOrDistance_RebuildsTheIndex_AndAnUnchangedOneIsLeftAlone()
    {
        WriteTable(""", "M": 8, "Distance": "cosine" """);
        Deploy();
        Assert.That(CreateTable(), Does.Contain("`M`='8'").And.Contain("`DISTANCE`='cosine'"));

        WriteTable(""", "M": 12, "Distance": "euclidean" """);
        var changed = Deploy();
        Assert.Multiple(() =>
        {
            Assert.That(CreateTable(), Does.Contain("`M`='12'").And.Contain("`DISTANCE`='euclidean'"), "the index carries the options it now declares");
            Assert.That(IndexActions(changed), Is.EqualTo("dropped,created"), changed.DescribeAudit());
        });

        var again = Deploy();
        Assert.That(IndexActions(again), Is.Empty, "a redeploy of the same options leaves the index alone: " + again.DescribeAudit());
    }

    // The rebuild read every declared VECTOR index, gated or not, and dropped one gated off for this target even where
    // the package keeps indexes it does not declare here (DropIndexesRemovedFromProduct false): the index step then
    // skipped it, so it was gone.
    [Test]
    public void AGatedOffIndex_WhoseOptionsChanged_IsLeftAsItIs()
    {
        WriteTable(""", "M": 8, "Distance": "cosine" """);
        Deploy();

        WriteTable(""", "M": 12, "Distance": "euclidean", "ShouldApplyExpression": "SELECT 0" """, """ "DropIndexesRemovedFromProduct": false,""");
        var gated = Deploy();

        Assert.Multiple(() =>
        {
            Assert.That(CreateTable(), Does.Contain("`M`='8'").And.Contain("`DISTANCE`='cosine'"), "the index is still there, as it was");
            Assert.That(IndexActions(gated), Does.Not.Contain("dropped"), gated.DescribeAudit());
        });
    }

    [Test]
    public void UnderWhatIf_AChangedOptionIsReported_AndTheIndexIsLeftAsItIs()
    {
        WriteTable(""", "M": 8 """);
        Deploy();

        WriteTable(""", "M": 16 """);
        var preview = Deploy(whatIf: true);
        Assert.Multiple(() =>
        {
            Assert.That(CreateTable(), Does.Contain("`M`='8'"));
            Assert.That(preview.Log.Any(l => l != null && l.Contains("Would rebuild VECTOR index") && l.Contains("vx")), Is.True, string.Join(" | ", preview.Log));
            Assert.That(IndexActions(preview), Is.EqualTo("wouldRebuild"), preview.DescribeAudit());
        });
    }
}
