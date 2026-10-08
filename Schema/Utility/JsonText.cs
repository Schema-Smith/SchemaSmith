// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.IO;
using System.Xml.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace Schema.Utility;

/// <summary>
/// The one way package and data JSON is read: strings stay strings. Newtonsoft's default turns any string that looks
/// like an ISO date into a <see cref="System.DateTime"/> in the host's time zone, so a value read and written back
/// changed its text, and changed it differently on each machine -- a custom property, a JSON-schema enum, a deploy
/// payload, a delivered row.
/// </summary>
public static class JsonText
{
    /// <summary>Settings for <see cref="JsonConvert.DeserializeObject(string, System.Type, JsonSerializerSettings)"/>.</summary>
    public static JsonSerializerSettings Settings(JsonSerializerSettings settings = null)
    {
        settings ??= new JsonSerializerSettings();
        settings.DateParseHandling = DateParseHandling.None;
        return settings;
    }

    public static JToken Parse(string json)
    {
        using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
        var token = JToken.ReadFrom(reader);
        // The same trailing-content check JToken.Parse makes.
        while (reader.Read())
            if (reader.TokenType != JsonToken.Comment)
                throw new JsonReaderException("Additional text found in JSON string after finishing deserializing object.");
        return token;
    }

    public static JObject ParseObject(string json) =>
        Parse(json) as JObject ?? throw new JsonReaderException("Error reading JObject: the JSON is not an object.");

    public static JArray ParseArray(string json) =>
        Parse(json) as JArray ?? throw new JsonReaderException("Error reading JArray: the JSON is not an array.");

    public static T Deserialize<T>(string json, JsonSerializerSettings settings = null) =>
        JsonConvert.DeserializeObject<T>(json, Settings(settings));

    /// <summary><see cref="JsonConvert.DeserializeXNode(string, string)"/> without the date conversion.</summary>
    public static XDocument ToXDocument(string json, string rootElement) =>
        JsonConvert.DeserializeObject<XDocument>(json,
            Settings(new JsonSerializerSettings { Converters = { new XmlNodeConverter { DeserializeRootElementName = rootElement } } }));
}
