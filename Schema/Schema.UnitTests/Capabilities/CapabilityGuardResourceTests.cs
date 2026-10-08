// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using Schema.Capabilities;
using Schema.Utility;

namespace Schema.UnitTests.Capabilities
{
    // Drift-guard for the registry against the shipped .sql guards + the C# data-delivery gate, for the rows
    // that have no callable predicate (SQL Server / PostgreSQL inline gates, MySQL data delivery). The
    // MySQL/MariaDb predicate boundaries are asserted live in the integration suite
    // (CapabilityGuardConsistencyTests); these are the no-engine checks: the exact degrade message still ships
    // in a real script, and the SS/PG numeric gate literals + the data-delivery boundary still match the registry.
    [TestFixture]
    public class CapabilityGuardResourceTests
    {
        private static readonly Assembly SchemaAsm = typeof(Capability).Assembly;

        [Test]
        public void ManifestObjectTypes_StillShipInAnEmbeddedScript()
        {
            var allSql = AllEmbeddedSql();
            var missing = CapabilityRegistry.All
                .Where(c => c.ManifestObjectType != null && !allSql.Contains(c.ManifestObjectType))
                .Select(c => $"{c.Key}/{c.Platform}: '{c.ManifestObjectType}'")
                .ToList();
            Assert.That(missing, Is.Empty,
                "every registry ManifestObjectType must still exist verbatim in a shipped .sql script (the message encodes the version): "
                + string.Join("; ", missing));
        }

        [Test]
        public void SqlServer_GateLiterals_PinColumnstoreAndTheTwentySixteenFamily()
        {
            // Columnstore: nonclustered needs major 11, clustered 12 — the distinctive split literal.
            Assert.That(LoadScript("SqlServer.SchemaSmith.DegradeUnsupportedColumnStore.sql"),
                Does.Contain("THEN 12 ELSE 11"),
                "columnstore rows (NCCI 11 / CCI 12) must match the DegradeUnsupportedColumnStore gate");
            // Temporal / masking / Always Encrypted all gate on major < 13 (SQL Server 2016).
            Assert.That(LoadScript("SqlServer.SchemaSmith.DegradeUnsupportedFeatures.sql"),
                Does.Contain("< 13"),
                "the 2016 features (temporal/masking/AE) must gate on fn_ServerMajorVersion() < 13");
        }

        [Test]
        public void PostgreSql_GuardFunctionVersions_PinTheRegistry()
        {
            Assert.That(LoadScript("PostgreSQL.SchemaSmith.ColumnCompression.sql"), Does.Contain(">= 14"),
                "per-column compression (PG14)");
            Assert.That(LoadScript("PostgreSQL.SchemaSmith.StatisticsExpressionColumns.sql"), Does.Contain(">= 14"),
                "expression statistics (PG14)");
            Assert.That(LoadScript("PostgreSQL.SchemaSmith.IndexNullsNotDistinct.sql"), Does.Contain(">= 15"),
                "NULLS NOT DISTINCT (PG15)");
            Assert.That(LoadScript("PostgreSQL.SchemaSmith.SequenceQuench.sql"), Does.Contain("ServerVersionNum\"() < 15"),
                "UNLOGGED sequence (PG15)");
        }

        // Data delivery has no callable predicate and no ChangeAudit manifest — the boundary lives in the
        // (private) BuildJsonRowSourceMySql, the documented single source of truth: JSON_TABLE at >= 800,
        // throws for non-MariaDB below it. Probe it so the registry's data-delivery comparable (800) can't drift.
        [Test]
        public void DataDelivery_MySqlBoundary_Matches_Registry800()
        {
            var dd = CapabilityRegistry.For(Schema.Domain.Platform.MySQL).Single(c => c.Key == "data-delivery");
            Assert.That(dd.IntroducedInComparable, Is.EqualTo(800));

            var method = typeof(MergeScriptHelper).GetMethod("BuildJsonRowSourceMySql",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null, "BuildJsonRowSourceMySql not found — registry data-delivery probe is stale");

            // An empty List<MySqlColumnInfo> via the method's own (nested-internal) parameter type.
            var emptyColumns = Activator.CreateInstance(method!.GetParameters()[0].ParameterType);

            // At the boundary comparable: the JSON_TABLE row source is produced.
            var atBoundary = (string)method!.Invoke(null, new object[] { emptyColumns, dd.IntroducedInComparable })!;
            Assert.That(atBoundary, Does.Contain("JSON_TABLE"), "data delivery must be supported at MySQL 8.0 (800)");

            // Just below: a (non-MariaDB) target throws — delivery is skipped/unsupported.
            var ex = Assert.Throws<TargetInvocationException>(() =>
                method.Invoke(null, new object[] { emptyColumns, dd.IntroducedInComparable - 1 }));
            Assert.That(ex!.InnerException, Is.TypeOf<NotSupportedException>(),
                "data delivery must be unsupported just below MySQL 8.0 (799)");
        }

        private static string LoadScript(string endsWith)
        {
            var name = SchemaAsm.GetManifestResourceNames().Single(n => n.EndsWith(endsWith, StringComparison.Ordinal));
            using var s = SchemaAsm.GetManifestResourceStream(name)!;
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }

        private static string AllEmbeddedSql()
        {
            var sb = new StringBuilder();
            foreach (var n in SchemaAsm.GetManifestResourceNames().Where(n => n.EndsWith(".sql", StringComparison.Ordinal)))
            {
                using var s = SchemaAsm.GetManifestResourceStream(n)!;
                using var r = new StreamReader(s);
                sb.AppendLine(r.ReadToEnd());
            }
            return sb.ToString();
        }
    }
}
