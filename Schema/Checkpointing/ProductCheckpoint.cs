// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Schema.Checkpointing;

internal class ProductCheckpoint
{
    public string ProductName { get; set; } = "";
    // UTC, as the deployment summary reports times.
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public Guid? RunId { get; set; }
    public Dictionary<string, List<string>> CompletedBeforeScripts { get; set; } = new();
    public List<string> CompletedTemplates { get; set; } = [];
    public Dictionary<string, List<string>> CompletedAfterScripts { get; set; } = new();

    public void AddBeforeScript(string server, string scriptPath)
    {
        var normalizedPath = NormalizePath(scriptPath);
        if (!CompletedBeforeScripts.TryGetValue(server, out var scripts))
        {
            scripts = new List<string>();
            CompletedBeforeScripts[server] = scripts;
        }
        if (!scripts.Any(s => s.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase)))
            scripts.Add(normalizedPath);
    }

    public void AddTemplate(string templateName)
    {
        if (!CompletedTemplates.Any(t => t.Equals(templateName, StringComparison.OrdinalIgnoreCase)))
            CompletedTemplates.Add(templateName);
    }

    public void AddAfterScript(string server, string scriptPath)
    {
        var normalizedPath = NormalizePath(scriptPath);
        if (!CompletedAfterScripts.TryGetValue(server, out var scripts))
        {
            scripts = new List<string>();
            CompletedAfterScripts[server] = scripts;
        }
        if (!scripts.Any(s => s.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase)))
            scripts.Add(normalizedPath);
    }

    public bool HasCompletedBeforeScript(string server, string scriptPath)
    {
        var normalizedPath = NormalizePath(scriptPath);
        return CompletedBeforeScripts.TryGetValue(server, out var scripts) &&
               scripts.Any(s => s.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase));
    }

    public bool HasCompletedTemplate(string templateName)
    {
        return CompletedTemplates.Any(t => t.Equals(templateName, StringComparison.OrdinalIgnoreCase));
    }

    public bool HasCompletedAfterScript(string server, string scriptPath)
    {
        var normalizedPath = NormalizePath(scriptPath);
        return CompletedAfterScripts.TryGetValue(server, out var scripts) &&
               scripts.Any(s => s.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/');
    }
}
