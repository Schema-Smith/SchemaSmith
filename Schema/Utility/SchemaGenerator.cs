// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Schema.Domain;
using Schema.Isolators;

namespace Schema.Utility;

public static class SchemaGenerator
{
    public static JObject GenerateSchema(Type rootType, Platform? platform = null)
    {
        // Identity resolver preserves the historical behavior exactly: list element types are
        // built as their declared (base) type. Existing callers/tests rely on this.
        return GenerateSchema(rootType, t => t, platform);
    }

    /// <summary>
    /// Generates a JSON schema, mapping list element types through <paramref name="elementTypeResolver"/>
    /// so platform subclass properties appear in generated collection element schemas.
    /// </summary>
    public static JObject GenerateSchema(Type rootType, Func<Type, Type> elementTypeResolver, Platform? platform = null)
    {
        return BuildObjectSchema(rootType, elementTypeResolver, platform);
    }

    public static JObject MergeExtensionsDefinition(JObject generated, JObject existing)
    {
        if (existing == null) return generated;

        // Carry over a hand-authored Extensions fragment wherever the user defined one — table root,
        // every collection element (columns, indexes, FKs, checks, statistics, ...), single-object
        // components, and both branches of a SingleOrArray oneOf. Walk both trees in lockstep by
        // structure so a fragment is only preserved at the location it was authored.
        MergeExtensionsNode(generated, existing);
        return generated;
    }

    private static void MergeExtensionsNode(JToken generated, JToken existing)
    {
        if (generated is not JObject genObj || existing is not JObject exObj) return;

        if (genObj["properties"] is JObject genProps && exObj["properties"] is JObject exProps)
        {
            // Preserve the authored fragment at this level (only where the current model still has the slot).
            if (genProps["Extensions"] != null && exProps["Extensions"] is { } exExtensions)
                genProps["Extensions"] = exExtensions.DeepClone();

            // Recurse into every sibling property the two trees share.
            foreach (var genProp in genProps.Properties())
            {
                if (genProp.Name == "Extensions") continue;
                if (exProps[genProp.Name] is { } exChild)
                    MergeExtensionsNode(genProp.Value, exChild);
            }
        }

        // Descend through array element schemas and each SingleOrArray oneOf branch.
        if (genObj["items"] is { } genItems && exObj["items"] is { } exItems)
            MergeExtensionsNode(genItems, exItems);

        if (genObj["oneOf"] is JArray genOneOf && exObj["oneOf"] is JArray exOneOf)
            for (var i = 0; i < genOneOf.Count && i < exOneOf.Count; i++)
                MergeExtensionsNode(genOneOf[i], exOneOf[i]);
    }

    private static JObject BuildObjectSchema(Type type, Func<Type, Type> elementTypeResolver, Platform? platform)
    {
        var schema = new JObject { ["type"] = "object" };
        var properties = new JObject();
        var required = new JArray();
        var conditionalRequired = new List<(string Property, string Unless)>();

        foreach (var prop in GetSortedProperties(type))
        {
            // Platform scoping (see SchemaPropertyAttribute.Platforms). Keyed on the EXACT platform, not
            // GetBasePlatform(): MariaDB shares MySqlTemplate, so folding it here would make a MariaDB-only
            // setting unexpressible. Undecorated properties are emitted everywhere -- scoping is opt-in.
            if (!AppliesToPlatform(prop, platform)) continue;

            var propSchema = MapType(prop.PropertyType, elementTypeResolver, platform);
            ApplyConstraints(prop, propSchema);
            DocumentPlatforms(prop, propSchema);

            var schemaAttr = prop.GetCustomAttribute<SchemaPropertyAttribute>();
            if (schemaAttr is { SingleOrArray: true } && propSchema["items"] is JObject itemSchema)
                propSchema = new JObject { ["oneOf"] = new JArray(itemSchema.DeepClone(), propSchema) };

            properties[GetPropertyName(prop)] = propSchema;

            // JsonHelper.Serialize omits a value equal to its [DefaultValue], so requiring such a property
            // would reject the commonest file the model itself writes. Absent already loads as the default.
            if (schemaAttr is { Required: true } && prop.GetCustomAttribute<DefaultValueAttribute>() == null)
            {
                // A property may be required only when a sibling flag is off -- IndexColumns is
                // required unless the index is a columnstore, which has no key columns at all.
                var unless = string.IsNullOrEmpty(schemaAttr.RequiredUnless)
                    ? null
                    : type.GetProperty(schemaAttr.RequiredUnless);
                if (unless != null)
                    conditionalRequired.Add((GetPropertyName(prop), GetPropertyName(unless)));
                else
                    required.Add(GetPropertyName(prop));
            }
        }

        schema["properties"] = properties;
        schema["additionalProperties"] = false;
        if (required.Count > 0)
            schema["required"] = required;

        if (conditionalRequired.Count > 0)
        {
            // `if` must also require the flag: without that, a document omitting it satisfies the
            // `properties` clause vacuously and would skip the requirement entirely.
            var allOf = new JArray();
            foreach (var (property, unless) in conditionalRequired)
                allOf.Add(new JObject
                {
                    ["if"] = new JObject
                    {
                        ["properties"] = new JObject { [unless] = new JObject { ["const"] = true } },
                        ["required"] = new JArray(unless)
                    },
                    ["else"] = new JObject { ["required"] = new JArray(property) }
                });
            schema["allOf"] = allOf;
        }

        return schema;
    }

