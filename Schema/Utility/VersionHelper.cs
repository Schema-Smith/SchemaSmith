// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using Schema.Domain;

namespace Schema.Utility
{
    /// <summary>
    /// Normalizes engine version strings to a per-platform integer comparable (major-based)
    /// and compares them. Detected target version drives code generation; this utility is the
    /// shared comparison primitive for that and for the MinimumVersion pre-flight gate.
    /// </summary>
    public static class VersionHelper
    {
        // SQL Server release-year -> major-version map (declared-version year alias). Bottoms out at
        // 2008 (major 10): the intrinsic floor. Below compat 130 SchemaSmith ingests/compares the model
        // as XML (the JSON cliff), so 2008-2016 are supported; the separate DB compatibility-level floor
        // is 100 — see PreFlightVersionGuard.
        private static readonly Dictionary<int, int> SqlServerYearToMajor = new()
        {
            { 2008, 10 }, { 2012, 11 }, { 2014, 12 }, { 2016, 13 }, { 2017, 14 }, { 2019, 15 }, { 2022, 16 }, { 2025, 17 }
        };

        // Intrinsic per-engine hard floor: the lowest version the current script set supports, matching
        // the "Supported engine floors" table in the SchemaQuench reference docs. Enforced before any
        // deployment or extraction, independent of the opt-in Product.MinimumVersion guardrail. Keyed on
        // the actual platform (MariaDB's floor differs from MySQL's despite sharing the base engine).
        private static int HardFloorComparable(Platform platform) => platform switch
        {
            Platform.SqlServer => 10,     // SQL Server 2008 (below compat 130 the model is ingested/compared as XML)
            Platform.PostgreSQL => 12,    // PostgreSQL 12 (features above 12 — per-column compression + expression statistics (14), NULLS NOT DISTINCT (15) — are degraded per the unsupported-feature policy)
            Platform.MySQL => 507,        // MySQL 5.7 (JSON_TABLE-free JSON_EXTRACT parse; CHECK degrades below 8.0.16; data delivery requires 8.0)
            Platform.MariaDb => 1002,     // MariaDB 10.2 (recursive-CTE data-delivery shred below 10.6; full support)
            _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, "No version floor defined for platform")
        };

        /// <summary>The comparable of the intrinsic hard floor for the platform.</summary>
        public static int HardFloor(Platform platform) => HardFloorComparable(platform);

        /// <summary>Human-friendly form of the hard floor, matching the reference docs' floors table.</summary>
        public static string HardFloorDisplay(Platform platform) => platform switch
        {
            Platform.SqlServer => "2008 (major 10)",
            Platform.PostgreSQL => "12",
            Platform.MySQL => "5.7.22 / 8.0.23",
            Platform.MariaDb => "10.2",
            _ => HardFloorComparable(platform).ToString()
        };

        // MySQL's floor is a patch on two lines: 5.7.22 (JSON_ARRAYAGG, which DataTongs needs) and 8.0.23 (the 8.0
        // feature gates key on 800 and fire on earlier patches that lack the feature). MySQL 8.0.0-8.0.22 and
        // 5.7.0-5.7.21 are refused rather than gated patch by patch.
        private const int MySqlFloor57 = 50722;
        private const int MySqlFloor80 = 80023;

        /// <summary>True when the detected server comparable is below the platform's intrinsic hard floor.</summary>
        public static bool IsBelowFloor(Platform platform, int serverComparable)
            => serverComparable < HardFloorComparable(platform);

        /// <summary>True when the detected server is below the platform's floor, including MySQL's patch floors.</summary>
        public static bool IsBelowFloor(TargetVersionInfo info)
        {
            if (info.Platform != Platform.MySQL)
                return IsBelowFloor(info.Platform, info.ServerComparable);

            var patch = ParsePatchComparable(info.RawVersion, info.Platform) ?? info.ServerComparable * 100;
            return patch < MySqlFloor57 || (patch >= 80000 && patch < MySqlFloor80);
        }

