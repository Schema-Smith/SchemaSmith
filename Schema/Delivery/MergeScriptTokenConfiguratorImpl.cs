// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Schema.Isolators;
using Schema.Utility;

namespace Schema.Delivery;

/// <summary>
/// IMergeScriptTokenConfigurator implementation that writes the ScriptTokens entry a tokenized
/// merge script needs into the template's Template.json. Mirrors DataDeliveryConfiguratorImpl's
/// idempotency shape exactly: change only what differs, write only when changed, log "already up
/// to date" otherwise, so a second extraction over an already-wired package produces zero churn.
/// </summary>
public class MergeScriptTokenConfiguratorImpl : IMergeScriptTokenConfigurator
{
    public static IMergeScriptTokenConfigurator GetFromFactory()
        => FactoryContainer.ResolveOrCreate<IMergeScriptTokenConfigurator, MergeScriptTokenConfiguratorImpl>();

    public void Configure(MergeScriptTokenConfiguratorContext context)
    {
        if (context == null || string.IsNullOrEmpty(context.TokenKey)) return;

        var templateJsonFile = Path.Join(context.TemplateRootPath ?? "", "Template.json");
        var file = FileWrapper.GetFromFactory();
        if (!file.Exists(templateJsonFile))
        {
            context.WarningLog?.Invoke($"    Template.json not found at '{templateJsonFile}'. Skipping token wiring for '{context.TokenKey}'.");
            return;
        }

        var json = file.ReadAllText(templateJsonFile);
        var template = JsonText.ParseObject(json);

        if (template["ScriptTokens"] is not JObject scriptTokens)
        {
            scriptTokens = new JObject();
            template["ScriptTokens"] = scriptTokens;
        }

        // Template tokens resolve relative to the template directory (not the package root), so the
        // <*File*> path must be template-relative — getting this wrong is the likeliest cause of #390.
        var relativePath = Path.GetRelativePath(context.TemplateRootPath, Path.GetFullPath(context.ContentFilePath)).Replace('\\', '/');
        var newValue = $"{TokenHelper.FileTag}{relativePath}";

        var existing = scriptTokens[context.TokenKey]?.ToString();
        if (string.Equals(existing, newValue, StringComparison.OrdinalIgnoreCase))
        {
            context.ProgressLog?.Invoke($"    ScriptTokens['{context.TokenKey}'] is already up to date.");
            return;
        }

        // A pre-existing value of the shape DataTongs itself writes (a <*File*> token) is safe to
        // refresh. Anything else is a deliberate user override and must not be silently discarded.
        if (existing != null && !existing.StartsWith(TokenHelper.FileTag, StringComparison.OrdinalIgnoreCase))
        {
            context.WarningLog?.Invoke($"    ScriptTokens['{context.TokenKey}'] is set to '{existing}', not a DataTongs-managed file token. Leaving it unchanged.");
            return;
        }

        scriptTokens[context.TokenKey] = newValue;
        file.WriteAllText(templateJsonFile, template.ToString(Formatting.Indented));
        context.ProgressLog?.Invoke($"    Updated ScriptTokens['{context.TokenKey}'] to '{newValue}'.");
    }
}
