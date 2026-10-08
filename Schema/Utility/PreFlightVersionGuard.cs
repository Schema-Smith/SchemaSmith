// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Schema.Domain;

namespace Schema.Utility
{
    /// <summary>
    /// Enforces SchemaSmith's intrinsic per-engine version floor — and, for SQL Server, the target
    /// database's compatibility level — before any kindling, deployment, or extraction. This is
    /// distinct from the opt-in <c>Product.MinimumVersion</c> guardrail: the floor is structural, the
    /// version below which the engine scripts do not run at all. The SQL Server server binary must be
    /// 2008+ and the target database's compatibility level must be 100+; below compat 130 the model is
    /// ingested and compared as XML (<c>OPENJSON</c>'s JSON-path is a parse error below compat 130).
    /// Failing here produces a clear "unsupported version" message instead of a raw engine error mid-run.
    /// </summary>
    public static class PreFlightVersionGuard
    {
        // SchemaSmith supports SQL Server down to compatibility level 100 (SQL Server 2008): at or above
        // compat 130 it ingests the model via OPENJSON; below 130 it switches to the XML ingest/compare
        // encoding (OPENJSON's JSON-path parse-errors below compat 130). Compat 100 is the floor — below
        // it (SQL Server 2005 / compat 90) the engine scripts do not run.
        private const int SqlServerCompatFloor = 100;

        /// <summary>
        /// Throws when <paramref name="info"/> is below the engine floor, or (SQL Server) when its
        /// detected database compatibility level is below 100. <paramref name="databaseLabel"/> names
        /// the database in the compatibility-level message.
        /// </summary>
        public static void CheckOrThrow(TargetVersionInfo info, string serverLabel, string databaseLabel = null)
        {
            if (VersionHelper.IsBelowFloor(info))
                throw new RunFailedException(
                    $"{serverLabel}: detected {info.Platform} version {VersionHelper.DisplayVersion(info)} is below the minimum " +
                    $"supported version {VersionHelper.HardFloorDisplay(info.Platform)}. SchemaSmith cannot run against it.");

            if (info.Platform.GetBasePlatform() == Platform.SqlServer &&
                info.CompatibilityLevel is { } compat && compat < SqlServerCompatFloor)
                throw new RunFailedException(
                    $"{serverLabel}: database {databaseLabel} is at compatibility level {compat}; SchemaSmith requires " +
                    $"{SqlServerCompatFloor} (SQL Server 2008) or higher. Raise it with " +
                    $"ALTER DATABASE ... SET COMPATIBILITY_LEVEL = {SqlServerCompatFloor}.");
        }
    }
}