    private static JObject MapType(Type type, Func<Type, Type> elementTypeResolver, Platform? platform)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        if (type == typeof(string)) return new JObject { ["type"] = "string" };
        if (type == typeof(bool)) return new JObject { ["type"] = "boolean" };
        if (IsIntegerType(type)) return new JObject { ["type"] = "integer" };
        if (IsNumberType(type)) return new JObject { ["type"] = "number" };
        if (type.IsEnum)
        {
            if (type.GetCustomAttribute<JsonConverterAttribute>()?.ConverterType == typeof(Newtonsoft.Json.Converters.StringEnumConverter))
            {
                var values = string.Join("|", Enum.GetNames(type));
                return new JObject { ["type"] = "string", ["pattern"] = values };
            }
            return new JObject { ["type"] = "integer" };
        }
        if (IsListType(type))
        {
            var elementType = type.GetGenericArguments()[0];
            // Resolve only list element types — this is the precise gap the overload closes.
            elementType = elementTypeResolver(elementType);
            var items = elementType == typeof(string) || IsIntegerType(elementType) || IsNumberType(elementType) || elementType == typeof(bool)
                ? MapType(elementType, elementTypeResolver, platform)
                : BuildObjectSchema(elementType, elementTypeResolver, platform);
            return new JObject { ["type"] = "array", ["items"] = items };
        }
        if (IsDictionaryType(type)) return new JObject { ["type"] = "object" };
        if (typeof(JToken).IsAssignableFrom(type)) return new JObject();

        return BuildObjectSchema(type, elementTypeResolver, platform);
    }

    private static void ApplyConstraints(PropertyInfo prop, JObject propSchema)
    {
        var attr = prop.GetCustomAttribute<SchemaPropertyAttribute>();
        if (attr == null) return;
        if (!string.IsNullOrEmpty(attr.Pattern)) propSchema["pattern"] = attr.Pattern;
        if (!double.IsNaN(attr.Minimum)) propSchema["minimum"] = attr.Minimum;
        if (!double.IsNaN(attr.Maximum)) propSchema["maximum"] = attr.Maximum;
        if (attr.MaxLength >= 0) propSchema["maxLength"] = attr.MaxLength;
        if (!string.IsNullOrEmpty(attr.Description)) propSchema["description"] = attr.Description;
    }
    private static bool AppliesToPlatform(PropertyInfo prop, Platform? platform)
    {
        if (platform == null) return true;
        var scoped = prop.GetCustomAttribute<SchemaPropertyAttribute>()?.Platforms;
        return scoped == null || scoped.Length == 0 || scoped.Contains(platform.Value);
    }

    /// <summary>
    /// Says in the schema file itself which engines a scoped property applies to. Filtering alone leaves a
    /// reader of one platform's file unable to tell whether a property is universal or merely happens to
    /// apply here -- which matters most for the two-engine settings, where the answer is neither.
    /// </summary>
    private static void DocumentPlatforms(PropertyInfo prop, JObject propSchema)
    {
        var scoped = prop.GetCustomAttribute<SchemaPropertyAttribute>()?.Platforms;
        if (scoped == null || scoped.Length == 0) return;

        var names = scoped.Select(p => p.ToDisplayName()).ToArray();
        var list = names.Length == 1
            ? names[0]
            : string.Join(", ", names[..^1]) + " and " + names[^1];
        var note = $"{list} only.";

        var existing = propSchema["description"]?.Value<string>();
        propSchema["description"] = string.IsNullOrWhiteSpace(existing) ? note : $"{existing} {note}";
    }


    private static IEnumerable<PropertyInfo> GetSortedProperties(Type type)
    {
        return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() == null)
            .OrderBy(p => p.GetCustomAttribute<JsonPropertyAttribute>()?.Order ?? int.MaxValue)
            .ThenBy(p => p.Name);
    }

    private static string GetPropertyName(PropertyInfo prop)
    {
        return prop.GetCustomAttribute<JsonPropertyAttribute>()?.PropertyName ?? prop.Name;
    }

    private static bool IsIntegerType(Type t) => t == typeof(byte) || t == typeof(short) || t == typeof(ushort) || t == typeof(int) || t == typeof(uint) || t == typeof(long) || t == typeof(ulong);
    private static bool IsNumberType(Type t) => t == typeof(float) || t == typeof(double) || t == typeof(decimal);
    private static bool IsListType(Type t) => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>);
    private static bool IsDictionaryType(Type t) => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Dictionary<,>);
}
