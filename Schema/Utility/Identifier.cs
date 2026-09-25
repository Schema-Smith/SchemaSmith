// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Schema.Domain;

namespace Schema.Utility;

/// <summary>
/// Platform-aware identifier helpers. Today this is a single delimiter-stripping
/// method used for same-schema identifier comparison in schema-template scrubbing
/// (issue #256), but the helper is also the right home for any future
/// extraction/data-delivery/scrub site that needs identifiers compared as
/// equivalent regardless of how either side was delimited.
/// </summary>
public static class Identifier
{
    /// <summary>
    /// Escapes the platform delimiter inside a raw identifier so it can be safely wrapped:
    /// SQL Server <c>]</c>→<c>]]</c>, PostgreSQL <c>"</c>→<c>""</c>, MySQL/MariaDB <c>`</c>→<c>``</c>.
    /// The caller supplies the surrounding delimiters (e.g. <c>[{EscapeDelimited(name, p)}]</c>).
    /// Null/empty pass through unchanged. This is the write-side inverse of <see cref="Unwrap"/>:
    /// an identifier from trusted metadata that happens to contain the delimiter character would
    /// otherwise produce broken (not injected) SQL.
    /// </summary>
    public static string EscapeDelimited(string value, Platform platform)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return platform.GetBasePlatform() switch
        {
            Platform.SqlServer => value.Replace("]", "]]"),
            Platform.PostgreSQL => value.Replace("\"", "\"\""),
            Platform.MySQL => value.Replace("`", "``"),
            _ => value
        };
    }

    /// <summary>
    /// Strips one layer of platform-appropriate delimiter wrapping from
    /// <paramref name="value"/>. Bracket-quoted SQL Server identifiers (<c>[name]</c>),
    /// double-quoted PostgreSQL identifiers (<c>"name"</c>), and backtick-quoted MySQL
    /// identifiers (<c>`name`</c>) are unwrapped; embedded delimiter escape sequences
    /// (<c>]]</c>, <c>""</c>, <c>``</c>) are collapsed.
    /// </summary>
    public static string Unwrap(string value, Platform platform)
    {
        if (string.IsNullOrEmpty(value)) return value;
        switch (platform.GetBasePlatform())
        {
            case Platform.SqlServer:
                if (value.Length >= 2 && value[0] == '[' && value[^1] == ']')
                    return value.Substring(1, value.Length - 2).Replace("]]", "]");
                return value;
            case Platform.PostgreSQL:
                if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                    return value.Substring(1, value.Length - 2).Replace("\"\"", "\"");
                return value;
            case Platform.MySQL:
                return MySqlReservedWords.Unquote(value);
            default:
                return value;
        }
    }

    /// <summary>
    /// Splits a (possibly) schema-qualified identifier into its unwrapped
    /// <c>(Schema, Name)</c> parts, treating the <c>.</c> separator as significant only
    /// when it is OUTSIDE platform delimiter wrapping — so a delimited name that itself
    /// contains a dot (<c>[my.schema]</c>, <c>"my.schema"</c>) is not mis-split. Both
    /// parts are returned <see cref="Unwrap"/>-ed. <c>Schema</c> is <c>null</c> when the
    /// value carries no top-level qualifier. Splits at the FIRST top-level dot (two-part
    /// semantics); a multi-dot remainder is returned whole as <c>Name</c>.
    /// </summary>
    public static (string Schema, string Name) SplitQualifiedName(string value, Platform platform)
    {
        if (string.IsNullOrEmpty(value)) return (null, value);

        var (open, close) = platform.GetBasePlatform() switch
        {
            Platform.SqlServer => ('[', ']'),
            Platform.PostgreSQL => ('"', '"'),
            Platform.MySQL => ('`', '`'),
            _ => ('\0', '\0')
        };

        var inWrap = false;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (open != '\0')
            {
                if (!inWrap && c == open) { inWrap = true; continue; }
                if (inWrap && c == close) { inWrap = false; continue; }
            }
            if (!inWrap && c == '.')
                return (Unwrap(value.Substring(0, i), platform), Unwrap(value.Substring(i + 1), platform));
        }

        return (null, Unwrap(value, platform));
    }
}
