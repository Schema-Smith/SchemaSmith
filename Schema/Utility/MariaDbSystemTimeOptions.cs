// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Text.RegularExpressions;
using Schema.Domain.MySQL;

namespace Schema.Utility;

/// <summary>
/// A SYSTEM_TIME partitioning's INTERVAL, STARTS, LIMIT and AUTO appear only in <c>SHOW CREATE TABLE</c>, on the
/// <c>PARTITION BY SYSTEM_TIME</c> line: <c>PARTITION BY SYSTEM_TIME INTERVAL 1 MONTH STARTS TIMESTAMP'2026-01-01 00:00:00' AUTO</c>.
/// </summary>
public static class MariaDbSystemTimeOptions
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
    private static readonly Regex Line = new(@"PARTITION\s+BY\s+SYSTEM_TIME(?<options>[^\r\n]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex Interval = new(@"\bINTERVAL\s+(?<value>.+?)(?=\s+STARTS\b|\s+AUTO\b|\s+LIMIT\b|\s*$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex Starts = new(@"\bSTARTS\s+(?:TIMESTAMP\s*)?'(?<value>[^']*)'", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex Limit = new(@"\bLIMIT\s+(?<value>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex Auto = new(@"\bAUTO\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);

    public static MariaDbSystemTimePartitioning Read(string createTable)
    {
        var line = Line.Match(createTable ?? "");
        if (!line.Success) return null;
        var options = line.Groups["options"].Value;
        var interval = Interval.Match(options);
        var starts = Starts.Match(options);
        var limit = Limit.Match(options);
        var result = new MariaDbSystemTimePartitioning
        {
            Interval = interval.Success ? interval.Groups["value"].Value.Trim() : null,
            StartsAt = starts.Success ? starts.Groups["value"].Value : null,
            Limit = limit.Success ? long.Parse(limit.Groups["value"].Value) : null,
            Auto = Auto.IsMatch(options) ? true : null
        };
        return result.Interval == null && result.Limit == null && result.Auto == null ? null : result;
    }
}
