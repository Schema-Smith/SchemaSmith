// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// Version-gate helpers for test fixtures that do NOT inherit <see cref="BaseTableQuenchTests"/> (which carries
/// the instance-method equivalents). Used to Assert.Ignore feature-gap tests below a MySQL-family floor.
/// </summary>
internal static class TestVersionGates
{
    /// <summary>Whether SchemaSmith deploys and reads CHECK constraints on the target: MySQL 8.0.16 and MariaDB 10.2.22, where
    /// INFORMATION_SCHEMA.CHECK_CONSTRAINTS arrived (SchemaSmith_SupportsCheckConstraints); SQL Server and PostgreSQL always.</summary>
    public static bool SupportsCheckConstraints(Platform platform, string connectionString)
    {
        if (platform.GetBasePlatform() != Platform.MySQL) return true;
        using var conn = DbConnectionFactory.ForPlatform(platform).GetDbConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT VERSION()";
        var patch = VersionHelper.ParsePatchComparable(cmd.ExecuteScalar()?.ToString(), platform);
        return patch == null || patch >= (platform == Platform.MariaDb ? 100222 : 80016);
    }
}
