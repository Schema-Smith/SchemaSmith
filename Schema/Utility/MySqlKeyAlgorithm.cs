// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Text.RegularExpressions;

namespace Schema.Utility;

/// <summary>
/// KEY partitioning's <c>ALGORITHM</c> appears only in <c>SHOW CREATE TABLE</c>, and only when it is 1: MySQL writes it as a
/// version comment between the method and the column list (<c>/*!50100 PARTITION BY KEY */ /*!50611 ALGORITHM = 1 */</c>),
/// MariaDB inline (<c>PARTITION BY KEY ALGORITHM = 1 (`id`)</c>).
/// </summary>
public static class MySqlKeyAlgorithm
{
    private static readonly Regex Clause = new(
        @"PARTITION\s+BY\s+(?:LINEAR\s+)?KEY\b(?:\s*\*/\s*/\*!\d+)?\s*ALGORITHM\s*=\s*(?<algorithm>[12])\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    public static int? Read(string createTable)
    {
        if (string.IsNullOrEmpty(createTable)) return null;
        var match = Clause.Match(createTable);
        return match.Success ? int.Parse(match.Groups["algorithm"].Value) : null;
    }
}
