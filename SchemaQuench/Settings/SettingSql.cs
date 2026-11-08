// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Data;
using System.Text.RegularExpressions;

namespace SchemaQuench.Settings;

internal static class SettingSql
{
    private static readonly Regex Token = new(@"^[A-Za-z0-9_]+( [A-Za-z0-9_]+)*$", RegexOptions.CultureInvariant);

    // ALTER DATABASE SET and SCOPED CONFIGURATION take keywords and numbers, not literals: anything else is refused, never spliced.
    internal static string RequireToken(string value, string display)
    {
        var v = Regex.Replace((value ?? "").Trim(), @"\s+", " ");
        if (!Token.IsMatch(v))
            throw new SettingValueException($"'{value}' for {display} is not a keyword or number the engine takes there.");
        return v.ToUpperInvariant();
    }

    internal static string BracketQuote(string name) => "[" + name.Replace("]", "]]") + "]";
    internal static string DoubleQuote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
    internal static string BacktickQuote(string name) => "`" + name.Replace("`", "``") + "`";
    internal static string StandardLiteral(string value) => "'" + (value ?? "").Replace("'", "''") + "'";
    internal static string MySqlLiteral(string value) => "'" + (value ?? "").Replace("\\", "\\\\").Replace("'", "''") + "'";

    internal static void Execute(IDbCommand cmd, string sql, params (string Name, object Value)[] parameters)
    {
        cmd.Parameters.Clear();
        cmd.CommandText = sql;
        AddParameters(cmd, parameters);
        try
        {
            cmd.ExecuteNonQuery();
        }
        finally
        {
            cmd.Parameters.Clear();
        }
    }

    private static void AddParameters(IDbCommand cmd, (string Name, object Value)[] parameters)
    {
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            cmd.Parameters.Add(p);
        }
    }

    // Runs a query with named parameters and returns the first row's columns as strings, or null when there is no row.
    internal static string[] FirstRow(IDbCommand cmd, string sql, params (string Name, object Value)[] parameters)
    {
        cmd.Parameters.Clear();
        cmd.CommandText = sql;
        AddParameters(cmd, parameters);
        try
        {
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            var row = new string[reader.FieldCount];
            for (var i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : System.Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture);
            return row;
        }
        finally
        {
            cmd.Parameters.Clear();
        }
    }
}
