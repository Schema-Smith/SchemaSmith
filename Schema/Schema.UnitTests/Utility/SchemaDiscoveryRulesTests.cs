// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Linq;
using NUnit.Framework;
using Schema.Domain;
using Schema.Utility;

namespace Schema.UnitTests.Utility;

// SchemaDiscoveryRules is the ONE rule for which discovered schema names a deploy accepts. It lived as private
// statics inside the SchemaQuench executable, so anything else reading a SchemaIdentificationScript's output
// could only learn which names the deploy would refuse by transcribing the lists -- and one bad name fails
// discovery for that database's WHOLE list, so a mismatch offers schemas the deploy never reaches.
// These tests pin the rule where it now lives; SchemaDiscoveryTests (62 cases) pin that the deploy still
// applies it with byte-identical messages.
[TestFixture]
public class SchemaDiscoveryRulesTests
{
    [TestCase("dbo")]
    [TestCase("SYS")]
    [TestCase("INFORMATION_SCHEMA")]
    [TestCase("guest")]
    [TestCase("db_datareader")]
    public void SqlServerReservedNames_AreReserved(string name) =>
        Assert.That(SchemaDiscoveryRules.IsReserved(name, Platform.SqlServer), Is.True);

    [TestCase("public")]
    [TestCase("pg_catalog")]
    [TestCase("pg_toast")]
    [TestCase("information_schema")]
    [TestCase("pg_temp_3")]
    [TestCase("pg_toast_temp_12")]
    public void PostgreSqlReservedNames_AreReserved(string name) =>
        Assert.That(SchemaDiscoveryRules.IsReserved(name, Platform.PostgreSQL), Is.True);

    [Test]
    public void ReservationIsPerPlatform()
    {
        Assert.That(SchemaDiscoveryRules.IsReserved("public", Platform.SqlServer), Is.False,
            "'public' is PostgreSQL's namespace, not SQL Server's");
        Assert.That(SchemaDiscoveryRules.IsReserved("dbo", Platform.PostgreSQL), Is.False,
            "'dbo' is SQL Server's namespace, not PostgreSQL's");
    }

    [Test]
    public void ValidNames_ReturnNoViolation() =>
        Assert.That(SchemaDiscoveryRules.Validate(new[] { "tenant_a", "Tenant B", "t-3", "t.4", "Ünïcødé" },
            Platform.SqlServer), Is.Null);

    [Test]
    public void Reserved_IsReportedWithItsKind()
    {
        var v = SchemaDiscoveryRules.Validate(new[] { "tenant_a", "dbo" }, Platform.SqlServer);
        Assert.That(v, Is.Not.Null);
        Assert.That(v.Name, Is.EqualTo("dbo"));
        Assert.That(v.Kind, Is.EqualTo(SchemaNameViolationKind.Reserved));
    }

    [TestCase("a]b", ']')]
    [TestCase("a[b", '[')]
    [TestCase("a\"b", '"')]
    [TestCase("a'b", '\'')]
    [TestCase("{{Tenant}}", '{')]
    public void DisallowedCharacter_IsReportedNamingTheCharacter(string name, char bad)
    {
        var v = SchemaDiscoveryRules.Validate(new[] { name }, Platform.SqlServer);
        Assert.That(v.Kind, Is.EqualTo(SchemaNameViolationKind.DisallowedCharacter));
        Assert.That(v.Detail, Does.Contain($"'{bad}'"), "the reason must name the character, or it is half a finding");
    }

    [Test]
    public void ControlCharacter_IsReported() =>
        Assert.That(SchemaDiscoveryRules.Validate(new[] { "a\tb" }, Platform.PostgreSQL).Kind,
            Is.EqualTo(SchemaNameViolationKind.ControlCharacter));

    [Test]
    public void Duplicate_IsReported_CaseInsensitively()
    {
        var v = SchemaDiscoveryRules.Validate(new[] { "Tenant_A", "tenant_a" }, Platform.SqlServer);
        Assert.That(v.Kind, Is.EqualTo(SchemaNameViolationKind.Duplicate));
        Assert.That(v.Name, Is.EqualTo("tenant_a"));
    }

    // The deploy skips null / blank rows rather than failing on them, so a consumer applying this rule must too
    // -- otherwise the two disagree about exactly the list the rule exists to agree on.
    [Test]
    public void NullAndBlankNames_AreSkipped_AsTheDeploySkipsThem() =>
        Assert.That(SchemaDiscoveryRules.Validate(new[] { null, "", "   ", "tenant_a" }, Platform.SqlServer), Is.Null);

    // The FIRST violation in discovery order is reported, and checks run in the deploy's order per name:
    // reserved, then characters, then duplicate.
    [Test]
    public void FirstViolationInOrder_IsTheOneReported()
    {
        var v = SchemaDiscoveryRules.Validate(new[] { "ok", "a]b", "dbo" }, Platform.SqlServer);
        Assert.That(v.Name, Is.EqualTo("a]b"));
        Assert.That(SchemaDiscoveryRules.DisallowedCharacters.OrderBy(c => c),
            Is.EquivalentTo(new[] { ']', '[', '"', '\'', '{', '}' }.OrderBy(c => c)));
    }
}
