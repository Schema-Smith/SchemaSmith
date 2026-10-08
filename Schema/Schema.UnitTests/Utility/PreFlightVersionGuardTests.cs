// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using NUnit.Framework;
using Schema.Domain;
using Schema.Utility;

namespace Schema.UnitTests.Utility
{
    [TestFixture]
    public class PreFlightVersionGuardTests
    {
        [Test]
        public void CheckOrThrow_BelowServerFloor_Throws_WithClearMessage()
        {
            var info = new TargetVersionInfo(Platform.SqlServer, "9", 9);   // SQL Server 2005 — below the 2008 floor

            var ex = Assert.Throws<RunFailedException>(() =>
                PreFlightVersionGuard.CheckOrThrow(info, "SQL2K5\\SC2K5", "Chinook"));

            Assert.That(ex!.Message, Does.Contain("below the minimum supported"));
            Assert.That(ex.Message, Does.Contain("SQL2K5"));
        }

        [Test]
        public void CheckOrThrow_CompatBelow100_Throws_DistinctMessage()
        {
            var info = new TargetVersionInfo(Platform.SqlServer, "10", 10, 90);   // 2008 binary, compat-90 DB

            var ex = Assert.Throws<RunFailedException>(() =>
                PreFlightVersionGuard.CheckOrThrow(info, "srv", "OldDb"));

            Assert.That(ex!.Message, Does.Contain("compatibility level 90"));
            Assert.That(ex.Message, Does.Contain("OldDb"));
        }

        [Test]
        public void CheckOrThrow_CompatAtFloor100_DoesNotThrow()
        {
            // compat 100 (SQL Server 2008) is the floor: below compat 130 the model is ingested/compared
            // as XML (OPENJSON's JSON-path parse-errors below 130), so a compat-100 DB is supported.
            var info = new TargetVersionInfo(Platform.SqlServer, "10", 10, 100);

            Assert.DoesNotThrow(() => PreFlightVersionGuard.CheckOrThrow(info, "srv", "Db"));
        }

        [Test]
        public void CheckOrThrow_SupportedServerAndCompat_DoesNotThrow()
        {
            var info = new TargetVersionInfo(Platform.SqlServer, "14", 14, 140);

            Assert.DoesNotThrow(() => PreFlightVersionGuard.CheckOrThrow(info, "srv", "Db"));
        }

        [Test]
        public void CheckOrThrow_CompatNull_SkipsCompatCheck()
        {
            var info = new TargetVersionInfo(Platform.SqlServer, "14", 14);   // compat not detected

            Assert.DoesNotThrow(() => PreFlightVersionGuard.CheckOrThrow(info, "srv"));
        }

        [Test]
        public void CheckOrThrow_PostgresBelowFloor_Throws()
        {
            var info = new TargetVersionInfo(Platform.PostgreSQL, "110006", 11);   // PostgreSQL 11 — below the 12 floor

            var ex = Assert.Throws<RunFailedException>(() => PreFlightVersionGuard.CheckOrThrow(info, "pg"));
            Assert.That(ex!.Message, Does.Contain("below the minimum supported"));
        }

        [Test]
        public void CheckOrThrow_MariaDbAtFloor_DoesNotThrow()
        {
            var info = new TargetVersionInfo(Platform.MariaDb, "10.2.44-MariaDB", 1002);   // 10.2 floor

            Assert.DoesNotThrow(() => PreFlightVersionGuard.CheckOrThrow(info, "maria"));
        }

        [Test]
        public void CheckOrThrow_MariaDbBelowFloor_Throws()
        {
            var info = new TargetVersionInfo(Platform.MariaDb, "10.1.48-MariaDB", 1001);   // 10.1 — below the 10.2 floor (no JSON)

            var ex = Assert.Throws<RunFailedException>(() => PreFlightVersionGuard.CheckOrThrow(info, "maria"));
            Assert.That(ex!.Message, Does.Contain("below the minimum supported"));
        }

        [Test]
        public void CheckOrThrow_MySqlAtFloor_DoesNotThrow()
        {
            var info = new TargetVersionInfo(Platform.MySQL, "5.7.22", 507);   // 5.7 floor (5.7.22)

            Assert.DoesNotThrow(() => PreFlightVersionGuard.CheckOrThrow(info, "mysql"));
        }

        [Test]
        public void CheckOrThrow_MySqlBelowFloor_Throws()
        {
            var info = new TargetVersionInfo(Platform.MySQL, "5.6.51", 506);   // 5.6 — below the 5.7 floor (no JSON)

            var ex = Assert.Throws<RunFailedException>(() => PreFlightVersionGuard.CheckOrThrow(info, "mysql"));
            Assert.That(ex!.Message, Does.Contain("below the minimum supported"));
        }

        [TestCase("5.7.21")]
        [TestCase("8.0.22")]
        public void CheckOrThrow_MySqlInsideTheRefusedPatchBand_Throws_NamingBothFloors(string raw)
        {
            var info = new TargetVersionInfo(Platform.MySQL, raw, VersionHelper.ParseDetectedVersion(raw, Platform.MySQL)!.Value);

            var ex = Assert.Throws<RunFailedException>(() => PreFlightVersionGuard.CheckOrThrow(info, "mysql"));
            Assert.That(ex!.Message, Does.Contain(raw).And.Contain("5.7.22").And.Contain("8.0.23"));
        }
    }
}
