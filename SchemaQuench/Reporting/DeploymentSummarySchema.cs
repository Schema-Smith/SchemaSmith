// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace SchemaQuench.Reporting;

/// <summary>
/// The JSON Schema of Summary.json, generated from the model through the same contract resolver the writer uses, so the
/// names, the null handling and the set of properties are the writer's own. Each object closes its property list
/// (additionalProperties false): a field the writer emits and the schema lacks is a validation error, not a silent gap.
/// A property is required when the writer always emits it, which is every one not marked to be left out when null.
/// </summary>
public static class DeploymentSummarySchema
{
    private static readonly IContractResolver Resolver = new CamelCasePropertyNamesContractResolver();

    public static string FileName => $"deployment-summary-{DeploymentSummaryAssembler.SchemaVersion}.schema.json";

    public static JObject Generate()
    {
        var definitions = new SortedDictionary<string, JObject>(StringComparer.Ordinal);
        var root = ObjectSchema(typeof(DeploymentSummary), definitions);
        ((JObject)root["properties"]!)["schemaVersion"] = new JObject
        {
            ["type"] = "string",
            ["enum"] = new JArray(DeploymentSummaryAssembler.SchemaVersion)
        };

        var schema = new JObject
        {
            ["$schema"] = "http://json-schema.org/draft-07/schema#",
            ["title"] = $"SchemaQuench deployment summary {DeploymentSummaryAssembler.SchemaVersion}"
        };
        foreach (var property in root.Properties()) schema[property.Name] = property.Value;
        schema["definitions"] = new JObject(definitions.Select(d => new JProperty(d.Key, d.Value)));
        return schema;
    }

    public static string Render() => Generate().ToString(Formatting.Indented).Replace("\r\n", "\n") + "\n";

    private static JObject ObjectSchema(Type type, IDictionary<string, JObject> definitions)
    {
        var contract = (JsonObjectContract)Resolver.ResolveContract(type);
        var properties = new JObject();
        var required = new JArray();
        foreach (var property in contract.Properties.Where(p => !p.Ignored && p.Readable))
        {
            properties[property.PropertyName!] = TypeSchema(property.PropertyType!, definitions);
            if (property.NullValueHandling != NullValueHandling.Ignore) required.Add(property.PropertyName);
        }
        return new JObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
            ["additionalProperties"] = false
        };
    }

    private static JObject TypeSchema(Type type, IDictionary<string, JObject> definitions)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        var nullable = underlying != null || !type.IsValueType;
        type = underlying ?? type;

        JObject Simple(string jsonType, string format = null)
        {
            var schema = new JObject { ["type"] = nullable ? new JArray(jsonType, "null") : jsonType };
            if (format != null) schema["format"] = format;
            return schema;
        }

        if (type == typeof(string)) return Simple("string");
        if (type == typeof(bool)) return Simple("boolean");
        if (type == typeof(int) || type == typeof(long)) return Simple("integer");
        if (type == typeof(double) || type == typeof(decimal)) return Simple("number");
        if (type == typeof(DateTime)) return Simple("string", "date-time");
        if (type == typeof(Guid)) return Simple("string", "uuid");
        if (type.IsEnum)
        {
            var names = new JArray(Enum.GetNames(type));
            if (nullable) names.Add(null);
            var schema = Simple("string");
            schema["enum"] = names;
            return schema;
        }

        var element = type.GetInterfaces().Append(type)
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))?.GetGenericArguments()[0];
        if (element != null && typeof(IEnumerable).IsAssignableFrom(type))
        {
            var array = Simple("array");
            array["items"] = TypeSchema(element, definitions);
            return array;
        }

        if (!definitions.ContainsKey(type.Name))
        {
            definitions[type.Name] = null;
            definitions[type.Name] = ObjectSchema(type, definitions);
        }
        var reference = new JObject { ["$ref"] = $"#/definitions/{type.Name}" };
        return nullable ? new JObject { ["anyOf"] = new JArray(reference, new JObject { ["type"] = "null" }) } : reference;
    }
}
