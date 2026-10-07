// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using Schema.Utility;

namespace Schema.UnitTests.Utility
{
    [TestFixture]
    public class VersionHelperTests
    {
        [TestCase("2017", Platform.SqlServer, 14)]
        [TestCase("2019", Platform.SqlServer, 15)]
        [TestCase("2022", Platform.SqlServer, 16)]
        [TestCase("2025", Platform.SqlServer, 17)]
        [TestCase("2008", Platform.SqlServer, 10)]   // floor lowered to 2008 — years 2008-2016 declarable again
        [TestCase("2012", Platform.SqlServer, 11)]
        [TestCase("2014", Platform.SqlServer, 12)]
        [TestCase("2016", Platform.SqlServer, 13)]
        [TestCase("15", Platform.SqlServer, 15)]   // already-a-major declaration
        [TestCase("16", Platform.PostgreSQL, 16)]
        [TestCase("15.3", Platform.PostgreSQL, 15)]
        [TestCase("8.0", Platform.MySQL, 800)]
        [TestCase("8.4", Platform.MySQL, 804)]
        [TestCase("8", Platform.MySQL, 800)]
        [TestCase("10.6", Platform.MariaDb, 1006)]
        [TestCase("11.4", Platform.MariaDb, 1104)]
        [TestCase("18beta1", Platform.PostgreSQL, 18)]   // a pre-release of 18 is 18, not an unreadable gate
        [TestCase("19devel", Platform.PostgreSQL, 19)]
        public void ParseDeclaredVersion_NormalizesToComparable(string version, Platform platform, int expected)
        {
            Assert.That(VersionHelper.ParseDeclaredVersion(version, platform), Is.EqualTo(expected));
        }

        [TestCase("not-a-version", Platform.SqlServer)]
        [TestCase("", Platform.PostgreSQL)]
        public void ParseDeclaredVersion_ReturnsNull_WhenUnparseable(string version, Platform platform)
        {
            Assert.That(VersionHelper.ParseDeclaredVersion(version, platform), Is.Null);
        }

        [TestCase("not-a-version", Platform.PostgreSQL)]
        [TestCase("", Platform.MySQL)]
        public void ParseDetectedVersion_ReturnsNull_WhenUnparseable(string raw, Platform platform)
        {
            Assert.That(VersionHelper.ParseDetectedVersion(raw, platform), Is.Null);
        }

        [TestCase("160004", Platform.PostgreSQL, 16)]
        [TestCase("150010", Platform.PostgreSQL, 15)]
        [TestCase("8.0.36", Platform.MySQL, 800)]
        [TestCase("8.4.1", Platform.MySQL, 804)]
        [TestCase("16", Platform.SqlServer, 16)]
        [TestCase("10.50.4000.0", Platform.SqlServer, 10)] // SERVERPROPERTY('ProductVersion') on 2008 R2 -> major 10
        [TestCase("16.0.1000.6", Platform.SqlServer, 16)]  // ProductVersion on 2022 -> major 16
        [TestCase("17.0.4075.5", Platform.SqlServer, 17)]  // ProductVersion on 2025 -> major 17
        [TestCase("10.6.27-MariaDB", Platform.MariaDb, 1006)]
        [TestCase("11.4.2-MariaDB-1:11.4.2+maria~ubu2404", Platform.MariaDb, 1104)]
        public void ParseDetectedVersion_NormalizesToComparable(string raw, Platform platform, int expected)
        {
            Assert.That(VersionHelper.ParseDetectedVersion(raw, platform), Is.EqualTo(expected));
        }

        [TestCase(16, 17, false)]
        [TestCase(17, 17, true)]
        [TestCase(16, 15, true)]
        public void IsAtLeast_ComparesComparables(int detected, int required, bool expected)
        {
            Assert.That(VersionHelper.IsAtLeast(detected, required), Is.EqualTo(expected));
        }

        // Years below 2008 (not in the year map) are not declarable — the floor is 2008.
        [TestCase("2005", Platform.SqlServer)]
        [TestCase("2000", Platform.SqlServer)]
        public void ParseDeclaredVersion_BelowFloorYear_ReturnsNull(string version, Platform platform)
        {
            Assert.That(VersionHelper.ParseDeclaredVersion(version, platform), Is.Null);
        }

