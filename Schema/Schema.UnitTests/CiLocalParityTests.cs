// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Schema.UnitTests;

/// <summary>
/// The local sweeps are the check run before a push, so they must run what CI runs: every leg, set up the way CI sets it
/// up. Both drifted before anyone looked: a MySQL 5.7.22 grant that passed through a client inside the server container
/// failed through CI's, and PostgreSQL bands ran at 100 connections where CI runs 2000.
/// </summary>
[TestFixture]
public class CiLocalParityTests
{
    private static string Read(params string[] parts) => File.ReadAllText(Path.Join([RepoSources.Root, .. parts]));

    private static string[] Legs(string engine)
    {
        var (exit, output) = CiTestedPathsTests.Python(Path.Join(RepoSources.Root, "scripts", "ci_matrix.py"), engine);
        Assert.That(exit, Is.EqualTo(0), $"ci_matrix.py {engine}: {output}");
        return output.Split('\n', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);
    }

    [Test]
    public void TheMatrixReader_EndsEachLineWithANewlineAlone()
    {
        var (exit, output) = CiTestedPathsTests.Python(Path.Join(RepoSources.Root, "scripts", "ci_matrix.py"), "sqlserver");
        Assert.That(exit, Is.EqualTo(0), $"ci_matrix.py sqlserver: {output}");
        Assert.That(output, Does.Not.Contain('\r'),
            "a carriage return ends up in the last field, the SQL Server band's collation, and the server refuses it as invalid");
    }

    [TestCase("sqlserver", 4)]
    [TestCase("postgres", 7)]
    [TestCase("mysql", 7)]
    [TestCase("mariadb", 6)]
    [TestCase("lctn1", 2)]
    public void TheMatrixReader_FindsEveryLegCiRunsOnMain(string engine, int legs) =>
        Assert.That(Legs(engine), Has.Length.EqualTo(legs),
            "the count CI runs on a merge to main; change it here only when continuous-integration.yml's matrix changes");

    [TestCase("postgres")]
    [TestCase("mysql")]
    [TestCase("mariadb")]
    public void EveryLegCiRunsOnMain_HasAFloorSweepBand(string engine)
    {
        var bands = Regex.Matches(Read("scripts", "run-floor-sweep.sh"), "^\\s*\"[^\":]+:([^\":]+):([^\":]+):\\d+:\\w+\"", RegexOptions.Multiline)
            .Select(m => $"{m.Groups[1].Value}:{m.Groups[2].Value}").ToHashSet();
        Assert.That(bands, Has.Count.GreaterThan(10), "read the floor sweep's bands");
        var missing = Legs(engine).Select(l => l.Split('\t')).Select(f => $"{f[0]}:{f[1]}").Where(l => !bands.Contains(l));
        Assert.That(missing, Is.Empty, "a CI leg no local band runs");
    }

    [Test]
    public void EachEnginesSetup_IsOneScript_ThatCiAndTheLocalSweepsBothRun()
    {
        var workflow = Read(".github", "workflows", "continuous-integration.yml");
        var floor = Read("scripts", "run-floor-sweep.sh");
        var linux = Read("scripts", "run-linux-leg.sh");
        var bands = Read("scripts", "run-modern-band-sweep.sh");
        Assert.Multiple(() =>
        {
            Assert.That(Regex.Matches(workflow, @"bash scripts/ci-mysql-family-setup\.sh ").Count, Is.EqualTo(3), "the MySQL, MariaDB and lctn1 jobs");
            Assert.That(workflow, Does.Contain("bash scripts/ci-postgres-setup.sh "));
            Assert.That(workflow, Does.Contain("bash scripts/ci-sqlserver-setup.sh "));
            foreach (var (name, text) in new[] { ("the workflow", workflow), ("run-floor-sweep.sh", floor), ("run-linux-leg.sh", linux), ("run-modern-band-sweep.sh", bands) })
                Assert.That(text, Does.Not.Contain("GRANT ALL").And.Not.Contain("ALTER LOGIN").And.Not.Contain("ALTER SYSTEM"),
                    $"{name} has its own copy of a setup step");
            Assert.That(floor, Does.Contain("ci_mysql_family_setup ").And.Contain("ci_postgres_setup "));
            Assert.That(linux, Does.Contain("ci_mysql_family_setup "));
            Assert.That(bands, Does.Contain("ci_sqlserver_setup ").And.Contain("scripts/ci_matrix.py sqlserver"));
        });
    }
}
