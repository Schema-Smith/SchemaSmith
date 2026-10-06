// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Linq;
using Schema.Domain;

namespace SchemaQuench;

/// <summary>
/// Tables in one template whose names differ only in case. A MySQL or MariaDB server with lower_case_table_names >= 1
/// stores both as the same table, so deploying such a package there would merge two declarations onto one table.
/// A group whose every member is gated is a variant set (one applies per database), not a collision.
/// </summary>
internal static class FoldedTableNameCollisions
{
    public static IReadOnlyList<string> Find(Template template) =>
        template.Tables
            .Where(t => !string.IsNullOrWhiteSpace(t.Name))
            .GroupBy(t => t.Name.Trim('`'), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.ToList())
            .Where(g => g.Select(t => t.Name.Trim('`')).Distinct(StringComparer.Ordinal).Count() > 1
                        && !g.All(t => !string.IsNullOrWhiteSpace(t.ShouldApplyExpression)))
            .Select(g => string.Join(", ", g.Select(t => t.Name.Trim('`')).Distinct(StringComparer.Ordinal)))
            .ToList();
}
