// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;

namespace SchemaQuench;

public enum SchemaBoundDependentKind { Module, IndexedView, CheckConstraint, ComputedColumn, Other }

/// <summary>
/// One object SQL Server will not let a module change under: a schema-bound view or function, an indexed view, a
/// CHECK constraint or a computed column that references it with SCHEMABINDING. Depth is the position in the chain
/// (a view bound to a view bound to the module is deeper); the chain is dropped deepest-first.
/// </summary>
public sealed record SchemaBoundDependent(
    SchemaBoundDependentKind Kind, string Schema, string Name, string ParentSchema, string ParentName, string ColumnName,
    int Depth, bool IsEncrypted, string TypeDescription, IReadOnlyList<string> IndexesOnColumn)
{
    public string Display => Kind switch
    {
        SchemaBoundDependentKind.CheckConstraint => $"CHECK constraint {Name} on {ParentSchema}.{ParentName}",
        SchemaBoundDependentKind.ComputedColumn => $"computed column {ParentSchema}.{ParentName}.{ColumnName}",
        SchemaBoundDependentKind.IndexedView => $"indexed view {Schema}.{Name}",
        SchemaBoundDependentKind.Module => $"schema-bound {TypeDescription} {Schema}.{Name}",
        _ => $"{TypeDescription} {Schema}.{Name}"
    };
}

/// <summary>What the package declares, keyed by lower-cased bare names, so a dependent can be told from a stranger.</summary>
public sealed record DeclaredTable(ISet<string> CheckConstraints, ISet<string> ComputedColumns, ISet<string> Indexes);

public sealed record DeclaredObjects(ISet<string> Modules, ISet<string> IndexedViews, IReadOnlyDictionary<string, DeclaredTable> Tables)
{
    public static string Key(string schema, string name) => $"{Bare(schema)}.{Bare(name)}";
    public static string Bare(string name) => (name ?? "").Trim().TrimStart('[').TrimEnd(']').Trim().ToLowerInvariant();
}

public sealed record SchemaBoundDrop(SchemaBoundDependent Dependent, IReadOnlyList<string> Statements, string AuditType, string AuditName);

/// <summary>The drops, in order, when every dependent is the package's; otherwise the ones that are not, which stop the deploy.</summary>
public sealed record SchemaBoundPlan(IReadOnlyList<SchemaBoundDrop> Drops, IReadOnlyList<SchemaBoundDependent> Undeclared, IReadOnlyList<SchemaBoundDependent> Encrypted)
{
    public bool CanProceed => Undeclared.Count == 0 && Encrypted.Count == 0;
}

/// <summary>
/// SQL Server refuses to alter a module that a schema-bound object references (error 3729). The catalog can list the
/// dependents before anything is dropped, so the deploy drops the ones the package declares -- a schema-bound view
/// or function it scripts, an indexed view it declares, a CHECK constraint or computed column in a declared table --
/// knowing the run puts them back, and stops with the others named, because dropping one SchemaSmith cannot recreate
/// would destroy something the package never described.
/// </summary>
public static class SchemaBoundDependents
{
    private const string WalkSql = @"
;WITH Walk AS (
  SELECT d.referencing_id AS ObjectId, d.referencing_minor_id AS MinorId, 1 AS Depth
    FROM sys.sql_expression_dependencies d
   WHERE d.referenced_id = OBJECT_ID(@p_Qualified) AND d.is_schema_bound_reference = 1
  UNION ALL
  SELECT d.referencing_id, d.referencing_minor_id, w.Depth + 1
    FROM Walk w
    JOIN sys.sql_expression_dependencies d ON d.referenced_id = w.ObjectId AND d.is_schema_bound_reference = 1
   WHERE w.MinorId = 0 AND w.Depth < 32)
SELECT w.ObjectId, w.MinorId, MAX(w.Depth) AS Depth, o.[type], OBJECT_SCHEMA_NAME(w.ObjectId), OBJECT_NAME(w.ObjectId),
       OBJECT_SCHEMA_NAME(o.parent_object_id), OBJECT_NAME(o.parent_object_id), c.[name],
       CASE WHEN o.[type] = 'V' AND EXISTS (SELECT 1 FROM sys.indexes i WHERE i.[object_id] = w.ObjectId AND i.index_id > 0) THEN 1 ELSE 0 END,
       CASE WHEN o.[type] IN ('V', 'FN', 'IF', 'TF') AND OBJECT_DEFINITION(w.ObjectId) IS NULL THEN 1 ELSE 0 END,
       STUFF((SELECT ',' + i.[name]
                FROM sys.indexes i
                JOIN sys.index_columns ic ON ic.[object_id] = i.[object_id] AND ic.index_id = i.index_id
               WHERE i.[object_id] = w.ObjectId AND ic.column_id = w.MinorId AND i.index_id > 0
               ORDER BY i.[name]
                 FOR XML PATH('')), 1, 1, '')
  FROM Walk w
  JOIN sys.objects o ON o.[object_id] = w.ObjectId
  LEFT JOIN sys.columns c ON c.[object_id] = w.ObjectId AND c.column_id = w.MinorId AND w.MinorId > 0
 GROUP BY w.ObjectId, w.MinorId, o.[type], o.parent_object_id, c.[name]
 ORDER BY MAX(w.Depth) DESC, OBJECT_NAME(w.ObjectId)
OPTION (MAXRECURSION 32)";

