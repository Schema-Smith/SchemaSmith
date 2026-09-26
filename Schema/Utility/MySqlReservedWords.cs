// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Schema.Utility;

/// <summary>
/// Provides utilities for handling MySQL reserved words and identifier quoting.
/// Based on MySQL 8.0 reserved words list.
/// </summary>
public static class MySqlReservedWords
{
    private static readonly HashSet<string> ReservedWordSet = new(StringComparer.OrdinalIgnoreCase)
    {
        // MySQL 8.0 Reserved Words (complete list)
        "ACCESSIBLE", "ADD", "ALL", "ALTER", "ANALYZE", "AND", "AS", "ASC",
        "ASENSITIVE", "BEFORE", "BETWEEN", "BIGINT", "BINARY", "BLOB", "BOTH",
        "BY", "CALL", "CASCADE", "CASE", "CHANGE", "CHAR", "CHARACTER", "CHECK",
        "COLLATE", "COLUMN", "CONDITION", "CONSTRAINT", "CONTINUE", "CONVERT",
        "CREATE", "CROSS", "CUBE", "CUME_DIST", "CURRENT_DATE", "CURRENT_TIME",
        "CURRENT_TIMESTAMP", "CURRENT_USER", "CURSOR", "DATABASE", "DATABASES",
        "DAY_HOUR", "DAY_MICROSECOND", "DAY_MINUTE", "DAY_SECOND", "DEC",
        "DECIMAL", "DECLARE", "DEFAULT", "DELAYED", "DELETE", "DENSE_RANK",
        "DESC", "DESCRIBE", "DETERMINISTIC", "DISTINCT", "DISTINCTROW", "DIV",
        "DOUBLE", "DROP", "DUAL", "EACH", "ELSE", "ELSEIF", "EMPTY", "ENCLOSED",
        "ESCAPED", "EXCEPT", "EXISTS", "EXIT", "EXPLAIN", "FALSE", "FETCH",
        "FIRST_VALUE", "FLOAT", "FLOAT4", "FLOAT8", "FOR", "FORCE", "FOREIGN",
        "FROM", "FULLTEXT", "FUNCTION", "GENERATED", "GET", "GRANT", "GROUP",
        "GROUPING", "GROUPS", "HAVING", "HIGH_PRIORITY", "HOUR_MICROSECOND",
        "HOUR_MINUTE", "HOUR_SECOND", "IF", "IGNORE", "IN", "INDEX", "INFILE",
        "INNER", "INOUT", "INSENSITIVE", "INSERT", "INT", "INT1", "INT2", "INT3",
        "INT4", "INT8", "INTEGER", "INTERVAL", "INTO", "IO_AFTER_GTIDS",
        "IO_BEFORE_GTIDS", "IS", "ITERATE", "JOIN", "JSON_TABLE", "KEY", "KEYS",
        "KILL", "LAG", "LAST_VALUE", "LATERAL", "LEAD", "LEADING", "LEAVE",
        "LEFT", "LIKE", "LIMIT", "LINEAR", "LINES", "LOAD", "LOCALTIME",
        "LOCALTIMESTAMP", "LOCK", "LONG", "LONGBLOB", "LONGTEXT", "LOOP",
        "LOW_PRIORITY", "MASTER_BIND", "MASTER_SSL_VERIFY_SERVER_CERT", "MATCH",
        "MAXVALUE", "MEDIUMBLOB", "MEDIUMINT", "MEDIUMTEXT", "MIDDLEINT",
        "MINUTE_MICROSECOND", "MINUTE_SECOND", "MOD", "MODIFIES", "NATURAL",
        "NOT", "NO_WRITE_TO_BINLOG", "NTH_VALUE", "NTILE", "NULL", "NUMERIC",
        "OF", "ON", "OPTIMIZE", "OPTIMIZER_COSTS", "OPTION", "OPTIONALLY", "OR",
        "ORDER", "OUT", "OUTER", "OUTFILE", "OVER", "PARTITION", "PERCENT_RANK",
        "PRECISION", "PRIMARY", "PROCEDURE", "PURGE", "RANGE", "RANK", "READ",
        "READS", "READ_WRITE", "REAL", "RECURSIVE", "REFERENCES", "REGEXP",
        "RELEASE", "RENAME", "REPEAT", "REPLACE", "REQUIRE", "RESIGNAL",
        "RESTRICT", "RETURN", "REVOKE", "RIGHT", "RLIKE", "ROW", "ROWS",
        "ROW_NUMBER", "SCHEMA", "SCHEMAS", "SECOND_MICROSECOND", "SELECT",
        "SENSITIVE", "SEPARATOR", "SET", "SHOW", "SIGNAL", "SMALLINT", "SPATIAL",
        "SPECIFIC", "SQL", "SQLEXCEPTION", "SQLSTATE", "SQLWARNING",
        "SQL_BIG_RESULT", "SQL_CALC_FOUND_ROWS", "SQL_SMALL_RESULT", "SSL",
        "STARTING", "STORED", "STRAIGHT_JOIN", "SYSTEM", "TABLE", "TERMINATED",
        "THEN", "TINYBLOB", "TINYINT", "TINYTEXT", "TO", "TRAILING", "TRIGGER",
        "TRUE", "UNDO", "UNION", "UNIQUE", "UNLOCK", "UNSIGNED", "UPDATE",
        "USAGE", "USE", "USING", "UTC_DATE", "UTC_TIME", "UTC_TIMESTAMP",
        "VALUES", "VARBINARY", "VARCHAR", "VARCHARACTER", "VARYING", "VIRTUAL",
        "WHEN", "WHERE", "WHILE", "WINDOW", "WITH", "WRITE", "XOR",
        "YEAR_MONTH", "ZEROFILL"
    };

