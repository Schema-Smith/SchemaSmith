// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SchemaQuench;

/// <summary>
/// Pure rendering of collected <see cref="FailureRecord"/>s into the end-of-run roll-up block
/// (a one-line phase-grouped header summary followed by a per-failure entry). No I/O — the caller
/// routes the returned string to the FailureLog logger and the console. Empty string ⇒ no failures.
/// </summary>
public static class FailureReportWriter
{
    public static string Render(IReadOnlyCollection<FailureRecord> records)
    {
        if (records.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        var byPhase = records.GroupBy(r => r.Phase).ToList();
        var summary = string.Join(", ", byPhase.Select(g => $"{g.Count()} {g.Key}"));
        sb.AppendLine($"{records.Count} failure(s): {summary}");
        sb.AppendLine();

        foreach (var record in records)
        {
            sb.AppendLine($"─── FAILED  [{record.Phase}]  {record.ScopeKey} ───");
            sb.AppendLine($"Error: {record.Error}");
            sb.AppendLine($"Debug SQL: {record.ArtifactPath ?? (DebugSql.Enabled ? "n/a" : "rerun with --WriteDebugSql to write it")}");
            if (record.ContextTail.Count == 0)
                sb.AppendLine("Context: none captured");
            else
            {
                sb.AppendLine($"Context (last {record.ContextTail.Count} lines):");
                foreach (var line in record.ContextTail) sb.AppendLine($"  {line}");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
