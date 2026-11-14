// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.IO;
using NUnit.Framework;

namespace SchemaQuench.UnitTests;

/// <summary>
/// E4e: proves the PURE <see cref="ProductQuench.ResolveReportPaths"/> path-resolution helper —
/// the piece of the deployment-summary emit wiring that has no I/O, no config, no clock, and is
/// therefore unit-testable in isolation. The emit method itself (assembly + actual file writes +
/// abort-path wiring) is exercised end-to-end by the SchemaQuench integration suite.
/// </summary>
[TestFixture]
public class DeploymentSummaryEmitTests
{
    [Test]
    public void ResolveReportPaths_NullReportSwitch_DefaultsToLogDir()
    {
        var (jsonPath, mdPath) = ProductQuench.ResolveReportPaths(null, "/logs", "SchemaQuench");

        Assert.That(jsonPath, Is.EqualTo(Path.Join("/logs", "SchemaQuench - Summary.json")));
        Assert.That(mdPath, Is.EqualTo(Path.Join("/logs", "SchemaQuench - Summary.md")));
    }

    [Test]
    public void ResolveReportPaths_EmptyReportSwitch_DefaultsToLogDir()
    {
        var (jsonPath, mdPath) = ProductQuench.ResolveReportPaths(string.Empty, "/logs", "SchemaQuench");

        Assert.That(jsonPath, Is.EqualTo(Path.Join("/logs", "SchemaQuench - Summary.json")));
        Assert.That(mdPath, Is.EqualTo(Path.Join("/logs", "SchemaQuench - Summary.md")));
    }

    [Test]
    public void ResolveReportPaths_WhitespaceReportSwitch_DefaultsToLogDir()
    {
        var (jsonPath, mdPath) = ProductQuench.ResolveReportPaths("   ", "/logs", "SchemaQuench");

        Assert.That(jsonPath, Is.EqualTo(Path.Join("/logs", "SchemaQuench - Summary.json")));
        Assert.That(mdPath, Is.EqualTo(Path.Join("/logs", "SchemaQuench - Summary.md")));
    }

    [Test]
    public void ResolveReportPaths_ExplicitReportSwitch_RedirectsBothFiles()
    {
        var (jsonPath, mdPath) = ProductQuench.ResolveReportPaths("/custom/report", "/logs", "SchemaQuench");

        Assert.That(jsonPath, Is.EqualTo("/custom/report.json"));
        Assert.That(mdPath, Is.EqualTo("/custom/report.md"));
    }

    [Test]
    public void ResolveReportPaths_UsesSuppliedAppNameInDefaultFileNames()
    {
        var (jsonPath, mdPath) = ProductQuench.ResolveReportPaths(null, "/logs", "SchemaTongs");

        Assert.That(jsonPath, Is.EqualTo(Path.Join("/logs", "SchemaTongs - Summary.json")));
        Assert.That(mdPath, Is.EqualTo(Path.Join("/logs", "SchemaTongs - Summary.md")));
    }

    [Test]
    public void MakeScriptPathRelative_StripsProductRoot_AndNormalizesSeparators()
    {
        var full = string.Join(Path.DirectorySeparatorChar, "C:", "root", "Jobs", "SubFolder", "Job 2.sql");
        var baseDir = string.Join(Path.DirectorySeparatorChar, "C:", "root");

        var relative = ProductQuench.MakeScriptPathRelative(full, baseDir);

        Assert.That(relative, Is.EqualTo("Jobs/SubFolder/Job 2.sql"));
    }

    // The product root was removed wherever it occurred, not only in front: a package at "." lost every dot, so the
    // summary listed Templates/Main/v10sql, and run-once tracking keyed on the same mangled path.
    [TestCase("./Templates/Main/v1.0.sql", ".", "Templates/Main/v1.0.sql")]
    [TestCase("Templates/Main/v1.0.sql", ".", "Templates/Main/v1.0.sql")]
    [TestCase("pkg/Templates/pkg/a.sql", "pkg", "Templates/pkg/a.sql")]
    [TestCase("other/pkg/a.sql", "pkg", "other/pkg/a.sql")]
    public void MakeScriptPathRelative_RemovesTheRootOnlyInFront(string path, string baseDir, string expected)
    {
        Assert.That(ProductQuench.MakeScriptPathRelative(path, baseDir), Is.EqualTo(expected));
    }

    [Test]
    public void MakeScriptPathRelative_EmptyBaseDir_ReturnsNormalizedTrimmedPath()
    {
        var relative = ProductQuench.MakeScriptPathRelative("/root/Jobs/Job 1.sql", "");

        Assert.That(relative, Is.EqualTo("root/Jobs/Job 1.sql"));
    }

    [Test]
    public void EnsureReportDirectory_CreatesMissingParentDirectory()
    {
        var tempRoot = Path.Join(Path.GetTempPath(), $"SummaryDir_{System.Guid.NewGuid():N}");
        var reportFile = Path.Join(tempRoot, "nested", "sub", "deploy-summary.json");
        try
        {
            Assert.That(Directory.Exists(Path.GetDirectoryName(reportFile)), Is.False, "precondition: parent dir absent");
            ProductQuench.EnsureReportDirectory(reportFile);
            Assert.That(Directory.Exists(Path.GetDirectoryName(reportFile)), Is.True, "the --report parent directory must be created");
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Test]
    public void EnsureReportDirectory_NoDirectoryComponent_DoesNotThrow()
    {
        Assert.DoesNotThrow(() => ProductQuench.EnsureReportDirectory("summary.json"));
    }
}
