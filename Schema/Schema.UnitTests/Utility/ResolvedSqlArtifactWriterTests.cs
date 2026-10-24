// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Schema.Utility;

namespace Schema.UnitTests.Utility;

[TestFixture]
public class ResolvedSqlArtifactWriterTests
{
    private static readonly string Owner = ResolvedSqlArtifactWriter.OwnerLine("Shop", "srv", "db", "");

    [Test]
    public void BuildArtifact_IncludesHeader_AndAllBatches_FailingBatchMarked()
    {
        var text = ResolvedSqlArtifactWriter.BuildArtifact(Owner,
            header: "Failed: srv.db [script 001.sql] — division by zero",
            batches: new List<string> { "CREATE TABLE A(...);", "SELECT 1/0;" },
            failingBatchIndex: 1);

        Assert.That(text, Does.Contain("Failed: srv.db"));
        Assert.That(text, Does.Contain("CREATE TABLE A"));
        Assert.That(text, Does.Contain("SELECT 1/0"));
        Assert.That(text, Does.Contain("FAILING BATCH"));
    }

    // #576: the file holds the real values, says so, and names who wrote it -- the line a later run reads to decide
    // whether the file is its to delete.
    [Test]
    public void BuildArtifact_OpensWithTheOwner_AndSaysItHoldsRealValues()
    {
        var text = ResolvedSqlArtifactWriter.BuildArtifact(Owner, "Failed", new List<string> { "SELECT 1;" }, 0);
        var lines = text.Replace("\r", "").Split('\n');

        Assert.Multiple(() =>
        {
            Assert.That(lines[0], Is.EqualTo($"-- SchemaSmith debug SQL | Product: Shop | Server: srv | Database: db | Schema:  | Run: {ResolvedSqlArtifactWriter.RunId}"));
            Assert.That(lines[1], Does.Contain("real values").And.Contain("Do not attach"));
            Assert.That(ResolvedSqlArtifactWriter.RunId, Does.Match("^[0-9a-f]{32}$"));
        });
    }

    [Test]
    public void WithOwner_PutsTheOwnerAndRealValuesLinesInFrontOfTheSql()
    {
        var text = ResolvedSqlArtifactWriter.WithOwner(Owner, "EXEC SchemaSmith.TableQuench ...");
        var lines = text.Replace("\r", "").Split('\n');

        Assert.That(lines, Is.EqualTo(new[] { Owner, lines[1], "EXEC SchemaSmith.TableQuench ..." }));
        Assert.That(lines[1], Does.Contain("real values"));
    }

    [Test]
    public void BuildArtifact_HeaderWithEmbeddedNewlines_StaysFullyCommented()
    {
        var text = ResolvedSqlArtifactWriter.BuildArtifact(Owner,
            header: "Failed: x — ERROR: relation does not exist\n\nPOSITION: 87",
            batches: new List<string> { "SELECT 1;" },
            failingBatchIndex: 0);

        // Every line of the header block must be a SQL comment — no bare line may escape.
        // Specifically, "POSITION: 87" must NOT appear on a line that doesn't start with --.
        foreach (var line in text.Replace("\r", "").Split('\n'))
            Assert.That(line == "" || line.StartsWith("--") || !line.Contains("POSITION"),
                $"Un-commented header fragment leaked into artifact body: '{line}'");

        // And stripping comment lines + GO must NOT leave any POSITION fragment in the executable SQL.
        var executable = string.Join("\n",
            text.Replace("\r", "").Split('\n')
                .Where(l => !l.StartsWith("--") && l.Trim() != "GO"));
        Assert.That(executable, Does.Not.Contain("POSITION"));
        Assert.That(executable, Does.Contain("SELECT 1"));
    }

