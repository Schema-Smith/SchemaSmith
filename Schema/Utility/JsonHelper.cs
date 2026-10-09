// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Newtonsoft.Json;
using Schema.Domain;
using Schema.Isolators;

namespace Schema.Utility;

public static class JsonHelper
{
    private static readonly JsonSerializerSettings SerializeSettings = new()
    {
        NullValueHandling = NullValueHandling.Ignore,
        DefaultValueHandling = DefaultValueHandling.Ignore,
        Formatting = Formatting.Indented
    };

    /// <summary>
    /// Settings that include default values (e.g., false for bools).
    /// Used when JSON consumers need all properties explicitly present (e.g., MySQL JSON_TABLE).
    /// </summary>
    private static readonly JsonSerializerSettings SerializeAllSettings = new()
    {
        NullValueHandling = NullValueHandling.Ignore,
        Formatting = Formatting.Indented
    };

    public static T Load<T>(string filePath)
    {
        if (!FileWrapper.GetFromFactory().Exists(filePath))
            throw new Exception($"File {filePath} does not exist");

        var text = FileWrapper.GetFromFactory().ReadAllText(filePath);
        return WithFileContext(filePath, () => JsonText.Deserialize<T>(text));
    }

    // Matches --Validate (JSON-schema additionalProperties:false) and the generated editor
    // .json-schemas — an unrecognised package property should error at deploy time too, not
    // vanish (Newtonsoft's default is MissingMemberHandling.Ignore). Extensions is unaffected:
    // DynamicBase declares it as a real property, so it always matches as a member.
    // Scoped to ProductLoad only — Load&lt;T&gt; is SchemaTongs' own scaffolding/bookkeeping
    // re-read (RepositoryHelper, LoadPackageTokens), never the editor/--Validate/deploy path this
    // task reconciles, and hardening it risks breaking an exploratory re-extraction over content
    // that was never meant to be re-validated there.
    //
    // Takes the handling as a parameter (default Error, unchanged for every existing caller)
    // rather than a fixed setting: PackageLoader passes Ignore so `--Validate` can still determine
    // Product.Platform — and therefore still run every other check — when Product.json itself
    // carries an unrecognised property, instead of the whole run dying on a single SS-LOAD-001
    // before JsonSchemaCheck ever gets to report the precise SS-JSON-001.
    public static Product ProductLoad(string filePath, MissingMemberHandling missingMemberHandling = MissingMemberHandling.Error)
    {
        if (!ProductFileWrapper.GetFromFactory().Exists(filePath))
            throw new Exception($"File {filePath} does not exist");

        var text = ProductFileWrapper.GetFromFactory().ReadAllText(filePath);
        var settings = JsonText.Settings(new JsonSerializerSettings { MissingMemberHandling = missingMemberHandling });
        return WithFileContext(filePath, () => JsonConvert.DeserializeObject<Product>(text, settings))
            ?? throw new JsonSerializationException($"Failed to deserialize Product from {filePath}");
    }

    public static Table TableLoad(string filePath, Platform platform)
    {
        if (!ProductFileWrapper.GetFromFactory().Exists(filePath))
            throw new Exception($"File {filePath} does not exist");

        var text = ProductFileWrapper.GetFromFactory().ReadAllText(filePath);
        return WithFileContext(filePath, () => PlatformDeserializer.DeserializeTable(text, platform));
    }

    // missingMemberHandling defaults to Error (deploy path, unchanged) — PackageLoader passes
    // Ignore for --Validate, mirroring ProductLoad's existing leniency parameter above.
    public static Template TemplateLoad(string filePath, Platform platform, MissingMemberHandling missingMemberHandling = MissingMemberHandling.Error)
    {
        if (!ProductFileWrapper.GetFromFactory().Exists(filePath))
            throw new Exception($"File {filePath} does not exist");

        var text = ProductFileWrapper.GetFromFactory().ReadAllText(filePath);
        return WithFileContext(filePath, () => PlatformDeserializer.DeserializeTemplate(text, platform, missingMemberHandling));
    }

    // Every JsonHelper load path funnels through here so a JSON parse/member failure — including
    // the MissingMemberHandling.Error rejection above — always names the file it came from, not
    // just the in-document property path Newtonsoft reports on its own.
    private static T WithFileContext<T>(string filePath, Func<T> deserialize)
    {
        try
        {
            return deserialize();
        }
        catch (JsonException e)
        {
            throw new JsonSerializationException($"Error parsing {filePath}: {e.Message}", e);
        }
    }

    public static string Serialize<T>(T obj)
    {
        return JsonConvert.SerializeObject(obj, SerializeSettings);
    }

    /// <summary>
    /// Serializes including default-valued properties (e.g., bool=false).
    /// Required for MySQL JSON_TABLE which needs explicit property presence.
    /// </summary>
    public static string SerializeAll<T>(T obj)
    {
        return JsonConvert.SerializeObject(obj, SerializeAllSettings);
    }

    public static void Write<T>(string filePath, T obj)
    {
        FileWrapper.GetFromFactory().WriteAllText(filePath, Serialize(obj));
    }
}
