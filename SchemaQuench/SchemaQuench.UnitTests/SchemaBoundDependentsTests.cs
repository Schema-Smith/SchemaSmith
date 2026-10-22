// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace SchemaQuench.UnitTests;

// The plan is what decides whether a blocked module change proceeds: every dependent the package declares is dropped
// in an order SQL Server accepts, and one it does not declare stops everything before anything is dropped.
public class SchemaBoundDependentsTests
{
    private static SchemaBoundDependent Module(string name, int depth = 1, bool encrypted = false, string type = "view") =>
        new(SchemaBoundDependentKind.Module, "dbo", name, null, null, null, depth, encrypted, type, []);

    private static SchemaBoundDependent IndexedView(string name) =>
        new(SchemaBoundDependentKind.IndexedView, "dbo", name, null, null, null, 1, false, "view", []);

    private static SchemaBoundDependent Check(string table, string name) =>
        new(SchemaBoundDependentKind.CheckConstraint, "dbo", name, "dbo", table, null, 1, false, "CHECK constraint", []);

    private static SchemaBoundDependent Computed(string table, string column, params string[] indexes) =>
        new(SchemaBoundDependentKind.ComputedColumn, "dbo", table, "dbo", table, column, 1, false, "table", indexes);

    private static DeclaredObjects Declared(IEnumerable<string> modules = null, IEnumerable<string> indexedViews = null, Dictionary<string, DeclaredTable> tables = null) =>
        new(new HashSet<string>(modules ?? []), new HashSet<string>(indexedViews ?? []), tables ?? new Dictionary<string, DeclaredTable>());

    private static DeclaredTable Table(IEnumerable<string> checks = null, IEnumerable<string> computed = null, IEnumerable<string> indexes = null) =>
        new(new HashSet<string>(checks ?? []), new HashSet<string>(computed ?? []), new HashSet<string>(indexes ?? []));

    [Test]
    public void ADeclaredSecurityPolicy_IsDropped_AndAnUndeclaredOneStopsTheChange()
    {
        var policy = new SchemaBoundDependent(SchemaBoundDependentKind.SecurityPolicy, "dbo", "Sales Filter", null, null, null, 1, false, "security policy", []);
        var declared = new DeclaredObjects(new HashSet<string>(), new HashSet<string>(), new Dictionary<string, DeclaredTable>(), new HashSet<string> { "dbo.sales filter" });

        var plan = SchemaBoundDependents.Plan([policy], declared);
        var stopped = SchemaBoundDependents.Plan([policy], Declared());

        Assert.Multiple(() =>
        {
            Assert.That(plan.CanProceed, Is.True);
            Assert.That(plan.Drops.Single().Statements, Is.EqualTo(new[] { "DROP SECURITY POLICY [dbo].[Sales Filter]" }));
            Assert.That(plan.Drops.Single().AuditType, Is.EqualTo("securityPolicy"));
            Assert.That(stopped.CanProceed, Is.False, "a policy no script of the package creates is not SchemaSmith's to drop");
        });
    }

    [TestCase("CREATE SECURITY POLICY [dbo].[Sales Filter] ADD FILTER PREDICATE dbo.f(Rep) ON dbo.Sales", "dbo.sales filter")]
    [TestCase("EXEC (N'CREATE SECURITY POLICY dbo.[Sales Filter] ADD FILTER PREDICATE dbo.f(Rep) ON dbo.Sales')", "dbo.sales filter")]
    [TestCase("create security policy rls.p1 add filter predicate rls.f(x) on dbo.t", "rls.p1")]
    [TestCase("CREATE SECURITY POLICY p2 ADD FILTER PREDICATE dbo.f(x) ON dbo.t", "dbo.p2")]
    public void SecurityPoliciesCreatedBy_ReadsThePolicyAScriptCreates(string script, string key) =>
        Assert.That(DeclaredObjects.SecurityPoliciesCreatedBy([script]), Is.EqualTo(new[] { key }));

    [Test]
    public void DeclaredModules_AreDroppedDeepestFirst()
    {
        var plan = SchemaBoundDependents.Plan([Module("Inner", 1), Module("Outer", 2)], Declared(modules: ["dbo.inner", "dbo.outer"]));
        Assert.Multiple(() =>
        {
            Assert.That(plan.CanProceed, Is.True);
            Assert.That(plan.Drops.Select(d => d.Dependent.Name), Is.EqualTo(new[] { "Outer", "Inner" }), "the outer view is bound to the inner one, so it has to go first");
            Assert.That(plan.Drops[0].Statements, Is.EqualTo(new[] { "DROP VIEW [dbo].[Outer]" }));
            Assert.That(plan.Drops.Select(d => d.AuditType), Is.All.EqualTo("schemaBoundModule"));
        });
    }

