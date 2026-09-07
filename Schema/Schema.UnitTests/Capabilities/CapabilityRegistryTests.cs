// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Schema.Capabilities;
using Schema.Domain;

namespace Schema.UnitTests.Capabilities
{
    // Structural + manifest-string-alignment checks for the capability registry. The version-boundary
    // drift-guard (asserting IntroducedInComparable against the live .sql gates, and each ManifestObjectType
    // against a shipped .sql resource) lives in the integration suite; these are the no-engine invariants.
    [TestFixture]
    public class CapabilityRegistryTests
    {
        // Mirror of the exact 'downgraded' ChangeAudit ObjectType literals emitted by the deploy-time guards.
        // A rename in the .sql without updating the registry breaks the integration resource-grep; a rename in
        // the registry without updating this set breaks here.
        private static readonly HashSet<string> KnownManifestObjectTypes = new()
        {
            "temporal (SQL Server 2016)",
            "graph table (SQL Server 2017)",
            "ledger table (SQL Server 2022)",
            "XML compression (SQL Server 2022)",
            "data masking (SQL Server 2016)",
            "Always Encrypted (SQL Server 2016)",
            "columnstore index (SQL Server 2012/2014)",
            "NULLS NOT DISTINCT (PG15)",
            "expression statistics (PG14)",
            "per-column compression (PG14)",
            "table access method (PG15)",
            "VIRTUAL generated column (PG18)",
            "table without its PERIOD FOR clause",
            "table without its WITH SYSTEM VERSIONING clause",
            "column without its WITHOUT SYSTEM VERSIONING clause",
            "CDC (database not enabled)",
            "Change Tracking (database not enabled)",
            "FILESTREAM (server or filegroup not available)",
            "INDEX (invisible, MySQL 8.0 / MariaDB 10.6)",
            "INDEX (descending key part, MySQL 8.0 / MariaDB 10.8)",
            "CHECK constraint (MySQL 8.0.16)",
            "column (DEFAULT expression, MySQL 8.0.13)",
            "column (invisible, MySQL 8.0.23 / MariaDB 10.3)",
            "column (SRID, MySQL 8.0.3)",
            "INDEX (functional/expression, MySQL 8.0.13)"
        };

        [Test]
        public void AllRows_HaveWellFormedFields()
        {
            Assert.That(CapabilityRegistry.All, Is.Not.Empty);
            Assert.Multiple(() =>
            {
                foreach (var c in CapabilityRegistry.All)
                {
                    Assert.That(c.Key, Is.Not.Null.And.Not.Empty, "Key");
                    Assert.That(c.DisplayName, Is.Not.Null.And.Not.Empty, $"DisplayName for {c.Key}");
                    Assert.That(c.IntroducedInDisplay, Is.Not.Null.And.Not.Empty, $"IntroducedInDisplay for {c.Key}");
                    // 0 means "not version-gated" and is legal ONLY alongside a stated database
                    // prerequisite -- otherwise it is a row that forgot its version.
                    if (c.RequiredDatabaseSetting == null)
                        Assert.That(c.IntroducedInComparable, Is.GreaterThan(0), $"IntroducedInComparable for {c.Key}/{c.Platform}");
                    else
                        Assert.That(c.IntroducedInComparable, Is.Zero, $"a database-gated row must not carry a version it never compares against: {c.Key}/{c.Platform}");
                    Assert.That(c.Platform, Is.Not.EqualTo(Platform.Unknown), $"Platform for {c.Key}");
                    Assert.That(c.RequiredCompatLevel, Is.Null.Or.GreaterThan(0), $"RequiredCompatLevel for {c.Key}");
                }
            });
        }

        [Test]
        public void KeyPlatform_IsUnique()
        {
            var dups = CapabilityRegistry.All
                .GroupBy(c => (c.Key, c.Platform))
                .Where(g => g.Count() > 1)
                .Select(g => $"{g.Key.Key}/{g.Key.Platform}")
                .ToList();
            Assert.That(dups, Is.Empty, "each (Key, Platform) pair must be unique: " + string.Join(", ", dups));
        }

        [Test]
        public void ManifestObjectTypes_AreKnownDowngradedLiterals()
        {
            var unexpected = CapabilityRegistry.All
                .Where(c => c.ManifestObjectType != null && !KnownManifestObjectTypes.Contains(c.ManifestObjectType))
                .Select(c => $"{c.Key}/{c.Platform}: '{c.ManifestObjectType}'")
                .ToList();
            Assert.That(unexpected, Is.Empty, "ManifestObjectType must match a known 'downgraded' literal: " + string.Join("; ", unexpected));
        }