    /// <summary>
    /// Check if a word is a MySQL reserved word.
    /// </summary>
    public static bool IsReserved(string word)
    {
        if (string.IsNullOrEmpty(word)) return false;
        return ReservedWordSet.Contains(word.Trim());
    }

    /// <summary>
    /// Quote an identifier with backticks if it's a reserved word or contains special characters.
    /// </summary>
    public static string QuoteIfNeeded(string identifier)
    {
        if (string.IsNullOrEmpty(identifier)) return identifier;

        var trimmed = identifier.Trim();

        // Already quoted
        if (trimmed.StartsWith("`") && trimmed.EndsWith("`"))
            return trimmed;

        // Needs quoting if reserved word or contains special chars
        if (IsReserved(trimmed) || NeedsQuoting(trimmed))
            return Quote(trimmed);

        return trimmed;
    }

    /// <summary>
    /// Always quote an identifier with backticks, escaping embedded backticks.
    /// <para>Accepts either a raw name or an already-wrapped one, so re-quoting is idempotent. The unwrap is
    /// ONE PAIR, via <see cref="Unquote"/>: this used <c>.Trim('`')</c>, which strips EVERY backtick at each
    /// end, so a raw name beginning or ending with a backtick was destroyed on the way in — the name
    /// <c>a`</c> came out as <c>`a`</c>, which is the name <c>a</c>. Ordinary names are unaffected either
    /// way, which is why it went unnoticed. The deployed <c>SchemaSmith_QuoteIdentifier</c> carried the same
    /// flaw and was fixed with it, so the two halves still agree.</para>
    /// <para>The residual is unavoidable rather than an oversight: a RAW name that itself begins and ends
    /// with a backtick cannot be told apart from the delimited form of the name between them, so <c>`x`</c>
    /// reads as <c>x</c>. SQL Server carries the identical limit with <c>[ ]</c>, both halves agree on that
    /// reading, and the end-user docs state it.</para>
    /// </summary>
    public static string Quote(string identifier)
    {
        if (string.IsNullOrEmpty(identifier)) return "``";

        var escaped = Unquote(identifier).Replace("`", "``");
        return $"`{escaped}`";
    }

    /// <summary>
    /// Remove backticks from an identifier.
    /// </summary>
    public static string Unquote(string identifier)
    {
        if (string.IsNullOrEmpty(identifier)) return identifier;

        var trimmed = identifier.Trim();
        // Length >= 2 is load-bearing, not defensive. A LONE backtick both starts and ends with one, so
        // without it Substring(1, -1) threw ArgumentOutOfRangeException -- and Unwrap's SQL Server and
        // PostgreSQL arms both already guarded on length while this one did not. The deployed
        // SchemaSmith_StripBacktickWrapping guards and returns the input unchanged, so the two halves of the
        // convention disagreed on exactly that input: one returned a value, the other threw.
        if (trimmed.Length >= 2 && trimmed.StartsWith("`") && trimmed.EndsWith("`"))
        {
            var unquoted = trimmed.Substring(1, trimmed.Length - 2);
            return unquoted.Replace("``", "`");
        }
        return trimmed;
    }

    /// <summary>
    /// Unquote each identifier in a comma-separated list.
    /// </summary>
    public static string UnquoteList(string identifiers)
    {
        if (string.IsNullOrEmpty(identifiers)) return identifiers;
        return string.Join(",", identifiers.Split(',').Select(id => Unquote(id.Trim())));
    }

    /// <summary>
    /// Quote each identifier in a comma-separated list with backticks.
    /// </summary>
    public static string QuoteList(string identifiers)
    {
        if (string.IsNullOrEmpty(identifiers)) return identifiers;
        return string.Join(",", identifiers.Split(',').Select(id => Quote(id.Trim())));
    }

    /// <summary>
    /// Check if an identifier needs quoting (contains special characters or starts with a number).
    /// </summary>
    private static bool NeedsQuoting(string identifier)
    {
        if (string.IsNullOrEmpty(identifier)) return false;

        // Starts with a digit
        if (char.IsDigit(identifier[0])) return true;

        // Contains non-alphanumeric characters (except underscore)
        foreach (var c in identifier)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
                return true;
        }

        return false;
    }
}
