// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using Schema.Domain;
using Schema.Utility;

namespace SchemaQuench;

/// <summary>
/// Runs a schema-template's <c>SchemaIdentificationScript</c> against an open database
/// command, reads back the single-column result set of schema names, and validates each
/// returned name against the platform-specific reserved-name reject list (design §5.4).
///
/// <para>
/// Mirrors the existing call-site pattern for <c>DatabaseIdentificationScript</c> in
/// <see cref="ProductQuench"/> — caller supplies an <see cref="IDbCommand"/> with a live
/// <see cref="IDbConnection"/>; this class sets the command text, executes the reader,
/// and projects column 0 to <see cref="string"/>.
/// </para>
///
/// <para><b>Reserved-name guards (design §5.4).</b> Discovery hard-rejects platform-owned
/// schema namespaces: SQL Server's <c>dbo</c>, <c>sys</c>, <c>INFORMATION_SCHEMA</c>,
/// <c>guest</c>, and the <c>db_*</c> built-in roles; PostgreSQL's <c>public</c>,
/// <c>pg_catalog</c>, <c>pg_toast</c>, <c>pg_temp_*</c>, <c>pg_toast_temp_*</c>,
/// <c>information_schema</c>. These names remain valid as <c>RelatedTableSchema</c>
/// literals (cross-schema FK references are a feature, not a bug) and as explicit
/// schema references in free-form SQL — the guard only fires when a name is presented as
/// an <i>iteration target</i>. Shared content belongs in a regular template that runs earlier
/// in <c>TemplateOrder</c>, not in a fan-out iteration.</para>
///
/// <para><b>MySQL:</b> schema templates are intentionally not offered on MySQL (no
/// namespace-inside-database concept — see design §2). Load-time validation already
/// rejects schema-template-on-MySQL upstream; the defensive throw here catches direct
/// misuse from a calling test or programmatic invocation that bypasses the load path.</para>
/// </summary>
public static class SchemaDiscovery
{
    /// <summary>
    /// Runs <paramref name="template"/>'s <c>SchemaIdentificationScript</c> against
    /// <paramref name="command"/>, validates each returned name against the platform's
    /// reject list, and returns the schema names in discovery order.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the platform is MySQL (schema templates are SQL-Server / PostgreSQL only),
    /// or when discovery returns a reserved schema name. The thrown message names the
    /// offending schema and points the user at the "shared content in a regular template" pattern.
    /// </exception>
    public static List<string> Discover(IDbCommand command, Template template)
    {
        if (template == null) throw new ArgumentNullException(nameof(template));
        if (command == null) throw new ArgumentNullException(nameof(command));

        var platform = template.Product?.Platform ?? Platform.Unknown;
        if (platform.GetBasePlatform() == Platform.MySQL)
        {
            throw new InvalidOperationException(
                $"Schema-template discovery is not supported on MySQL (template '{template.Name}'). " +
                "MySQL conflates schemas and databases; use DatabaseIdentificationScript instead. " +
                "See design §2 for rationale.");
        }

        command.CommandText = template.SchemaIdentificationScript;
        var results = new List<string>();
        using var reader = command.ExecuteReader();
        if (reader.FieldCount != 1)
        {
            throw new InvalidOperationException(
                $"SchemaIdentificationScript for template '{template.Name}' returned {reader.FieldCount} columns; " +
                "discovery expects a single-column result set of schema names. " +
                "Adjust the script's SELECT to project exactly one column (the schema name).");
        }
        while (reader.Read())
        {
            var raw = reader[0];
            if (raw != null && raw != DBNull.Value) results.Add(raw.ToString());
        }

        // ONE rule: SchemaDiscoveryRules (Schema/) is what anything else reading this script's output calls too, so
        // the deploy and a consumer cannot disagree about which names are acceptable. It skips null/blank entries
        // and reports the FIRST violation in order, which is what reading row by row and throwing did.
        var violation = SchemaDiscoveryRules.Validate(results, platform);
        if (violation != null)
            throw new InvalidOperationException(
                $"SchemaIdentificationScript for template '{template.Name}' returned {violation.Detail}");

        results.RemoveAll(string.IsNullOrWhiteSpace);
        return results;
    }

}
