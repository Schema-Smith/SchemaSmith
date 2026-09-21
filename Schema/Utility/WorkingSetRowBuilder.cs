// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Schema.Utility;

/// <summary>
/// Builds the rows of a working-set table on the client, for the ingest path that bulk-loads instead of
/// asking the engine to shred JSON.
/// <para>
/// What it produces is the INGEST shape -- raw values, exactly as the shred's SELECT hands them over.
/// Every default, bracket-wrap and canonicalization belongs to the NORMALIZE pass that runs afterwards
/// in SQL, shared by both paths. That division is the whole reason two producers can be trusted to agree:
/// the client copies values, it does not interpret them.
/// </para>
/// </summary>
public static class WorkingSetRowBuilder
{
    /// <summary>
    /// Columns the client does not write because something else owns them, with the reason. Anything in
    /// the table that is neither mapped from JSON nor listed here is a column nobody taught this builder
    /// about, and <see cref="Build"/> refuses rather than bulk-loading a silent null into it.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> NotWrittenByClient =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["_RowId"] = "assigned here, from the model's order",
            ["RebuildPolicySpecified"] = "derived from whether the RebuildPolicy object is present at all",
        };

    /// <summary>
    /// Fill <paramref name="target"/> -- whose shape was read from the temp table SQL created -- with one
    /// row per element of <paramref name="model"/>.
    /// </summary>
    /// <param name="model">The declared model, already token-substituted.</param>
    /// <param name="map">The shred's own JSON-path mapping, from <see cref="WorkingSetShredMap"/>.</param>
    /// <param name="target">An empty DataTable carrying the working-set table's real column shape.</param>
    public static void Build(JArray model, IReadOnlyList<WorkingSetShredMap.ShredColumn> map, DataTable target)
    {
        var byColumn = map.ToDictionary(c => c.Column, StringComparer.OrdinalIgnoreCase);
        AssertEveryColumnIsAccountedFor(map, target);

        // '$.RebuildPolicy' is read by the shred into a scratch column it never stores, purely to answer
        // "did this table declare a policy at all?" -- the sentinel the apply side needs, because a
        // policy applies WHOLE and a per-field check cannot tell an omitted field from an absent object.
        var policyPath = byColumn.TryGetValue("RebuildPolicyJson", out var policy) ? policy.JsonPath : null;

        var rowId = 0L;
        foreach (var element in model.OfType<JObject>())
        {
            var row = target.NewRow();
            rowId++;

            foreach (DataColumn column in target.Columns)
            {
                if (string.Equals(column.ColumnName, "_RowId", StringComparison.OrdinalIgnoreCase))
                {
                    row[column] = rowId;
                    continue;
                }

                if (string.Equals(column.ColumnName, "RebuildPolicySpecified", StringComparison.OrdinalIgnoreCase))
                {
                    row[column] = policyPath != null && Select(element, policyPath) is { Type: not JTokenType.Null };
                    continue;
                }

                if (!byColumn.TryGetValue(column.ColumnName, out var shred)) continue;

                var token = Select(element, shred.JsonPath);
                row[column] = Convert(token, shred, column);
            }

            target.Rows.Add(row);
        }
    }

    /// <summary>
    /// A column the SQL declares but nothing fills is the failure this whole design has to avoid: the
    /// bulk path would load a null where the shred loaded a value, and only a deploy would show it. So a
    /// column added to the working set without a mapping stops the run here, by name.
    /// </summary>
    private static void AssertEveryColumnIsAccountedFor(
        IReadOnlyList<WorkingSetShredMap.ShredColumn> map, DataTable target)
    {
        var mapped = new HashSet<string>(map.Select(c => c.Column), StringComparer.OrdinalIgnoreCase);
        var unexplained = target.Columns.Cast<DataColumn>()
            .Select(c => c.ColumnName)
            .Where(name => !mapped.Contains(name) && !NotWrittenByClient.ContainsKey(name))
            .ToList();

        if (unexplained.Count > 0)
            throw new Exception(
                $"The working-set table '{target.TableName}' has columns the client ingest path does not " +
                $"know how to fill: {string.Join(", ", unexplained)}. Either map them in the shred's " +
                "OPENJSON WITH block, or record why the client does not write them. Loading them as NULL " +
                "would differ from what the JSON shred produces, and nothing downstream would say so.");
    }

    /// <summary>
    /// Resolve a shred path against one element. Only the forms the script actually uses are supported --
    /// a top-level property and one level of nesting ('$.RebuildPolicy.Mode') -- and an unsupported form
    /// throws rather than quietly resolving to nothing.
    /// </summary>
    private static JToken Select(JObject element, string path)
    {
        if (!path.StartsWith("$.", StringComparison.Ordinal))
            throw new Exception($"Unsupported shred path '{path}': every mapping is expected to be rooted at '$.'.");

        var steps = path[2..].Split('.');
        if (steps.Length > 2)
            throw new Exception($"Unsupported shred path '{path}': only one level of nesting is handled.");

        JToken token = element;
        foreach (var step in steps)
        {
            token = (token as JObject)?[step];
            if (token == null || token.Type == JTokenType.Null) return null;
        }
        return token;
    }

    private static object Convert(JToken token, WorkingSetShredMap.ShredColumn shred, DataColumn column)
    {
        if (token == null || token.Type == JTokenType.Null) return DBNull.Value;

        // AS JSON carries the nested array or object through as raw text. The engine's own shred emits it
        // without whitespace; matching that keeps the two paths byte-comparable rather than merely
        // equivalent, which is what the equality guard between them asserts.
        if (shred.AsJson) return token.ToString(Formatting.None);

        var type = Nullable.GetUnderlyingType(column.DataType) ?? column.DataType;

        // Booleans are the one place JSON and the engine disagree about spelling: OPENJSON accepts the
        // JSON literals and 0/1 alike for a BIT column, so a model written either way must land the same.
        if (type == typeof(bool))
            return token.Type == JTokenType.Boolean
                ? token.Value<bool>()
                : token.ToString().Trim() is "1" or "true" or "True";

        if (type == typeof(string)) return token.Type == JTokenType.String ? token.Value<string>() : token.ToString();

        return System.Convert.ChangeType(token.ToObject<object>(), type, CultureInfo.InvariantCulture);
    }
}
