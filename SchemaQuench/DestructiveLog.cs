// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SchemaQuench;

/// <summary>The log lines for the destructive changes a step recorded (H3).</summary>
internal static class DestructiveLog
{
    public const string Action = "destructive";

    public static string Describe(ChangeAuditRow row)
    {
        var what = row.ObjectType switch
        {
            "table drop" => $"table {row.ObjectName} dropped",
            "column drop" => $"column {row.ObjectName} dropped",
            "column replaced" => $"column {row.ObjectName} replaced ({row.Detail}): dropped and added again without its values",
            "type change" => $"column {row.ObjectName} changed {row.Detail}, which changes its values without an error",
            _ => $"{row.ObjectType} {row.ObjectName}{(string.IsNullOrEmpty(row.Detail) ? "" : " " + row.Detail)}",
        };
        return row.EstimatedRows is { } rows ? $"{what} (about {rows.ToString("N0", CultureInfo.InvariantCulture)} rows)" : what;
    }

    /// <summary>Warnings for the changes made under warn, information lines for the rest; refusals are the step's failure.</summary>
    public static IEnumerable<(bool Warn, string Line)> Lines(string scope, IEnumerable<ChangeAuditRow> rows) =>
        rows.Where(r => r.Action == Action).Select(r => r.Mode switch
        {
            "warned" => (true, $"{scope} Destructive change: {Describe(r)}. Name it in AcceptDestructive to record it as intended."),
            "accepted" => (false, $"  Destructive change (accepted): {Describe(r)}"),
            "allowed" => (false, $"  Destructive change: {Describe(r)}"),
            _ => (false, (string)null),
        }).Where(l => l.Item2 != null);
}
