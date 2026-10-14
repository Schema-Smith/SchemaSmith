// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using log4net;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaTongs;

public class OrphanHandler
{
    private readonly ILog _log = LogFactory.GetLogger("ProgressLog");

    public void ProcessOrphans(
        Dictionary<string, ExtractionFileIndex> folderIndexes,
        Platform platform,
        OrphanHandlingMode mode,
        HashSet<string> fullyExtractedFolders,
        string logsDirectory,
        Dictionary<string, ScriptObjectType> folderObjectTypes)
    {
        var allOrphans = new Dictionary<string, List<string>>();

        foreach (var (folderName, index) in folderIndexes)
        {
            if (!fullyExtractedFolders.Contains(folderName)) continue;

            var orphans = index.GetOrphans();

            // A gated table absent from this source is the ShouldApplyExpression's doing,
            // not a removal — its declaration file is not an orphan.
            if (folderName == "Tables")
            {
                var gatedCount = orphans.Count(path => IsGatedTableFile(path, platform));
                orphans = orphans.Where(path => !IsGatedTableFile(path, platform)).ToList();
                if (gatedCount > 0)
                    _log.Info($"  Retained {gatedCount} gated table declaration file(s) (ShouldApplyExpression gated them off this source; not treated as orphans)");
            }

            // Extraction never reads SchemaSmith's own namespace, so a file there was never going to be marked
            // written. Those files are user-authored (the recycle-bin hooks live there), and a cleanup script
            // would drop them from the database.
            var excludedCount = orphans.Count(path => IsInExcludedNamespace(path, platform));
            if (excludedCount > 0)
            {
                orphans = orphans.Where(path => !IsInExcludedNamespace(path, platform)).ToList();
                _log.Info($"  Retained {excludedCount} file(s) in {folderName} for objects in the SchemaSmith namespace, which extraction does not read (not treated as orphans)");
            }

            if (orphans.Count > 0)
                allOrphans[folderName] = orphans;
        }

        if (allOrphans.Count == 0) return;

        LogOrphans(allOrphans);

        if (mode == OrphanHandlingMode.Detect) return;

        ArchiveExistingCleanupScripts(logsDirectory);
        GenerateCleanupScripts(allOrphans, platform, logsDirectory, folderObjectTypes);

        if (mode == OrphanHandlingMode.DetectDeleteAndCleanup)
            DeleteOrphanFiles(allOrphans);
    }

    private void LogOrphans(Dictionary<string, List<string>> allOrphans)
    {
        var totalCount = allOrphans.Values.Sum(l => l.Count);
        _log.Warn($"{totalCount} orphaned file(s) detected across {allOrphans.Count} folder(s):");

        foreach (var (folderName, orphans) in allOrphans)
        {
            _log.Warn($"  {folderName}: {orphans.Count} orphan(s)");
            foreach (var orphan in orphans)
                _log.Warn($"    {Path.GetFileName(orphan)}");
        }
    }

    private void ArchiveExistingCleanupScripts(string logsDirectory)
    {
        var directory = DirectoryWrapper.GetFromFactory();
        var file = FileWrapper.GetFromFactory();

        if (!directory.Exists(logsDirectory)) return;

        var existingScripts = directory.GetFiles(logsDirectory, "_OrphanCleanup_*.sql", SearchOption.TopDirectoryOnly);
        if (existingScripts.Length == 0) return;

        var archiveDir = Path.Combine(logsDirectory, DateTime.Now.ToString("yyyy-MM-dd_HHmmss"));
        directory.CreateDirectory(archiveDir);

        foreach (var script in existingScripts)
        {
            var destPath = Path.Combine(archiveDir, Path.GetFileName(script));
            file.Move(script, destPath);
        }
    }

    private void GenerateCleanupScripts(
        Dictionary<string, List<string>> allOrphans,
        Platform platform,
        string logsDirectory,
        Dictionary<string, ScriptObjectType> folderObjectTypes)
    {
        var file = FileWrapper.GetFromFactory();
        var directory = DirectoryWrapper.GetFromFactory();

        foreach (var (folderName, orphans) in allOrphans)
        {
            var objectType = folderObjectTypes.GetValueOrDefault(folderName, ScriptObjectType.None);
            var fileNames = orphans.Select(Path.GetFileName).ToList();

            var script = CleanupScriptGenerator.GenerateCleanupScript(fileNames, objectType, platform, folderName);
            if (script == null) continue;

            directory.CreateDirectory(logsDirectory);
            var scriptFileName = $"_OrphanCleanup_{folderName.Replace(" ", "")}.sql";
            var scriptPath = Path.Combine(logsDirectory, scriptFileName);
            file.WriteAllText(scriptPath, script);

            var dropCount = fileNames.Count(f =>
                CleanupScriptGenerator.GenerateDropStatement(f, objectType, platform, folderName) != null);
            _log.Info($"Generated {scriptFileName} with {dropCount} DROP statement(s).");
        }
    }

    internal static bool IsInExcludedNamespace(string path, Platform platform)
    {
        var prefix = platform.GetBasePlatform() == Platform.MySQL ? "SchemaSmith_" : "SchemaSmith.";
        return Path.GetFileName(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGatedTableFile(string path, Platform platform)
    {
        try
        {
            var table = JsonHelper.TableLoad(path, platform);
            return !string.IsNullOrWhiteSpace(table?.ShouldApplyExpression);
        }
        catch
        {
            return false; // unreadable file: treat as a normal orphan
        }
    }

    private void DeleteOrphanFiles(Dictionary<string, List<string>> allOrphans)
    {
        var file = FileWrapper.GetFromFactory();
        foreach (var orphans in allOrphans.Values)
        {
            foreach (var orphan in orphans)
                file.Delete(orphan);
        }
    }
}