    public static List<SchemaBoundDependent> Read(IDbCommand cmd, string schema, string name)
    {
        var results = new List<SchemaBoundDependent>();
        cmd.CommandText = WalkSql.Replace("@p_Qualified", "N'" + $"[{schema.Replace("]", "]]")}].[{name.Replace("]", "]]")}]".Replace("'", "''") + "'");
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var minorId = reader.GetInt32(1);
            var type = reader.GetString(3).Trim();
            var isIndexedView = reader.GetInt32(9) == 1;
            var kind = minorId > 0 ? SchemaBoundDependentKind.ComputedColumn
                : type == "C" ? SchemaBoundDependentKind.CheckConstraint
                : type == "V" && isIndexedView ? SchemaBoundDependentKind.IndexedView
                : type is "V" or "FN" or "IF" or "TF" ? SchemaBoundDependentKind.Module
                : SchemaBoundDependentKind.Other;
            var indexes = reader.IsDBNull(11) ? [] : reader.GetString(11).Split(',', StringSplitOptions.RemoveEmptyEntries);
            var objectSchema = reader.IsDBNull(4) ? null : reader.GetString(4);
            var objectName = reader.IsDBNull(5) ? null : reader.GetString(5);
            // A computed column's referencing object is its table, so the table is the parent; a constraint's parent is in the catalog.
            var parentSchema = kind == SchemaBoundDependentKind.ComputedColumn ? objectSchema : reader.IsDBNull(6) ? null : reader.GetString(6);
            var parentName = kind == SchemaBoundDependentKind.ComputedColumn ? objectName : reader.IsDBNull(7) ? null : reader.GetString(7);
            results.Add(new SchemaBoundDependent(kind, objectSchema, objectName, parentSchema, parentName,
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetInt32(2), reader.GetInt32(10) == 1, TypeDescription(type), indexes));
        }
        return results;
    }

    private static string TypeDescription(string type) => type switch
    {
        "V" => "view",
        "FN" or "IF" or "TF" => "function",
        "C" => "CHECK constraint",
        "SP" => "security policy",
        "TR" => "trigger",
        _ => "object"
    };

    public static SchemaBoundPlan Plan(IReadOnlyList<SchemaBoundDependent> dependents, DeclaredObjects declared)
    {
        var drops = new List<SchemaBoundDrop>();
        var undeclared = new List<SchemaBoundDependent>();
        var encrypted = dependents.Where(d => d.IsEncrypted).ToList();
        // Modules deepest-first, then the indexed views, constraints and columns the modules may have hidden behind.
        var order = dependents.OrderByDescending(d => d.Depth).ThenBy(d => d.Kind).ThenBy(d => d.Name, StringComparer.Ordinal);
        foreach (var d in order)
        {
            switch (d.Kind)
            {
                case SchemaBoundDependentKind.Module when declared.Modules.Contains(DeclaredObjects.Key(d.Schema, d.Name)):
                    drops.Add(new SchemaBoundDrop(d, [$"DROP {(d.TypeDescription == "view" ? "VIEW" : "FUNCTION")} {Q(d.Schema)}.{Q(d.Name)}"], "schemaBoundModule", $"{d.Schema}.{d.Name}"));
                    break;
                case SchemaBoundDependentKind.IndexedView when declared.IndexedViews.Contains(DeclaredObjects.Key(d.Schema, d.Name)):
                    drops.Add(new SchemaBoundDrop(d, [$"DROP VIEW {Q(d.Schema)}.{Q(d.Name)}"], "indexedView", $"{d.Schema}.{d.Name}"));
                    break;
                case SchemaBoundDependentKind.CheckConstraint when DeclaredTableOf(d, declared) is { } table && table.CheckConstraints.Contains(DeclaredObjects.Bare(d.Name)):
                    drops.Add(new SchemaBoundDrop(d, [$"ALTER TABLE {Q(d.ParentSchema)}.{Q(d.ParentName)} DROP CONSTRAINT {Q(d.Name)}"], "constraint", $"{Q(d.ParentSchema)}.{Q(d.ParentName)}.{Q(d.Name)}"));
                    break;
                case SchemaBoundDependentKind.ComputedColumn when DeclaredTableOf(d, declared) is { } table && table.ComputedColumns.Contains(DeclaredObjects.Bare(d.ColumnName)):
                    // An index on the column has to go first, and only one the package puts back.
                    var strangers = d.IndexesOnColumn.Where(i => !table.Indexes.Contains(DeclaredObjects.Bare(i))).ToList();
                    if (strangers.Count > 0)
                    {
                        undeclared.Add(d with { TypeDescription = $"computed column (indexed by {string.Join(", ", strangers)}, which the package does not declare)", Kind = SchemaBoundDependentKind.Other });
                        break;
                    }
                    var statements = d.IndexesOnColumn.Select(i => $"DROP INDEX {Q(i)} ON {Q(d.ParentSchema)}.{Q(d.ParentName)}").ToList();
                    statements.Add($"ALTER TABLE {Q(d.ParentSchema)}.{Q(d.ParentName)} DROP COLUMN {Q(d.ColumnName)}");
                    drops.Add(new SchemaBoundDrop(d, statements, "column", $"{Q(d.ParentSchema)}.{Q(d.ParentName)}.{Q(d.ColumnName)}"));
                    break;
                default:
                    undeclared.Add(d);
                    break;
            }
        }
        return new SchemaBoundPlan(drops, undeclared, encrypted);
    }

    private static DeclaredTable DeclaredTableOf(SchemaBoundDependent d, DeclaredObjects declared) =>
        declared.Tables.TryGetValue(DeclaredObjects.Key(d.ParentSchema, d.ParentName), out var table) ? table : null;

    private static string Q(string name) => "[" + (name ?? "").Replace("]", "]]") + "]";

    /// <summary>The message a stopped deploy carries: the module, the dependents, and what to do about each.</summary>
    public static string BlockedMessage(string schema, string name, SchemaBoundPlan plan)
    {
        var parts = new List<string>();
        if (plan.Encrypted.Count > 0)
            parts.Add("encrypted, so nothing can recreate it once dropped: " + string.Join(", ", plan.Encrypted.Select(d => d.Display)));
        if (plan.Undeclared.Count > 0)
            parts.Add("not declared by the package, so SchemaSmith will not drop it: " + string.Join(", ", plan.Undeclared.Select(d => d.Display)));
        return $"Cannot alter {schema}.{name}: it is referenced with SCHEMABINDING by an object that is {string.Join("; and by one that is ", parts)}. "
             + "Declare a schema-bound view or function in the 'SchemaBound Views' or 'SchemaBound Functions' folder, a CHECK constraint, "
             + "computed column or index in the table's JSON, or change it by hand.";
    }
}
