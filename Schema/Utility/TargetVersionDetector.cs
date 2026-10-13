// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Schema.Domain;

namespace Schema.Utility
{
    /// <summary>
    /// Detects a target server's version over an open command. Detection failure is a hard
    /// error: SchemaSmith never generates or deploys against an unknown target version.
    /// </summary>
    public static class TargetVersionDetector
    {
        public static string GetVersionQuery(Platform platform) => platform switch
        {
            // ProductVersion (e.g. '10.50.4000.0'), NOT ProductMajorVersion: the latter is a 2016+ property
            // that returns NULL on the pre-2016 binaries the SQL Server 2008 floor targets, so detection would
            // fail on exactly those servers. ParseDetectedVersion takes the major from the first dotted part.
            Platform.SqlServer => "SELECT CONVERT(varchar(50), SERVERPROPERTY('ProductVersion'))",
            Platform.PostgreSQL => "SELECT current_setting('server_version_num')",
            Platform.MySQL => "SELECT VERSION()",
            Platform.MariaDb => "SELECT VERSION()",
            _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, "Unsupported platform for version detection")
        };

        public static TargetVersionInfo Detect(IDbCommand command, Platform platform, string databaseName = null)
        {
            command.CommandText = GetVersionQuery(platform);
            var raw = command.ExecuteScalar()?.ToString();

            // MySQL and MariaDB share the VERSION() query and comparable encoding, so a package
            // pointed at the wrong one would silently mis-generate DDL. The -MariaDB marker in the
            // version string disambiguates; fail closed on a mismatch.
            var isMariaDb = raw != null && raw.IndexOf("MariaDB", StringComparison.OrdinalIgnoreCase) >= 0;
            if (platform == Platform.MariaDb && !isMariaDb)
                throw new RunFailedException($"Target declared as MariaDb but the server does not appear to be MariaDB (VERSION() = '{raw ?? "<null>"}').");
            if (platform == Platform.MySQL && isMariaDb)
                throw new RunFailedException($"Target declared as MySQL but the server appears to be MariaDB (VERSION() = '{raw}'). Set Platform to MariaDb.");

            var comparable = VersionHelper.ParseDetectedVersion(raw, platform);
            if (comparable == null)
                throw new RunFailedException($"Unable to determine the {platform} server version (got '{raw ?? "<null>"}').");

            int? compatibilityLevel = null;
            if (platform.GetBasePlatform() == Platform.SqlServer && !string.IsNullOrWhiteSpace(databaseName))
                compatibilityLevel = DetectSqlServerCompatibilityLevel(command, databaseName);

            return new TargetVersionInfo(platform, raw, comparable.Value, compatibilityLevel);
        }

        /// <summary>
        /// Best-effort detection for callers that can safely proceed with a default when the version is
        /// unknown — e.g. selecting the model-ingest encoding, where JSON is the safe modern default.
        /// Unlike <see cref="Detect"/> this returns <c>null</c> instead of throwing on an undetectable or
        /// engine-ambiguous version; it applies no "never work blind" guard — that stays a caller concern
        /// (the pre-flight <see cref="PreFlightVersionGuard"/>).
        /// </summary>
        public static TargetVersionInfo TryDetect(IDbCommand command, Platform platform, string databaseName = null)
        {
            command.CommandText = GetVersionQuery(platform);
            var raw = command.ExecuteScalar()?.ToString();

            var comparable = VersionHelper.ParseDetectedVersion(raw, platform);
            if (comparable == null) return null;

            // MySQL and MariaDB share the VERSION() query; a mismatch is ambiguous, not throwing here.
            // raw is non-null here — a null raw fails ParseDetectedVersion above, which returns early.
            var isMariaDb = raw.IndexOf("MariaDB", StringComparison.OrdinalIgnoreCase) >= 0;
            if ((platform == Platform.MariaDb && !isMariaDb) || (platform == Platform.MySQL && isMariaDb))
                return null;

            int? compatibilityLevel = null;
            if (platform.GetBasePlatform() == Platform.SqlServer && !string.IsNullOrWhiteSpace(databaseName))
                compatibilityLevel = DetectSqlServerCompatibilityLevel(command, databaseName);

            return new TargetVersionInfo(platform, raw, comparable.Value, compatibilityLevel);
        }

        // The target database's compatibility_level selects the model-ingest encoding independently of
        // the server major version — a modern server can still host a database left at compat 100, which
        // takes the XML ingest/compare path (OPENJSON's JSON-path parse-errors below compat 130). The
        // database name comes from trusted configuration/package data; it is escaped as a string
        // literal (the codebase idiom) rather than parameterized so detection stays self-contained.
        private static int? DetectSqlServerCompatibilityLevel(IDbCommand command, string databaseName)
        {
            command.CommandText =
                $"SELECT compatibility_level FROM sys.databases WHERE name = N'{databaseName.Replace("'", "''")}'";
            var result = command.ExecuteScalar();
            return result != null && int.TryParse(result.ToString(), out var level) ? level : (int?)null;
        }
    }
}
