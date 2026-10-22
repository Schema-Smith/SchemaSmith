// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Linq;
using Schema.Domain;
using Schema.Domain.PostgreSQL;

namespace Schema.Validation.Checks;

/// <summary>
/// PostgreSQL does not allow <c>DEFERRABLE</c> on a CHECK constraint, and the deploy no longer emits it, so a check
/// that declares <c>Deferrable</c> or <c>InitiallyDeferred</c> is stopped (<c>SS-CHK-001</c>, Error) rather than
/// quietly deployed without the clause it asked for.
/// </summary>
public sealed class ConstraintAttributeCheck : ISchemaCheck
{
    public const string DeferrableCheckCode = "SS-CHK-001";
    public const string MariaDbColumnNameCode = "SS-CHK-002";
    public const string NotNullNameShapeCode = "SS-COL-003";
    private const string Category = "Constraint";

    public IEnumerable<Finding> Run(ValidationContext ctx)
    {
        var platform = ctx.Platform.GetBasePlatform();
        if (ctx.Platform == Platform.MariaDb)
        {
            // MariaDB names a column-level check after its column and, from 10.11.19 / 11.4.13 / 13.0.2, refuses a
            // table-level check that shares a column's name (1826). SchemaSmith reads the column's own check by that
            // name, so a CheckConstraints entry named like a column is both a collision and a misread.
            foreach (var template in ctx.Templates)
                foreach (var table in template.Tables)
                    foreach (var check in table.CheckConstraints)
                    {
                        var column = table.Columns.FirstOrDefault(c => string.Equals(Strip(c.Name), Strip(check.Name), StringComparison.OrdinalIgnoreCase));
                        if (column == null) continue;
                        yield return new Finding(Severity.Error, MariaDbColumnNameCode, Category,
                            $"Template '{template.Name}' / Table '{table.Name}'",
                            $"Check constraint {check.Name} is named like column {column.Name}. MariaDB gives that name to the column's own " +
                            "column-level check, and from 10.11.19 / 11.4.13 / 13.0.2 refuses a table-level check that shares it. Rename the " +
                            "constraint, or declare it as the column's `CheckExpression` if that is what it is.");
                    }
            yield break;
        }
        if (platform != Platform.PostgreSQL) yield break;
        foreach (var template in ctx.Templates)
            foreach (var table in template.Tables)
            {
                foreach (var check in table.CheckConstraints.OfType<PostgreSqlCheckConstraint>().Where(c => c.Deferrable || c.InitiallyDeferred))
                    yield return new Finding(Severity.Error, DeferrableCheckCode, Category,
                        $"Template '{template.Name}' / Table '{table.Name}'",
                        $"Check constraint {check.Name} declares `{(check.Deferrable ? "Deferrable" : "InitiallyDeferred")}`, and PostgreSQL rejects " +
                        "DEFERRABLE on a CHECK constraint (\"CHECK constraints cannot be marked DEFERRABLE\"). Remove the key: only a " +
                        "foreign key, primary key, unique or exclusion constraint can be deferred.");

                // PostgreSQL 18 names the NOT NULL constraint it makes for a NOT NULL column <table>_<column>_not_null. A check
                // or foreign key given that name collides with it on 18 (the column's own NOT NULL is not modelled by name),
                // and reads as one to anybody looking at the catalog on every version.
                var tableName = Strip(table.Name);
                foreach (var name in table.CheckConstraints.Select(c => c.Name).Concat(table.ForeignKeys.Select(f => f.Name)))
                {
                    var bare = Strip(name);
                    if (!bare.StartsWith(tableName + "_", StringComparison.OrdinalIgnoreCase) || !bare.EndsWith("_not_null", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var columnName = bare.Substring(tableName.Length + 1, bare.Length - tableName.Length - 1 - "_not_null".Length);
                    if (!table.Columns.Any(c => string.Equals(Strip(c.Name), columnName, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    yield return new Finding(Severity.Warning, NotNullNameShapeCode, Category,
                        $"Template '{template.Name}' / Table '{table.Name}'",
                        $"Constraint {name} is named like the NOT NULL constraint PostgreSQL 18 makes for column {columnName} ({tableName}_{columnName}_not_null). " +
                        "A column's NOT NULL is declared by Nullable, not by a named constraint; rename this one so it cannot collide with, or be mistaken for, the engine's.");
                }
            }
    }

    private static string Strip(string name) => (name ?? "").Trim().Trim('`', '"', '[', ']');
}
