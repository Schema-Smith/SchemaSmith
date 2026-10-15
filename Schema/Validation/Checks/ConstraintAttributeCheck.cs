// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

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
    private const string Category = "Constraint";

    public IEnumerable<Finding> Run(ValidationContext ctx)
    {
        if (ctx.Platform.GetBasePlatform() != Platform.PostgreSQL) yield break;
        foreach (var template in ctx.Templates)
            foreach (var table in template.Tables)
                foreach (var check in table.CheckConstraints.OfType<PostgreSqlCheckConstraint>().Where(c => c.Deferrable || c.InitiallyDeferred))
                    yield return new Finding(Severity.Error, DeferrableCheckCode, Category,
                        $"Template '{template.Name}' / Table '{table.Name}'",
                        $"Check constraint {check.Name} declares `{(check.Deferrable ? "Deferrable" : "InitiallyDeferred")}`, and PostgreSQL rejects " +
                        "DEFERRABLE on a CHECK constraint (\"CHECK constraints cannot be marked DEFERRABLE\"). Remove the key: only a " +
                        "foreign key, primary key, unique or exclusion constraint can be deferred.");
    }
}
