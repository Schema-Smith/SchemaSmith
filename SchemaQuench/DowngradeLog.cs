// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.Linq;
using Schema.Domain;

namespace SchemaQuench;

/// <summary>
/// The console lines for features a deploy skipped under the warn policy. SQL Server, MySQL and MariaDB print one at the
/// step that skips the feature; PostgreSQL only records the audit row, so its lines come from the rows.
/// </summary>
internal static class DowngradeLog
{
    public static IEnumerable<string> Lines(Platform platform, IEnumerable<ChangeAuditRow> rows) =>
        platform.GetBasePlatform() != Platform.PostgreSQL
            ? []
            : rows.Where(r => r.Action == "downgraded").Select(r => $"  Skipping {r.ObjectType} on {r.ObjectName} (downgraded)");
}
