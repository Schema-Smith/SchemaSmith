// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using Schema.Domain;

namespace Schema.Utility;

/// <summary>What a discovered schema name was refused for.</summary>
public enum SchemaNameViolationKind
{
    /// <summary>A platform-owned namespace (dbo, sys, public, pg_catalog, ...).</summary>
    Reserved,
    /// <summary>A character below U+0020.</summary>
    ControlCharacter,
    /// <summary>One of <see cref="SchemaDiscoveryRules.DisallowedCharacters"/>.</summary>
    DisallowedCharacter,
    /// <summary>The same name returned twice (case-insensitively).</summary>
    Duplicate
}

/// <summary>
/// The first name a <see cref="SchemaDiscoveryRules.Validate"/> call refused. <see cref="Detail"/> describes the
/// violation from the name onward -- "reserved schema name 'dbo'. Platform-owned ..." -- so a caller can prefix it
/// with its own context, which is exactly how the deploy builds its error message.
/// </summary>
public sealed record SchemaNameViolation(string Name, SchemaNameViolationKind Kind, string Detail);

/// <summary>
/// Which schema names a <c>SchemaIdentificationScript</c> may return. This is the rule the deploy applies during
/// schema discovery, exposed so anything else reading the same script's output applies the same rule rather than
/// a transcription of it.
/// <para>A single refused name fails discovery for that database's WHOLE list, so a caller presenting the
/// script's output as choices should validate the list as a unit, as <see cref="Validate"/> does.</para>
/// <para>Schema templates are offered on SQL Server and PostgreSQL only; on any other platform no name is
/// reserved and only the character and duplicate rules apply.</para>
/// </summary>
public static class SchemaDiscoveryRules
{
    // SQL Server: dbo / sys / INFORMATION_SCHEMA / guest plus the db_* fixed database roles, which double as schemas.
    private static readonly HashSet<string> SqlServerReserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "dbo", "sys", "INFORMATION_SCHEMA", "guest",
        "db_owner", "db_accessadmin", "db_securityadmin", "db_ddladmin", "db_backupoperator",
        "db_datareader", "db_datawriter", "db_denydatareader", "db_denydatawriter"
    };

    // PostgreSQL fixed names; pg_temp_* and pg_toast_temp_* are matched by prefix in IsReserved.
    private static readonly HashSet<string> PostgreSqlReservedFixed = new(StringComparer.OrdinalIgnoreCase)
    {
        "public", "pg_catalog", "pg_toast", "information_schema"
    };

    private static readonly char[] Disallowed = { ']', '[', '"', '\'', '{', '}' };

    /// <summary>
    /// Characters a discovered schema name may not contain: they would corrupt SQL quoting (<c>[ ]</c>), JSON
    /// serialization (<c>" '</c>) or token substitution (<c>{ }</c>). Letters (including non-ASCII), digits,
    /// underscores, hyphens, dots and spaces are all accepted.
    /// </summary>
    public static IReadOnlyCollection<char> DisallowedCharacters => Disallowed;

    /// <summary>True when <paramref name="name"/> is a namespace the platform owns.</summary>
    public static bool IsReserved(string name, Platform platform) => platform switch
    {
        Platform.SqlServer => SqlServerReserved.Contains(name),
        Platform.PostgreSQL => PostgreSqlReservedFixed.Contains(name)
                               || name.StartsWith("pg_temp_", StringComparison.OrdinalIgnoreCase)
                               || name.StartsWith("pg_toast_temp_", StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    /// <summary>
    /// The first violation in <paramref name="names"/>, in order, or <c>null</c> when every name is accepted.
    /// Null and blank entries are skipped, as the deploy skips them. Per name the checks run in the deploy's
    /// order: reserved, control character, disallowed character, then duplicate.
    /// </summary>
    public static SchemaNameViolation Validate(IEnumerable<string> names, Platform platform)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            var violation = ValidateName(name, platform);
            if (violation != null) return violation;
            if (!seen.Add(name))
                return new SchemaNameViolation(name, SchemaNameViolationKind.Duplicate,
                    $"duplicate schema name '{name}'. " +
                    "Each iteration target must appear exactly once in the result set — " +
                    "fix the discovery query to project distinct schema names (e.g. add SELECT DISTINCT or remove the join causing the fan-out).");
        }
        return null;
    }

    /// <summary>
    /// The violation for a single name, or <c>null</c>. Does not check for duplicates -- that needs the list;
    /// use <see cref="Validate"/>.
    /// </summary>
    public static SchemaNameViolation ValidateName(string name, Platform platform)
    {
        if (IsReserved(name, platform))
            return new SchemaNameViolation(name, SchemaNameViolationKind.Reserved,
                $"reserved schema name '{name}'. " +
                "Platform-owned namespaces (dbo/sys/public/pg_catalog/etc.) cannot be iteration targets — " +
                "put shared content in a regular template that runs earlier in TemplateOrder. " +
                "(The same name remains valid as a RelatedTableSchema literal or as an explicit reference in free-form SQL.)");

        foreach (var ch in name)
        {
            if (ch < ' ')
                return new SchemaNameViolation(name, SchemaNameViolationKind.ControlCharacter,
                    $"schema name '{name}' containing control character (U+{(int)ch:X4}). " +
                    "Schema names cannot contain control characters — clean the discovery query's result.");
        }

        foreach (var bad in Disallowed)
        {
            if (name.IndexOf(bad) >= 0)
                return new SchemaNameViolation(name, SchemaNameViolationKind.DisallowedCharacter,
                    $"schema name '{name}' containing disallowed character '{bad}'. " +
                    "Schema names cannot contain brackets, quotes, or braces — these characters would corrupt " +
                    "SchemaSmith's SQL quoting, JSON serialization, or token substitution. " +
                    "Rename the schema (or fix the discovery query) to use only letters, digits, underscores, hyphens, dots, or spaces.");
        }
        return null;
    }
}
