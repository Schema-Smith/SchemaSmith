// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Schema.Settings;

/// <summary>Compares a declared value with the server's the engine's way, so an equivalent spelling is not drift.</summary>
public static class SettingValueComparer
{
    private static readonly Regex Quantity = new(@"^(-?\d+(?:\.\d+)?)\s*([A-Za-z]*)$", RegexOptions.CultureInvariant);

    // ON / 1 / true / yes are one value, as are their opposites; numbers compare as numbers; anything else ignores case.
    public static bool Boolish(string declared, string current)
    {
        var a = Canonical(declared);
        var b = Canonical(current);
        if (decimal.TryParse(a, NumberStyles.Number, CultureInfo.InvariantCulture, out var x)
            && decimal.TryParse(b, NumberStyles.Number, CultureInfo.InvariantCulture, out var y))
            return x == y;
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    public static bool CaseInsensitive(string declared, string current) =>
        string.Equals(Collapse(declared), Collapse(current), StringComparison.OrdinalIgnoreCase);

    // PostgreSQL: a quantity with or without a unit is converted to bytes or milliseconds, using the parameter's own base
    // unit (pg_settings.unit, which can be a multiple such as 8kB) for a bare number.
    public static bool PostgreSql(string declared, string current, string unit)
    {
        var a = InBaseUnits(Unquote(declared), unit);
        var b = InBaseUnits(Unquote(current), unit);
        if (a != null && b != null) return a == b;
        return Boolish(Unquote(declared), Unquote(current));
    }

    private static decimal? InBaseUnits(string value, string unit)
    {
        var m = Quantity.Match(value ?? "");
        if (!m.Success) return null;
        var number = decimal.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var factor = m.Groups[2].Value.Length > 0 ? UnitFactor(m.Groups[2].Value) : UnitFactor(unit ?? "") ?? 1;
        return factor == null ? null : number * factor;
    }

    private static decimal? UnitFactor(string unit)
    {
        var m = Quantity.Match(unit);
        var multiple = 1m;
        if (m.Success)
        {
            multiple = decimal.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            unit = m.Groups[2].Value;
        }
        decimal? factor = unit switch
        {
            "B" => 1m, "kB" => 1024m, "MB" => 1024m * 1024, "GB" => 1024m * 1024 * 1024, "TB" => 1024m * 1024 * 1024 * 1024,
            "us" => 0.001m, "ms" => 1m, "s" => 1000m, "min" => 60_000m, "h" => 3_600_000m, "d" => 86_400_000m,
            "" => 1m,
            _ => null
        };
        return factor * multiple;
    }

    private static string Canonical(string value)
    {
        var v = Collapse(value);
        return v.ToLowerInvariant() switch
        {
            "on" or "true" or "yes" => "1",
            "off" or "false" or "no" => "0",
            _ => v
        };
    }

    private static string Collapse(string value) => Regex.Replace((value ?? "").Trim(), @"\s+", " ");

    private static string Unquote(string value)
    {
        var v = (value ?? "").Trim();
        return v.Length >= 2 && v[0] == '\'' && v[^1] == '\'' ? v[1..^1].Replace("''", "'") : v;
    }
}
