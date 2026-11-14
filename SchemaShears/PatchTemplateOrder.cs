// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaShears;

/// <summary>
/// A patch carries the templates its manifest touches, not all of them, and a <c>TemplateOrder</c> entry with no folder
/// stops the load. The patch's order keeps the source's sequence for the templates it carries and drops the rest.
/// </summary>
public static class PatchTemplateOrder
{
    public static void Prune(string outputPath)
    {
        var productJson = Path.Join(outputPath, "Product.json");
        var json = JsonText.ParseObject(FileWrapper.GetFromFactory().ReadAllText(productJson));
        if (json["TemplateOrder"] is not JArray order) return;

        var templatesRoot = Path.Join(outputPath, "Templates");
        var carried = DirectoryWrapper.GetFromFactory().Exists(templatesRoot)
            ? DirectoryWrapper.GetFromFactory().GetDirectories(templatesRoot, "*", SearchOption.TopDirectoryOnly).Select(Path.GetFileName).ToList()
            : [];
        var kept = order.Where(e => carried.Contains(e.Value<string>(), StringComparer.OrdinalIgnoreCase)).ToList();
        if (kept.Count == order.Count) return;

        json["TemplateOrder"] = new JArray(kept);
        FileWrapper.GetFromFactory().WriteAllText(productJson, json.ToString(Formatting.Indented));
    }
}
