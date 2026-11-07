// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Linq;
using Schema.Domain;
using Schema.Domain.Settings;
using Schema.Validation;
using Schema.Validation.Checks;

namespace Schema.UnitTests.Validation.Checks;

[TestFixture]
public class SettingsCheckTests
{
    private static Setting S(string kind, string name, bool? fix = true, DriftLevel? onDrift = DriftLevel.Warn, string gate = null, string variant = null) =>
        new() { Kind = kind, Name = name, Value = "1", Fix = fix, OnDrift = onDrift, ShouldApplyExpression = gate, VariantName = variant };

    private static SettingsFile File(SettingScope scope, params Setting[] settings) =>
        new() { Scope = scope, FilePath = scope == SettingScope.Server ? "pkg/ServerSettings.json" : "pkg/Templates/Main/DatabaseSettings.json", Settings = settings.ToList() };

    private static ValidationContext Context(Platform platform, SettingsFile server = null, SettingsFile database = null, bool schemaTemplate = false)
    {
        var template = new Template { Name = "Main", DatabaseSettings = database, SchemaIdentificationScript = schemaTemplate ? "SELECT 'dbo'" : null };
        return new ValidationContext(new Product { Name = "Acme", Platform = platform, ServerSettings = server }, [template], "pkg");
    }

    private static Finding[] Run(ValidationContext ctx) => new SettingsCheck().Run(ctx).ToArray();

    [Test]
    public void NoSettingsFiles_NoFindings()
    {
        Assert.That(Run(Context(Platform.SqlServer)), Is.Empty);
    }

    [Test]
    public void ValidDeclarations_NoFindings()
    {
        var ctx = Context(Platform.SqlServer,
            server: File(SettingScope.Server, S("Configuration", "max degree of parallelism", gate: "SELECT 1", variant: "OLTP"),
                S("Configuration", "max degree of parallelism", gate: "SELECT 0", variant: "DW"), S("TraceFlag", "1117")),
            database: File(SettingScope.Database, S("Option", "RECOVERY"), S("ScopedConfiguration", "MAXDOP")));

        Assert.That(Run(ctx), Is.Empty);
    }

    [Test]
    public void UnknownKind_IsSS_SET_001_NamingTheKindsThePlatformHas()
    {
        var findings = Run(Context(Platform.PostgreSQL, server: File(SettingScope.Server, S("Variable", "work_mem"))));

        Assert.That(findings, Has.Exactly(1).Items);
        Assert.Multiple(() =>
        {
            Assert.That(findings[0].Code, Is.EqualTo("SS-SET-001"));
            Assert.That(findings[0].Severity, Is.EqualTo(Severity.Error));
            Assert.That(findings[0].Location, Is.EqualTo("ServerSettings.json / Variable 'work_mem'"));
            Assert.That(findings[0].Message, Does.Contain("'Variable'").And.Contain("Parameter"));
        });
    }

    [TestCase(null, DriftLevel.Warn)]
    [TestCase(true, null)]
    public void MissingFixOrOnDrift_IsSS_SET_002(bool? fix, DriftLevel? onDrift)
    {
        var findings = Run(Context(Platform.MySQL, server: File(SettingScope.Server, S("Variable", "max_connections", fix, onDrift))));

        Assert.That(findings.Select(f => f.Code), Is.EqualTo(new[] { "SS-SET-002" }));
        Assert.That(findings[0].Message, Does.Contain("must state Fix and OnDrift"));
    }

    [Test]
    public void TwoDeclarationsOfOneSetting_EitherUngated_IsSS_SET_003()
    {
        var findings = Run(Context(Platform.SqlServer, database: File(SettingScope.Database,
            S("Option", "RECOVERY"), S("option", "recovery", gate: "SELECT 1", variant: "Prod"))));

        Assert.That(findings.Select(f => f.Code), Is.EqualTo(new[] { "SS-SET-003" }));
        Assert.That(findings[0].Location, Is.EqualTo("Template 'Main' / DatabaseSettings.json / Option 'RECOVERY'"));
    }

    [Test]
    public void DatabaseSettingsOnASchemaTemplate_IsSS_SET_004()
    {
        var findings = Run(Context(Platform.PostgreSQL, database: File(SettingScope.Database, S("Parameter", "work_mem")), schemaTemplate: true));

        Assert.That(findings.Select(f => f.Code), Is.EqualTo(new[] { "SS-SET-004" }));
        Assert.That(findings[0].Message, Does.Contain("runs once per database"));
    }

    [Test]
    public void SettingWithoutAName_IsSS_SET_002_Too_AndNamesTheFile()
    {
        var findings = Run(Context(Platform.SqlServer, server: File(SettingScope.Server, S("Configuration", null))));

        Assert.That(findings.Select(f => f.Code), Is.EqualTo(new[] { "SS-SET-002" }));
        Assert.That(findings[0].Message, Does.Contain("has no Name"));
    }

    [Test]
    public void IsRegistered()
    {
        Assert.That(ValidationCheckRegistry.Default().OfType<SettingsCheck>(), Has.Exactly(1).Items);
    }
}
