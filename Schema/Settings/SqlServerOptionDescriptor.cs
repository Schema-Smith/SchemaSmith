// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Text.RegularExpressions;
using Schema.Domain.Settings;

namespace Schema.Settings;

/// <summary>SQL Server <c>Option</c>: an <c>ALTER DATABASE … SET</c> keyword, read from <c>sys.databases</c>.</summary>
public sealed class SqlServerOptionDescriptor : ISettingDescriptor
{
    private enum Form { Keyword, EqualsValue, Seconds }

    // Name -> (sys.databases expression, how SET spells it). Only these are read: an option has to be mapped to its column.
    private static readonly Dictionary<string, (string Column, Form Form)> Options = new(StringComparer.OrdinalIgnoreCase)
    {
        ["COMPATIBILITY_LEVEL"] = ("compatibility_level", Form.EqualsValue),
        ["RECOVERY"] = ("recovery_model_desc", Form.Keyword),
        ["READ_COMMITTED_SNAPSHOT"] = ("is_read_committed_snapshot_on", Form.Keyword),
        ["ALLOW_SNAPSHOT_ISOLATION"] = ("snapshot_isolation_state_desc", Form.Keyword),
        ["AUTO_CLOSE"] = ("is_auto_close_on", Form.Keyword),
        ["AUTO_SHRINK"] = ("is_auto_shrink_on", Form.Keyword),
        ["AUTO_CREATE_STATISTICS"] = ("is_auto_create_stats_on", Form.Keyword),
        ["AUTO_UPDATE_STATISTICS"] = ("is_auto_update_stats_on", Form.Keyword),
        ["AUTO_UPDATE_STATISTICS_ASYNC"] = ("is_auto_update_stats_async_on", Form.Keyword),
        ["PAGE_VERIFY"] = ("page_verify_option_desc", Form.Keyword),
        ["TRUSTWORTHY"] = ("is_trustworthy_on", Form.Keyword),
        ["PARAMETERIZATION"] = ("CASE is_parameterization_forced WHEN 1 THEN 'FORCED' ELSE 'SIMPLE' END", Form.Keyword),
        ["TARGET_RECOVERY_TIME"] = ("target_recovery_time_in_seconds", Form.Seconds),
        ["DELAYED_DURABILITY"] = ("delayed_durability_desc", Form.EqualsValue),
        ["ANSI_NULLS"] = ("is_ansi_nulls_on", Form.Keyword),
        ["ANSI_PADDING"] = ("is_ansi_padding_on", Form.Keyword),
        ["ANSI_WARNINGS"] = ("is_ansi_warnings_on", Form.Keyword),
        ["ARITHABORT"] = ("is_arithabort_on", Form.Keyword),
        ["CONCAT_NULL_YIELDS_NULL"] = ("is_concat_null_yields_null_on", Form.Keyword),
        ["QUOTED_IDENTIFIER"] = ("is_quoted_identifier_on", Form.Keyword),
        ["NUMERIC_ROUNDABORT"] = ("is_numeric_roundabort_on", Form.Keyword),
        ["ACCELERATED_DATABASE_RECOVERY"] = ("is_accelerated_database_recovery_on", Form.EqualsValue),
    };

    public string Kind => SettingKinds.Option;
    public SettingScope Scope => SettingScope.Database;
    public bool PersistsAcrossRestart => true;

    public SettingReading Read(IDbCommand cmd, string name, string database)
    {
        if (!Options.TryGetValue(name, out var option))
            return SettingReading.Missing($"'{name}' is not a database option SchemaSmith reads; set it in a Setup script instead");
        // A column this version lacks (ACCELERATED_DATABASE_RECOVERY before 2019) fails the read; the step grades that.
        var row = SettingSql.FirstRow(cmd, $"SELECT CAST({option.Column} AS NVARCHAR(128)) FROM sys.databases WHERE name = @db", ("@db", database));
        return row == null ? SettingReading.Missing($"database '{database}' was not found") : new SettingReading(true, row[0], row[0], false, null);
    }

    public bool Equivalent(string declared, SettingReading current) =>
        SettingValueComparer.Boolish(Regex.Replace(declared ?? "", @"(?i)\s*seconds$", ""), current.Configured);

    public void Apply(IDbCommand cmd, string name, string value, string database)
    {
        if (!Options.TryGetValue(name, out var option))
            throw new SettingValueException($"'{name}' is not a database option SchemaSmith sets.");
        var keyword = name.ToUpperInvariant();
        var v = SettingSql.RequireToken(value, $"Option '{name}'");
        var clause = option.Form switch
        {
            Form.EqualsValue => $"{keyword} = {v}",
            Form.Seconds => $"{keyword} = {Regex.Replace(v, " SECONDS$", "")} SECONDS",
            _ => $"{keyword} {v}"
        };
        SettingSql.Execute(cmd, $"ALTER DATABASE {SettingSql.BracketQuote(database)} SET {clause}");
    }
}