        [TestCase(Platform.SqlServer, 9, true)]     // SQL Server 2005 — below the 2008 floor
        [TestCase(Platform.SqlServer, 10, false)]   // 2008 floor
        [TestCase(Platform.SqlServer, 13, false)]   // SQL Server 2016 — now supported
        [TestCase(Platform.SqlServer, 16, false)]
        [TestCase(Platform.PostgreSQL, 11, true)]   // PostgreSQL 11 (below floor)
        [TestCase(Platform.PostgreSQL, 12, false)]  // 12 floor
        [TestCase(Platform.MySQL, 506, true)]       // MySQL 5.6 — below the 5.7 floor (no JSON)
        [TestCase(Platform.MySQL, 507, false)]      // 5.7 floor
        [TestCase(Platform.MySQL, 800, false)]      // 8.0 — above floor
        [TestCase(Platform.MariaDb, 1001, true)]    // MariaDB 10.1 — below the 10.2 floor (no JSON)
        [TestCase(Platform.MariaDb, 1002, false)]   // 10.2 floor
        [TestCase(Platform.MariaDb, 1006, false)]   // 10.6 — above floor
        public void IsBelowFloor_ComparesAgainstEngineFloor(Platform platform, int comparable, bool expected)
        {
            Assert.That(VersionHelper.IsBelowFloor(platform, comparable), Is.EqualTo(expected));
        }

        [TestCase(Platform.SqlServer, "2008 (major 10)")]
        [TestCase(Platform.PostgreSQL, "12")]
        [TestCase(Platform.MySQL, "5.7.22 / 8.0.23")]
        [TestCase(Platform.MariaDb, "10.2")]
        public void HardFloorDisplay_MatchesSupportedFloorsTable(Platform platform, string expected)
        {
            Assert.That(VersionHelper.HardFloorDisplay(platform), Is.EqualTo(expected));
        }

        [TestCase(Platform.PostgreSQL, "160013", 16, "16")]   // raw server_version_num -> major
        [TestCase(Platform.SqlServer, "16", 16, "16")]
        [TestCase(Platform.MySQL, "8.0.36", 800, "8.0.36")]
        [TestCase(Platform.MariaDb, "10.6.27-MariaDB", 1006, "10.6.27-MariaDB")]
        public void DisplayVersion_NormalizesPostgresRawNum(Platform platform, string raw, int comparable, string expected)
        {
            var info = new TargetVersionInfo(platform, raw, comparable);
            Assert.That(VersionHelper.DisplayVersion(info), Is.EqualTo(expected));
        }

        // The MySQL family's patch, which the major*100+minor comparable cannot see.
        [TestCase("8.0.23", Platform.MySQL, 80023)]
        [TestCase("8.0.23-log", Platform.MySQL, 80023)]
        [TestCase("5.7.21", Platform.MySQL, 50721)]
        [TestCase("26.7.0", Platform.MySQL, 260700)]      // calendar versioning
        [TestCase("8.0", Platform.MySQL, 80000)]
        [TestCase("10.5.2-MariaDB", Platform.MariaDb, 100502)]
        [TestCase("11.4.2-MariaDB-1:11.4.2+maria~ubu2404", Platform.MariaDb, 110402)]
        public void ParsePatchComparable_ReadsMajorMinorPatch(string raw, Platform platform, int expected)
        {
            Assert.That(VersionHelper.ParsePatchComparable(raw, platform), Is.EqualTo(expected));
        }

        [TestCase(Platform.SqlServer, "16.0.1000.6")]
        [TestCase(Platform.PostgreSQL, "160004")]
        public void ParsePatchComparable_IsMySqlFamilyOnly(Platform platform, string raw)
        {
            Assert.That(VersionHelper.ParsePatchComparable(raw, platform), Is.Null);
        }

        // P1: the MySQL floor is 5.7.22 on the 5.7 line and 8.0.23 on the 8.0 line. Below them DataTongs extracts
        // nothing (5.7.21) and the 8.0 feature gates fire on patches that lack the feature.
        [TestCase("5.6.51", true)]
        [TestCase("5.7.21", true)]
        [TestCase("5.7.22", false)]
        [TestCase("5.7.44", false)]
        [TestCase("8.0.12", true)]
        [TestCase("8.0.22", true)]
        [TestCase("8.0.23", false)]
        [TestCase("8.4.0", false)]
        [TestCase("9.0.1", false)]
        [TestCase("26.7.0", false)]
        public void IsBelowFloor_MySql_IsPatchAware(string raw, bool expected)
        {
            var info = new TargetVersionInfo(Platform.MySQL, raw, VersionHelper.ParseDetectedVersion(raw, Platform.MySQL)!.Value);
            Assert.That(VersionHelper.IsBelowFloor(info), Is.EqualTo(expected));
        }

        [TestCase(Platform.MariaDb, "10.1.48-MariaDB", true)]
        [TestCase(Platform.MariaDb, "10.2.0-MariaDB", false)]
        [TestCase(Platform.SqlServer, "9.0.5000.0", true)]
        [TestCase(Platform.SqlServer, "10.50.4000.0", false)]
        public void IsBelowFloor_OtherEngines_KeepTheirFloors(Platform platform, string raw, bool expected)
        {
            var info = new TargetVersionInfo(platform, raw, VersionHelper.ParseDetectedVersion(raw, platform)!.Value);
            Assert.That(VersionHelper.IsBelowFloor(info), Is.EqualTo(expected));
        }
    }
}
