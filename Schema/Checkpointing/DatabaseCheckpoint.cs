// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Schema.Checkpointing;

internal class DatabaseCheckpoint
{
    public string ProductName { get; set; } = "";
    public string TemplateName { get; set; } = "";
    public string Server { get; set; } = "";
    public string DatabaseName { get; set; } = "";
    public string SchemaName { get; set; } = "";
    public DateTime StartedAt { get; set; } = DateTime.Now;

    public List<string> CompletedSteps { get; set; } = [];
    public Dictionary<DatabaseScriptSlot, List<string>> CompletedScripts { get; set; } = new();

    public void AddStep(string stepName)
    {
        if (!CompletedSteps.Any(s => s.Equals(stepName, StringComparison.OrdinalIgnoreCase)))
            CompletedSteps.Add(stepName);
    }

    public bool HasCompletedStep(string stepName)
    {
        return CompletedSteps.Any(s => s.Equals(stepName, StringComparison.OrdinalIgnoreCase));
    }

    public void AddCompletedScript(DatabaseScriptSlot slot, string scriptPath)
    {
        if (!CompletedScripts.ContainsKey(slot)) CompletedScripts[slot] = new List<string>();
        var normalizedPath = NormalizePath(scriptPath);
        if (!CompletedScripts[slot].Any(s => s.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase)))
            CompletedScripts[slot].Add(normalizedPath);
    }

    public bool HasCompletedScript(DatabaseScriptSlot slot, string scriptPath)
    {
        if (!CompletedScripts.TryGetValue(slot, out var scripts)) return false;
        var normalizedPath = NormalizePath(scriptPath);
        return scripts.Any(s => s.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/');
    }
}
