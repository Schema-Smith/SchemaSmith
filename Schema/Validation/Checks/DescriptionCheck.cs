// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Schema.Domain;
using Schema.Domain.MySQL;

namespace Schema.Validation.Checks;

/// <summary>
/// <c>Description</c> is one spelling on every engine. Two older spellings still deploy -- the MySQL family's
/// <c>Comment</c>, and on SQL Server an <c>MS_Description</c> entry inside <c>Extensions.ExtendedProperties</c>,
/// which is where extraction used to put it -- so a package that still uses one is told (<c>SS-DESC-002</c>,
/// Warning, once per table naming the components), and one that says two different things is stopped
/// (<c>SS-DESC-001</c>, Error): the deploy applies <c>Description</c> and ignores the alias, which is not
/// something to find out from the database.
/// </summary>
public sealed class DescriptionCheck : ISchemaCheck
{
    public const string ConflictCode = "SS-DESC-001";
    public const string DeprecatedSpellingCode = "SS-DESC-002";
    private const string Category = "Description";

    private const string MySqlAlias = "Comment";
    private const string SqlServerAlias = "Extensions.ExtendedProperties.MS_Description";

    public IEnumerable<Finding> Run(ValidationContext ctx)
    {
        var basePlatform = ctx.Platform.GetBasePlatform();
        if (basePlatform == Platform.PostgreSQL) yield break;
        var alias = basePlatform == Platform.MySQL ? MySqlAlias : SqlServerAlias;

        foreach (var template in ctx.Templates)
        {
            foreach (var table in template.Tables)
            {
                var location = $"Template '{template.Name}' / Table '{table.Name}'";
                var stillAliased = new List<string>();
                foreach (var (what, description, aliased) in Components(table, basePlatform))
                {
                    if (aliased == null) continue;
                    stillAliased.Add(what);
                    if (description != null && description != aliased)
                        yield return Conflict(location, what, alias, description, aliased);
                }
                if (stillAliased.Count > 0)
                    yield return DeprecatedSpelling(location, alias, stillAliased);
            }

            if (basePlatform != Platform.MySQL) continue;
            foreach (var ev in template.Events.Where(e => e.Comment != null))
            {
                var location = $"Template '{template.Name}' / Event '{ev.Name}'";
                if (ev.Description != null && ev.Description != ev.Comment)
                    yield return Conflict(location, "the event", alias, ev.Description, ev.Comment);
                yield return DeprecatedSpelling(location, alias, ["the event"]);
            }
        }
    }

    private static IEnumerable<(string What, string Description, string Aliased)> Components(Table table, Platform basePlatform)
    {
        yield return ("the table", table.Description, Aliased(table, basePlatform));
        foreach (var column in table.Columns)
            yield return ($"column {column.Name}", column.Description, Aliased(column, basePlatform));
        foreach (var index in table.Indexes)
            yield return ($"index {index.Name}", index.Description, Aliased(index, basePlatform));
        if (table is MySqlTable mysql)
            foreach (var fullText in mysql.FullTextIndexes)
                yield return ($"full-text index {fullText.Name}", fullText.Description, fullText.Comment);
    }

    private static string Aliased(DynamicBase component, Platform basePlatform) =>
        basePlatform == Platform.MySQL ? MySqlComment(component) : LegacyMsDescription(component);

    private static string MySqlComment(DynamicBase component) => component switch
    {
        MySqlTable t => t.Comment,
        MySqlColumn c => c.Comment,
        MySqlIndex i => i.Comment,
        _ => null
    };

    private static string LegacyMsDescription(DynamicBase component) =>
        (component.Extensions as JObject)?["ExtendedProperties"] is JObject properties
        && properties["MS_Description"] is JValue { Type: JTokenType.String } value
            ? (string)value
            : null;

    private static Finding Conflict(string location, string what, string alias, string description, string aliased) =>
        new(Severity.Error, ConflictCode, Category, location,
            $"On {what}, `Description` says '{description}' but `{alias}` says '{aliased}'. Keep one: the deploy applies " +
            "`Description` and ignores the other, so the file would promise a description the database never gets.");

    private static Finding DeprecatedSpelling(string location, string alias, List<string> components) =>
        new(Severity.Warning, DeprecatedSpellingCode, Category, location,
            $"`{alias}` is the deprecated spelling of `Description` on {string.Join(", ", components)}. Move the value to " +
            "`Description`; the deploy still honours the old key, and SchemaTongs writes `Description` on re-extraction.");
}