        [Test]
        public void For_ReturnsOnlyThatPlatform_AndMariaDbIsDistinctFromMySql()
        {
            Assert.That(CapabilityRegistry.For(Platform.MariaDb).All(c => c.Platform == Platform.MariaDb));
            Assert.That(CapabilityRegistry.For(Platform.MySQL).All(c => c.Platform == Platform.MySQL));

            // MariaDb rows are independent of MySQL: same feature keys, different intro versions.
            var mysqlInvisible = CapabilityRegistry.For(Platform.MySQL).Single(c => c.Key == "invisible-index");
            var mariaInvisible = CapabilityRegistry.For(Platform.MariaDb).Single(c => c.Key == "invisible-index");
            Assert.That(mysqlInvisible.IntroducedInComparable, Is.EqualTo(800));
            Assert.That(mariaInvisible.IntroducedInComparable, Is.EqualTo(1006));
        }

        [Test]
        public void MariaDb_DescendingAndInvisible_HaveDistinctIntroVersions()
        {
            // The mismatch-prone rows: MariaDB descending (10.8) is a later version than invisible (10.6).
            var maria = CapabilityRegistry.For(Platform.MariaDb);
            Assert.That(maria.Single(c => c.Key == "invisible-index").IntroducedInComparable, Is.EqualTo(1006));
            Assert.That(maria.Single(c => c.Key == "descending-index").IntroducedInComparable, Is.EqualTo(1008));
        }

        [Test]
        public void MariaDb_HasNoCheckConstraintRow()
        {
            // CHECK is supported at the MariaDb 10.2 floor, so it never degrades — no MariaDb row.
            Assert.That(CapabilityRegistry.For(Platform.MariaDb).Any(c => c.Key == "check-constraint"), Is.False);
        }

        [Test]
        public void MariaDb_HasNoDefaultExpressionRow()
        {
            // MDEV-10134 landed in MariaDB 10.2.1, at/below our 10.2 floor — always supported, no row.
            Assert.That(CapabilityRegistry.For(Platform.MariaDb).Any(c => c.Key == "default-expression"), Is.False);
        }

        [Test]
        public void MariaDb_HasNoColumnSridRow()
        {
            // MariaDB has no SRID attribute at any version — SchemaSmith_SupportsColumnSrid() is
            // unconditionally 0 there, so there is no version boundary for a row to encode.
            Assert.That(CapabilityRegistry.For(Platform.MariaDb).Any(c => c.Key == "column-srid"), Is.False);
        }

        [Test]
        public void MariaDb_HasNoFunctionalIndexRow()
        {
            // MariaDB has no equivalent to a functional/expression index in this form at any version —
            // SchemaSmith_SupportsFunctionalIndex() is unconditionally 0 there, so there is no version
            // boundary for a row to encode (same "no row" shape as column-srid above).
            Assert.That(CapabilityRegistry.For(Platform.MariaDb).Any(c => c.Key == "functional-index"), Is.False);
        }

        [Test]
        public void InvisibleColumn_HasBothPlatformRows_WithDistinctIntroVersions()
        {
            // Unlike column-srid, invisible column IS a real threshold on both engines (unlike invisible
            // INDEX, the column keyword is identical on both — only the intro version differs).
            var mysql = CapabilityRegistry.For(Platform.MySQL).Single(c => c.Key == "invisible-column");
            var maria = CapabilityRegistry.For(Platform.MariaDb).Single(c => c.Key == "invisible-column");
            Assert.That(mysql.IntroducedInComparable, Is.EqualTo(800));
            Assert.That(maria.IntroducedInComparable, Is.EqualTo(1003));
            Assert.That(mysql.ManifestObjectType, Is.EqualTo(maria.ManifestObjectType),
                "shared manifest literal — mirrors invisible-index");
        }

        [Test]
        public void SqlServer_Columnstore_SplitsNonclusteredAndClustered()
        {
            var ss = CapabilityRegistry.For(Platform.SqlServer);
            Assert.That(ss.Single(c => c.Key == "columnstore-nonclustered").IntroducedInComparable, Is.EqualTo(11));
            Assert.That(ss.Single(c => c.Key == "columnstore-clustered").IntroducedInComparable, Is.EqualTo(12));
        }

        [Test]
        public void DataDelivery_IsAMySqlSkip_WithNoManifest_AndNoMariaDbRow()
        {
            var dd = CapabilityRegistry.For(Platform.MySQL).Single(c => c.Key == "data-delivery");
            Assert.That(dd.IntroducedInComparable, Is.EqualTo(800));
            Assert.That(dd.Degrade, Is.EqualTo(DegradeKind.Skip));
            Assert.That(dd.ManifestObjectType, Is.Null, "data delivery skips at the C# layer — no ChangeAudit manifest");
            Assert.That(CapabilityRegistry.For(Platform.MariaDb).Any(c => c.Key == "data-delivery"), Is.False,
                "MariaDb <10.6 delivers the same rows via a recursive CTE (equivalent-path) — no row");
        }
    }
}