    [Test]
    public void AnUndeclaredModule_StopsThePlan_AndNothingIsDropped()
    {
        var plan = SchemaBoundDependents.Plan([Module("Declared"), Module("Stranger")], Declared(modules: ["dbo.declared"]));
        Assert.Multiple(() =>
        {
            Assert.That(plan.CanProceed, Is.False);
            Assert.That(plan.Undeclared.Select(d => d.Name), Is.EqualTo(new[] { "Stranger" }));
            Assert.That(SchemaBoundDependents.BlockedMessage("dbo", "fn", plan), Does.Contain("dbo.Stranger").And.Contain("not declared by the package"));
        });
    }

    [Test]
    public void AnEncryptedModule_StopsThePlan_EvenWhenDeclared()
    {
        var plan = SchemaBoundDependents.Plan([Module("Secret", encrypted: true)], Declared(modules: ["dbo.secret"]));
        Assert.Multiple(() =>
        {
            Assert.That(plan.CanProceed, Is.False);
            Assert.That(plan.Encrypted.Select(d => d.Name), Is.EqualTo(new[] { "Secret" }));
            Assert.That(SchemaBoundDependents.BlockedMessage("dbo", "fn", plan), Does.Contain("encrypted"));
        });
    }

    [Test]
    public void ADeclaredCheckConstraint_AndADeclaredComputedColumnWithItsDeclaredIndex_AreDropped_IndexFirst()
    {
        var tables = new Dictionary<string, DeclaredTable> { ["dbo.t"] = Table(checks: ["ck_t"], computed: ["total"], indexes: ["ix_t_total"]) };
        var plan = SchemaBoundDependents.Plan([Check("T", "CK_T"), Computed("T", "Total", "IX_T_Total")], Declared(tables: tables));
        Assert.Multiple(() =>
        {
            Assert.That(plan.CanProceed, Is.True);
            Assert.That(plan.Drops.Select(d => d.AuditType), Is.EquivalentTo(new[] { "constraint", "column" }));
            var column = plan.Drops.Single(d => d.AuditType == "column");
            Assert.That(column.Statements, Is.EqualTo(new[] { "DROP INDEX [IX_T_Total] ON [dbo].[T]", "ALTER TABLE [dbo].[T] DROP COLUMN [Total]" }), "SQL Server refuses to drop a column an index covers");
            Assert.That(plan.Drops.Single(d => d.AuditType == "constraint").Statements, Is.EqualTo(new[] { "ALTER TABLE [dbo].[T] DROP CONSTRAINT [CK_T]" }));
        });
    }

    [Test]
    public void AComputedColumn_UnderAnIndexThePackageDoesNotDeclare_StopsThePlan_NamingTheIndex()
    {
        var tables = new Dictionary<string, DeclaredTable> { ["dbo.t"] = Table(computed: ["total"], indexes: ["ix_declared"]) };
        var plan = SchemaBoundDependents.Plan([Computed("T", "Total", "IX_Declared", "IX_Hand")], Declared(tables: tables));
        Assert.Multiple(() =>
        {
            Assert.That(plan.CanProceed, Is.False);
            Assert.That(plan.Undeclared.Single().Display, Does.Contain("IX_Hand").And.Not.Contain("IX_Declared"));
        });
    }

    [Test]
    public void ACheckOrColumn_OnATableThePackageDoesNotDeclare_IsUndeclared()
    {
        var plan = SchemaBoundDependents.Plan([Check("Other", "CK_Other"), Computed("Other", "Total")], Declared());
        Assert.That(plan.Undeclared.Select(d => d.Display), Is.EqualTo(new[] { "CHECK constraint CK_Other on dbo.Other", "computed column dbo.Other.Total" }));
    }

    [Test]
    public void ADeclaredIndexedView_IsDropped_AsAView()
    {
        var plan = SchemaBoundDependents.Plan([IndexedView("IvSales")], Declared(indexedViews: ["dbo.ivsales"]));
        Assert.Multiple(() =>
        {
            Assert.That(plan.CanProceed, Is.True);
            Assert.That(plan.Drops.Single().Statements, Is.EqualTo(new[] { "DROP VIEW [dbo].[IvSales]" }));
            Assert.That(plan.Drops.Single().AuditType, Is.EqualTo("indexedView"));
        });
    }

    [Test]
    public void Keys_IgnoreBracketsAndCase()
    {
        Assert.That(DeclaredObjects.Key("[Sales]", "[vw_Orders]"), Is.EqualTo("sales.vw_orders"));
    }
}
