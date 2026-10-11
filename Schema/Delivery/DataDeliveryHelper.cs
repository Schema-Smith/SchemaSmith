// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Schema.Delivery;

/// <summary>
/// FK-aware data delivery ordering helper.
/// Classifies FK edges as required or deferred based on column nullability.
/// </summary>
internal static class DataDeliveryHelper
{
    // Membership: a table participates in delivery if any of its deliveries has a real (non-None) MergeType.
    internal static bool HasDeliverable(IDeliverableTable t) =>
        t.DataDeliveries != null && t.DataDeliveries.Any(d =>
            !string.IsNullOrEmpty(d.MergeType) &&
            !d.MergeType.Equals("None", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Classifies FK edges for a table as either required (NOT NULL FK columns)
    /// or deferred (all-nullable FK columns).
    /// </summary>
    public static (HashSet<string> RequiredDeps, List<string> DeferredColumns) ClassifyFKEdges(
        IDeliverableTable table, HashSet<string> deliveryTableNames, string platform)
    {
        var requiredDeps = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
        var deferredColumns = new List<string>();

        var tableSchema = TrimIdentifierQuotes(table.Schema ?? GetDefaultSchema(platform), platform);
        var tableName = TrimIdentifierQuotes(table.Name, platform);

        foreach (var fk in table.DeliverableForeignKeys)
        {
            string relatedKey;

            if (IsMySQL(platform))
            {
                var relatedTable = TrimIdentifierQuotes(fk.RelatedTable, platform);
                relatedKey = relatedTable;

                if (relatedTable.Equals(tableName, StringComparison.OrdinalIgnoreCase))
                    continue;
            }
            else
            {
                var relatedSchema = TrimIdentifierQuotes(fk.RelatedTableSchema ?? GetDefaultSchema(platform), platform);
                var relatedTable = TrimIdentifierQuotes(fk.RelatedTable, platform);
                relatedKey = $"{relatedSchema}.{relatedTable}";

                if (relatedSchema.Equals(tableSchema, StringComparison.OrdinalIgnoreCase) &&
                    relatedTable.Equals(tableName, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            if (!deliveryTableNames.Contains(relatedKey))
                continue;

            var fkColumnNames = ParseFKColumnNames(fk.Columns, platform);

            var allNullable = true;
            var columnFound = true;
            foreach (var fkColName in fkColumnNames)
            {
                var column = table.DeliverableColumns.FirstOrDefault(c =>
                    TrimIdentifierQuotes(c.Name, platform).Equals(fkColName, StringComparison.OrdinalIgnoreCase));

                if (column == null)
                {
                    columnFound = false;
                    allNullable = false;
                    break;
                }

                if (!column.Nullable)
                {
                    allNullable = false;
                    break;
                }
            }

            if (allNullable && columnFound)
            {
                foreach (var fkColName in fkColumnNames)
                {
                    if (!deferredColumns.Any(dc => dc.Equals(fkColName, StringComparison.OrdinalIgnoreCase)))
                        deferredColumns.Add(fkColName);
                }
            }
            else
            {
                requiredDeps.Add(relatedKey);
            }
        }

        return (requiredDeps, deferredColumns);
    }

    /// <summary>
    /// Builds a case-insensitive set of table keys from the list of deliverable tables.
    /// </summary>
    public static HashSet<string> BuildDeliveryTableSet(IEnumerable<IDeliverableTable> tables, string platform)
    {
        var set = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
        foreach (var t in tables)
        {
            if (IsMySQL(platform))
                set.Add(TrimIdentifierQuotes(t.Name, platform));
            else
                set.Add($"{TrimIdentifierQuotes(t.Schema ?? GetDefaultSchema(platform), platform)}.{TrimIdentifierQuotes(t.Name, platform)}");
        }
        return set;
    }

    /// <summary>
    /// Gets the table key for matching against the delivery set.
    /// </summary>
    public static string GetTableKey(IDeliverableTable table, string platform)
    {
        if (IsMySQL(platform))
            return TrimIdentifierQuotes(table.Name, platform);

        var schema = TrimIdentifierQuotes(table.Schema ?? GetDefaultSchema(platform), platform);
        return $"{schema}.{TrimIdentifierQuotes(table.Name, platform)}";
    }

    internal static List<string> ParseFKColumnNames(string columns, string platform)
    {
        if (string.IsNullOrWhiteSpace(columns))
            return new List<string>();

        return columns.Split(',')
            .Select(c => TrimIdentifierQuotes(c.Trim(), platform))
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .ToList();
    }

    // InnoDB checks a foreign key row by row, so a MySQL or MariaDB table referencing itself fails on a child listed
    // before its parent; SQL Server and PostgreSQL check at the end of the statement. A nullable self-reference is
    // deferred there like a cycle edge. A NOT NULL one has nothing to defer to and is left to the engine.
    internal static List<string> SelfReferencingNullableColumns(IDeliverableTable table, string platform)
    {
        var columns = new List<string>();
        if (!IsMySQL(platform)) return columns;
        var tableName = TrimIdentifierQuotes(table.Name, platform);
        foreach (var fk in table.DeliverableForeignKeys)
        {
            if (!TrimIdentifierQuotes(fk.RelatedTable, platform).Equals(tableName, StringComparison.OrdinalIgnoreCase)) continue;
            var fkColumns = ParseFKColumnNames(fk.Columns, platform);
            var allNullable = fkColumns.All(name => table.DeliverableColumns.Any(c =>
                TrimIdentifierQuotes(c.Name, platform).Equals(name, StringComparison.OrdinalIgnoreCase) && c.Nullable));
            if (!allNullable) continue;
            columns.AddRange(fkColumns.Where(name => !columns.Contains(name, StringComparer.OrdinalIgnoreCase)));
        }
        return columns;
    }

    internal static string TrimIdentifierQuotes(string value, string platform)
    {
        if (string.IsNullOrEmpty(value))
            return value ?? "";

        var trimmed = value.Trim();
        if (IsSqlServer(platform)) return trimmed.Trim('[', ']').Trim();
        if (IsPostgreSQL(platform)) return trimmed.Trim('"').Trim();
        if (IsMySQL(platform)) return trimmed.Trim('`').Trim();
        return trimmed;
    }

    internal static string GetDefaultSchema(string platform)
    {
        if (IsSqlServer(platform)) return "dbo";
        if (IsPostgreSQL(platform)) return "public";
        return "";
    }

    private static bool IsSqlServer(string platform) =>
        platform.Equals("SqlServer", StringComparison.OrdinalIgnoreCase);

    private static bool IsPostgreSQL(string platform) =>
        platform.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase);

    private static bool IsMySQL(string platform) =>
        platform.Equals("MySQL", StringComparison.OrdinalIgnoreCase);
}