    [Test]
    public void WriteFailureArtifact_WritesTheRealValues_AndReturnsWrittenPath()
    {
        var tempDir = Directory.CreateTempSubdirectory("ss_artifact_").FullName;
        var fileName = "artifact.sql";

        try
        {
            var returnedPath = ResolvedSqlArtifactWriter.WriteFailureArtifact(
                directory: tempDir,
                owner: Owner,
                header: "Failed: srv.db [script 001.sql] — division by zero",
                batches: new List<string> { "CREATE LOGIN x WITH PASSWORD = 'hunter2secret';" },
                failingBatchIndex: 0,
                fileName: fileName);

            Assert.That(Path.GetDirectoryName(returnedPath), Is.EqualTo(tempDir));
            Assert.That(Path.GetFileName(returnedPath), Is.EqualTo(fileName));
            var written = File.ReadAllText(returnedPath);
            Assert.That(written, Does.Contain("hunter2secret"), "never scrubbed: the file exists to reproduce the failure");
            Assert.That(written, Does.StartWith(Owner));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public void WriteFailureArtifact_LabelSanitizingOverload_SanitizesLabelIntoFileName_AndWritesContent()
    {
        var dir = Path.Join(Path.GetTempPath(), $"rsaw_{Guid.NewGuid():N}");
        try
        {
            var path = ResolvedSqlArtifactWriter.WriteFailureArtifact(
                dir, Owner,
                header: "Failed data delivery: srv.db [dbo.Orders#EU/1]",
                batches: new List<string> { "SELECT 1" }, failingBatchIndex: 0,
                label: "dbo.Orders#EU/1",
                fileNameFromSafeLabel: safe => $"Failed DataDelivery {safe}.sql");

            Assert.That(Path.GetFileName(path), Does.Not.Contain("/"),
                "The '/' in the label must be sanitized out of the filename.");
            Assert.That(File.Exists(path), Is.True);
            Assert.That(File.ReadAllText(path), Does.Contain("SELECT 1"));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Test]
    public void Write_CreatesDirectory_WhenMissing()
    {
        // Use a fresh GUID subdir under temp — guaranteed not to exist before this call.
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var fileName = "artifact.sql";
        var content = "-- test artifact";

        try
        {
            Assert.That(Directory.Exists(tempDir), Is.False, "Precondition: directory must not exist before Write");

            var returnedPath = ResolvedSqlArtifactWriter.Write(tempDir, fileName, content);

            Assert.That(File.Exists(returnedPath), Is.True, "Write must create the file in the missing directory");
            Assert.That(returnedPath, Is.EqualTo(Path.Combine(tempDir, fileName)));
            Assert.That(File.ReadAllText(returnedPath), Is.EqualTo(content));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public void ReadOwner_ReadsBackEveryField_AndIgnoresALineThatIsNotAnOwnerLine()
    {
        var owner = ResolvedSqlArtifactWriter.ReadOwner(ResolvedSqlArtifactWriter.OwnerLine("Shop", "srv", "db", "tenant_a") + "\r");

        Assert.Multiple(() =>
        {
            Assert.That(owner, Is.EqualTo(new ResolvedSqlArtifactWriter.Owner("Shop", "srv", "db", "tenant_a", ResolvedSqlArtifactWriter.RunId)));
            Assert.That(ResolvedSqlArtifactWriter.ReadOwner("-- ============"), Is.Null);
            Assert.That(ResolvedSqlArtifactWriter.ReadOwner("SELECT 1"), Is.Null);
            Assert.That(ResolvedSqlArtifactWriter.ReadOwner(null), Is.Null);
        });
    }

    // #576: a run deletes only what it can prove a run wrote for the same product and target -- never a file without the
    // owner line (an older version's, or the user's own), never another product's or another database's.
    [Test]
    public void DeleteOwned_DeletesOnlyTheMatchingOwnedFiles()
    {
        var dir = Directory.CreateTempSubdirectory("ss_owned_").FullName;
        try
        {
            void Make(string name, string firstLine) => File.WriteAllText(Path.Join(dir, name), firstLine + Environment.NewLine + "SELECT 1;");
            const string otherRun = "00000000000000000000000000000000";
            Make("SchemaQuench - Quench Indexes srv.db.sql", $"-- SchemaSmith debug SQL | Product: Shop | Server: srv | Database: db | Schema:  | Run: {otherRun}");
            Make("SchemaQuench - Quench Indexes srv.other.sql", $"-- SchemaSmith debug SQL | Product: Shop | Server: srv | Database: other | Schema:  | Run: {otherRun}");
            Make("SchemaQuench - Quench Indexes srv.db.2.sql", $"-- SchemaSmith debug SQL | Product: Billing | Server: srv | Database: db | Schema:  | Run: {otherRun}");
            Make("SchemaQuench - Old srv.db.sql", "-- ============================================================");
            Make("SchemaQuench - Mine srv.db.sql", ResolvedSqlArtifactWriter.OwnerLine("Shop", "srv", "db", ""));
            Make("notes.sql", $"-- SchemaSmith debug SQL | Product: Shop | Server: srv | Database: db | Schema:  | Run: {otherRun}");

            ResolvedSqlArtifactWriter.DeleteOwned(dir, o => o.Product == "Shop" && o.Server == "srv" && o.Database == "db"
                                                             && o.Schema == "" && o.Run != ResolvedSqlArtifactWriter.RunId);

            Assert.That(Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(n => n), Is.EqualTo(new[]
            {
                "notes.sql", "SchemaQuench - Mine srv.db.sql", "SchemaQuench - Old srv.db.sql",
                "SchemaQuench - Quench Indexes srv.db.2.sql", "SchemaQuench - Quench Indexes srv.other.sql"
            }.OrderBy(n => n)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void DeleteOwned_AMissingDirectory_IsNothingToDo()
    {
        Assert.DoesNotThrow(() => ResolvedSqlArtifactWriter.DeleteOwned(Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N")), _ => true));
    }
}
