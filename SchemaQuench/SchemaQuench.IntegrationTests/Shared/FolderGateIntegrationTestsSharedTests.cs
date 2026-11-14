// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

using NUnit.Framework;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// Folder-gate (#260) engine-facing contract against a live MySQL: MySQL returns an <c>Int64</c>
/// (0/1) scalar rather than a native boolean, so this pins that <see cref="FolderGate.ShouldApply"/>
/// (via <c>ScalarToBool</c>) interprets it correctly — the one genuinely engine-specific seam. The
/// folder-filtering / slot-rebuild logic is engine-agnostic (unit-covered).
/// </summary>
public abstract class FolderGateIntegrationTestsSharedTests
{
    protected abstract Platform Platform { get; }

    private readonly string _connectionString;

    protected FolderGateIntegrationTestsSharedTests()
    {
        var config = FactoryContainer.Resolve<IConfigurationRoot>();
        var connProps = ConnectionString.ReadProperties(config, "Target:ConnectionProperties");
        _connectionString = ConnectionString.Build(Platform, config["Target:Server"], "information_schema",
            config["Target:User"], config["Target:Password"], config["Target:Port"], connProps);
    }

    [Test]
    public void FolderGate_LiveMySql_InterpretsBooleanPredicates()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        Assert.Multiple(() =>
        {
            Assert.That(FolderGate.ShouldApply(cmd, ""), Is.True, "Blank expression always applies.");
            Assert.That(FolderGate.ShouldApply(cmd, "SELECT 1"), Is.True);
            Assert.That(FolderGate.ShouldApply(cmd, "SELECT 0"), Is.False);
            Assert.That(
                FolderGate.ShouldApply(cmd, "SELECT CASE WHEN @@version LIKE '%' THEN 1 ELSE 0 END"),
                Is.True, "A real predicate returning Int64 evaluates true on MySQL.");
        });
    }

    [Test]
    public void FolderGate_LiveMySql_EvaluatesResolvedScriptToken()
    {
        // #260 fix: a gate may reference a script token, which is resolved before evaluation.
        // Pre-fix the unresolved '{{EnvType}}' would never equal 'prod' and the gate would read false.
        var folder = new TemplateFolder
        {
            FolderPath = "EnvGated",
            QuenchSlot = TemplateQuenchSlot.Before,
            ShouldApplyExpression = "SELECT CASE WHEN '{{EnvType}}' = 'prod' THEN 1 ELSE 0 END"
        };
        folder.LoadSqlFiles(NonexistentBasePath(), [new KeyValuePair<string, string>("EnvType", "prod")], Platform);

        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        Assert.That(FolderGate.ShouldApply(cmd, folder.ShouldApplyExpression), Is.True);
    }

    [Test]
    public void VersionTokens_CompatibilityLevelFallsBackToServerVersion_OffSqlServer()
    {
        // A1 parity smoke: off SQL Server there is no compatibility-level concept, so detection returns
        // a null CompatibilityLevel and {{CompatibilityLevel}} falls back to the detected server version.
        // A folder gate comparing the two tokens is therefore true — one portable expression shape works
        // across the per-platform packages. Uses the real detected version, so it asserts nothing engine-
        // version-specific.
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        var info = TargetVersionDetector.Detect(cmd, Platform);
        Assert.Multiple(() =>
        {
            Assert.That(info.CompatibilityLevel, Is.Null, "Off SQL Server there is no compatibility-level concept.");
            Assert.That(info.ServerComparable, Is.GreaterThan(0));
        });

        var product = new Product { Name = "P", Platform = Platform };
        var template = new Template { Name = "T" };
        var folder = new TemplateFolder
        {
            FolderPath = "parity",
            QuenchSlot = TemplateQuenchSlot.Before,
            ShouldApplyExpression = "SELECT CASE WHEN {{CompatibilityLevel}} = {{ServerMajorVersion}} THEN 1 ELSE 0 END"
        };
        folder.Scripts.Add(new SqlScript { Name = "parity.sql" });
        template.ScriptFolders.Add(folder);

        var quench = new DatabaseQuench(product, template, new DatabaseQuenchOptions { Server = "srv", DatabaseName = "db", UpdateTables = false, DeliverData = false, Drops = new DropPolicy(Columns: true, ForeignKeys: true, CheckConstraints: true, ExcludeConstraints: true, Statistics: true, Indexes: true) }, null);
        quench.PrepareIterationContent();
        quench.PrepareVersionScriptTokens(info.ServerComparable, info.CompatibilityLevel);
        quench.ApplyFolderGates(cmd);

        Assert.That(quench.IterationBeforeScripts.Select(s => s.Name), Is.EqualTo(new[] { "parity.sql" }));
    }

    private static string NonexistentBasePath() =>
        Path.Combine(Path.GetTempPath(), "ss-folder-gate-" + Guid.NewGuid().ToString("N"));
}