        /// <summary>
        /// The MySQL family's <c>major*10000+minor*100+patch</c>, for the few gates that turn on a patch. Null on
        /// other engines and when the version cannot be read. The everyday comparable stays <c>major*100+minor</c>.
        /// </summary>
        public static int? ParsePatchComparable(string rawVersion, Platform platform)
        {
            if (platform.GetBasePlatform() != Platform.MySQL || string.IsNullOrWhiteSpace(rawVersion)) return null;
            var parts = rawVersion.Trim().Split('.');
            var major = LeadingNumber(parts[0]);
            if (major == null) return null;
            var minor = parts.Length >= 2 ? LeadingNumber(parts[1]) ?? 0 : 0;
            var patch = parts.Length >= 3 ? LeadingNumber(parts[2]) ?? 0 : 0;
            return major.Value * 10000 + minor * 100 + patch;
        }

        /// <summary>
        /// Human-friendly detected-version string for logging. PostgreSQL's raw
        /// <c>server_version_num</c> (e.g. <c>160013</c>) is normalized to its major (<c>16</c>);
        /// other engines report a readable version already.
        /// </summary>
        public static string DisplayVersion(TargetVersionInfo info) =>
            info.Platform.GetBasePlatform() == Platform.PostgreSQL ? info.ServerComparable.ToString() : info.RawVersion;

        /// <summary>Normalizes a user-declared version (MinimumVersion or a feature threshold) to the comparable.</summary>
        public static int? ParseDeclaredVersion(string version, Platform platform)
        {
            if (string.IsNullOrWhiteSpace(version)) return null;
            version = version.Trim();

            if (platform.GetBasePlatform() == Platform.MySQL) return ParseMajorMinor(version);

            if (LeadingNumber(SplitFirst(version)) is not { } value) return null;

            // SQL Server: a year (>= 2000) is an alias for its major.
            if (platform == Platform.SqlServer && value >= 2000)
                return SqlServerYearToMajor.TryGetValue(value, out var major) ? major : (int?)null;

            return value;
        }

        /// <summary>Normalizes a raw detection-query result to the comparable.</summary>
        public static int? ParseDetectedVersion(string rawVersion, Platform platform)
        {
            if (string.IsNullOrWhiteSpace(rawVersion)) return null;
            rawVersion = rawVersion.Trim();

            return platform switch
            {
                // PostgreSQL current_setting('server_version_num') -> e.g. 160004 -> major 16.
                Platform.PostgreSQL => int.TryParse(rawVersion, out var num) ? num / 10000 : (int?)null,
                // MySQL VERSION() -> e.g. "8.0.36" -> 800.
                // MariaDb VERSION() -> e.g. "10.6.27-MariaDB" -> 1006 (the -MariaDB suffix sits
                // on the patch part, which ParseMajorMinor ignores).
                Platform.MySQL or Platform.MariaDb => ParseMajorMinor(rawVersion),
                // SQL Server SERVERPROPERTY('ProductVersion') -> e.g. "10.50.4000.0" -> major 10 (first part).
                _ => int.TryParse(SplitFirst(rawVersion), out var v) ? v : (int?)null
            };
        }

        /// <summary>True when the detected comparable meets or exceeds the required comparable.</summary>
        public static bool IsAtLeast(int detectedComparable, int requiredComparable)
            => detectedComparable >= requiredComparable;

        // "8.0.36" / "8.4" / "8" -> major*100 + minor.
        private static int? ParseMajorMinor(string version)
        {
            var parts = version.Split('.');
            if (!int.TryParse(parts[0], out var major)) return null;
            var minor = 0;
            if (parts.Length >= 2 && !int.TryParse(parts[1], out minor)) return null;
            return major * 100 + minor;
        }

        // "18beta1" -> 18, "23-log" -> 23: a version part's leading digits. Null when it has none.
        private static int? LeadingNumber(string part)
        {
            var digits = 0;
            while (digits < part.Length && char.IsAsciiDigit(part[digits])) digits++;
            return digits > 0 && int.TryParse(part.AsSpan(0, digits), out var value) ? value : null;
        }

        private static string SplitFirst(string version)
            => version.Contains('.') ? version.Split('.')[0] : version;
    }
}
