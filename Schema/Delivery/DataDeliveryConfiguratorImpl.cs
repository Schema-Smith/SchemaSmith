// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Schema.Isolators;

namespace Schema.Delivery;

/// <summary>
/// IDataDeliveryConfigurator implementation that writes data delivery settings
/// into table JSON files after DataTongs extraction. Uses JObject manipulation
/// to avoid Schema domain type dependencies.
/// </summary>
public class DataDeliveryConfiguratorImpl : IDataDeliveryConfigurator
{
    public static IDataDeliveryConfigurator GetFromFactory()
        => FactoryContainer.ResolveOrCreate<IDataDeliveryConfigurator, DataDeliveryConfiguratorImpl>();

    public bool Configure(DataDeliveryConfiguratorContext context)
    {
        if (context == null) return false;

        var tableJsonFile = FindTableJsonFile(context.TemplateRootPath, context.TableSchema, context.TableName);
        if (tableJsonFile == null)
        {
            var displayName = string.IsNullOrEmpty(context.TableSchema) ? context.TableName : $"{context.TableSchema}.{context.TableName}";
            context.WarningLog?.Invoke($"    Table.json not found for {displayName} in {Path.Combine(context.TemplateRootPath ?? "", "Tables")}. Skipping data delivery configuration.");
            return false;
        }

        var json = FileWrapper.GetFromFactory().ReadAllText(tableJsonFile);
        var table = JObject.Parse(json);

        JObject delivery;
        var isArrayElement = false;

        if (table["DataDelivery"] is JArray array)
        {
            var displayName = string.IsNullOrEmpty(context.TableSchema) ? context.TableName : $"{context.TableSchema}.{context.TableName}";

            if (string.IsNullOrWhiteSpace(context.VariantName))
            {
                context.WarningLog?.Invoke($"    DataDelivery for '{displayName}' is an authored array of gated variants and no VariantName was provided for this extraction, so the array was left untouched.");
                context.ProgressLog?.Invoke($"    Data delivery config for {context.TableName} left untouched (array of gated variants; no target VariantName).");
                return false;
            }

            var matches = array.OfType<JObject>()
                .Where(e => string.Equals((string)e["VariantName"], context.VariantName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
            {
                context.WarningLog?.Invoke($"    DataDelivery variant '{context.VariantName}' was not found in the authored array for '{displayName}', so the array was left untouched (extraction never invents an ungated variant).");
                context.ProgressLog?.Invoke($"    Data delivery config for {context.TableName} left untouched (variant '{context.VariantName}' not found).");
                return false;
            }

            if (matches.Count > 1)
            {
                context.WarningLog?.Invoke($"    DataDelivery variant '{context.VariantName}' matches {matches.Count} entries in the authored array for '{displayName}', so the array was left untouched (ambiguous reconciliation target).");
                context.ProgressLog?.Invoke($"    Data delivery config for {context.TableName} left untouched (variant '{context.VariantName}' ambiguous).");
                return false;
            }

            delivery = matches[0];
            isArrayElement = true;
        }
        else
        {
            if (table["DataDelivery"] is not JObject single)
            {
                single = new JObject();
                table["DataDelivery"] = single;
            }
            delivery = single;
        }

        var changed = false;

        var relativePath = Path.GetRelativePath(context.TemplateRootPath, Path.GetFullPath(context.ContentFilePath)).Replace('\\', '/');
        changed |= SetIfDifferent(delivery, "ContentFile", relativePath, context.WarningLog,
            () => $"    ContentFile for '{context.TableName}' changed from '{delivery["ContentFile"]}' to '{relativePath}'.");

        var mergeType = string.IsNullOrWhiteSpace(context.MergeTypeOverride) ? context.DefaultMergeType : context.MergeTypeOverride;
        changed |= SetIfDifferent(delivery, "MergeType", mergeType);

        var matchColumns = string.IsNullOrWhiteSpace(context.KeyColumnsOverride) ? null : context.KeyColumnsOverride;
        changed |= SetIfDifferent(delivery, "MatchColumns", matchColumns);

        var mergeFilter = string.IsNullOrWhiteSpace(context.MergeFilterOverride) ? null : context.MergeFilterOverride;
        changed |= SetIfDifferent(delivery, "MergeFilter", mergeFilter);

        // "Xml" writes ContentEncoding; "Json"/blank removes it (JSON is the default encoding, so we
        // don't clutter the entry with a redundant "ContentEncoding": "Json").
        var contentEncoding = string.Equals(context.ContentEncoding, "Xml", StringComparison.OrdinalIgnoreCase) ? "Xml" : null;
        changed |= SetIfDifferent(delivery, "ContentEncoding", contentEncoding);

        // The zone the data was extracted in (MySQL family); absent means the target session's zone.
        changed |= SetIfDifferent(delivery, "TimeZone", string.IsNullOrWhiteSpace(context.TimeZone) ? null : context.TimeZone);

        changed |= SetBoolIfDifferent(delivery, "MergeDisableTriggers", context.DisableTriggers);

        if (context.Platform?.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase) == true)
        {
            changed |= SetBoolIfDifferent(delivery, "MergeDisableRules", context.DisableRules);
            changed |= SetBoolIfDifferent(delivery, "MergeUpdateDescendents", context.UpdateDescendents);
        }

        if (!isArrayElement && !delivery.HasValues)
            table.Remove("DataDelivery");

        if (changed)
        {
            FileWrapper.GetFromFactory().WriteAllText(tableJsonFile, table.ToString(Formatting.Indented));
            context.ProgressLog?.Invoke($"    Updated data delivery config for {context.TableName}{(isArrayElement ? $" [variant '{context.VariantName}']" : "")}");
        }
        else
        {
            context.ProgressLog?.Invoke($"    Data delivery config for {context.TableName} is already up to date.");
        }

        return true;
    }

    internal static string FindTableJsonFile(string templateRootPath, string tableSchema, string tableName)
    {
        if (string.IsNullOrEmpty(templateRootPath) || string.IsNullOrEmpty(tableName))
            return null;

        var file = FileWrapper.GetFromFactory();
        var directory = DirectoryWrapper.GetFromFactory();

        var tablesDir = Path.Combine(templateRootPath, "Tables");
        if (!directory.Exists(tablesDir))
            return null;

        var candidates = new[]
        {
            Path.Combine(tablesDir, $"{tableName}.json"),
            Path.Combine(tablesDir, $"{tableSchema}.{tableName}.json")
        };

        foreach (var candidate in candidates)
            if (file.Exists(candidate)) return candidate;

        var files = directory.GetFiles(tablesDir, "*.json", SearchOption.TopDirectoryOnly);
        var targetName = string.IsNullOrEmpty(tableSchema) ? tableName : $"{tableSchema}.{tableName}";

        return files.FirstOrDefault(f =>
            Path.GetFileNameWithoutExtension(f).Equals(targetName, StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileNameWithoutExtension(f).Equals(tableName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool SetIfDifferent(JObject obj, string propertyName, string value,
        Action<string> warningLog = null, Func<string> warningMessage = null)
    {
        var current = obj[propertyName]?.ToString();
        if (string.Equals(current, value, StringComparison.OrdinalIgnoreCase))
            return false;

        if (warningLog != null && warningMessage != null && !string.IsNullOrEmpty(current))
            warningLog(warningMessage());

        if (string.IsNullOrEmpty(value))
            obj.Remove(propertyName);
        else
            obj[propertyName] = value;

        return true;
    }

    private static bool SetBoolIfDifferent(JObject obj, string propertyName, bool value)
    {
        var current = obj[propertyName]?.Value<bool>() ?? false;
        if (current == value)
            return false;

        if (value)
            obj[propertyName] = value;
        else
            obj.Remove(propertyName);

        return true;
    }
}
