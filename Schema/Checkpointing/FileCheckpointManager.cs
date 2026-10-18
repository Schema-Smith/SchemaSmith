// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Schema.Isolators;
using Schema.Utility;

namespace Schema.Checkpointing;

/// <summary>
/// ICheckpointing implementation that tracks completed steps and scripts to disk,
/// enabling SchemaQuench to resume interrupted deployments. Uses INI-style checkpoint
/// files with thread-safe writes.
/// </summary>
public class FileCheckpointManager : ICheckpointing
{
    private readonly string _checkpointDirectory;
    // Whether this run takes up progress files already on disk. Only --ResumeQuench should: the folder is shared by
    // every run for the same user, and the file names do not identify the run, so a run that is not resuming would
    // otherwise read a concurrent run's progress as its own and skip steps it never ran.
    private readonly bool _adoptExisting;
    private readonly ConcurrentDictionary<string, DatabaseCheckpoint> _dbCheckpoints = new();
    private readonly ConcurrentDictionary<string, ProductCheckpoint> _productCheckpoints = new();
    private static readonly ConcurrentDictionary<string, object> FileLocks = new();

    public FileCheckpointManager() : this(GetDefaultCheckpointDirectory()) { }

    public FileCheckpointManager(string checkpointDirectory)
        : this(checkpointDirectory, CommandLineParser.ContainsSwitch("ResumeQuench")) { }

    public FileCheckpointManager(string checkpointDirectory, bool adoptExisting)
    {
        _adoptExisting = adoptExisting;
        _checkpointDirectory = checkpointDirectory ?? throw new ArgumentNullException(nameof(checkpointDirectory));
        if (!DirectoryWrapper.GetFromFactory().Exists(_checkpointDirectory))
            DirectoryWrapper.GetFromFactory().CreateDirectory(_checkpointDirectory);
    }

    public static ICheckpointing GetFromFactory()
        => FactoryContainer.ResolveOrCreate<ICheckpointing, FileCheckpointManager>();

    private static string GetDefaultCheckpointDirectory()
        => Path.Combine(Path.GetTempPath(), "schemaquench-checkpoints");

    public void Track(TrackingScope scope, string stepName, Action work)
    {
        if (IsDatabaseScope(scope))
        {
            var checkpoint = GetOrLoadDatabaseCheckpoint(scope);
            if (checkpoint.HasCompletedStep(stepName)) return;

            work();

            checkpoint.AddStep(stepName);
            SaveDatabaseCheckpoint(checkpoint);
        }
        else
        {
            var checkpoint = GetOrLoadProductCheckpoint(scope.ProductName);
            if (checkpoint.HasCompletedTemplate(stepName)) return;

            work();

            checkpoint.AddTemplate(stepName);
            SaveProductCheckpoint(checkpoint);
        }
    }

    public void TrackScript(TrackingScope scope, string slot, string scriptPath, Action work)
    {
        if (IsDatabaseScope(scope))
        {
            var checkpoint = GetOrLoadDatabaseCheckpoint(scope);
            if (Enum.TryParse<DatabaseScriptSlot>(slot, true, out var scriptSlot) &&
                checkpoint.HasCompletedScript(scriptSlot, scriptPath))
                return;

            work();

            if (Enum.TryParse(slot, true, out scriptSlot))
            {
                checkpoint.AddCompletedScript(scriptSlot, scriptPath);
                SaveDatabaseCheckpoint(checkpoint);
            }
        }
        else
        {
            var checkpoint = GetOrLoadProductCheckpoint(scope.ProductName);
            var server = scope.Server ?? "default";
            var normalizedPath = scriptPath.Replace('\\', '/');

            if (slot.Equals("Before", StringComparison.OrdinalIgnoreCase) &&
                checkpoint.HasCompletedBeforeScript(server, normalizedPath))
                return;
            if (slot.Equals("After", StringComparison.OrdinalIgnoreCase) &&
                checkpoint.HasCompletedAfterScript(server, normalizedPath))
                return;

            work();

            if (slot.Equals("Before", StringComparison.OrdinalIgnoreCase))
                checkpoint.AddBeforeScript(server, normalizedPath);
            else if (slot.Equals("After", StringComparison.OrdinalIgnoreCase))
                checkpoint.AddAfterScript(server, normalizedPath);

            SaveProductCheckpoint(checkpoint);
        }
    }

    public bool ResumedAny { get; private set; }

    // A plain run discards only its own targets' checkpoints, as it reaches each one; a failed deploy of the same
    // product to another database keeps its resume point.
    public void DeleteCheckpointsOfThisRun(string productName)
    {
        foreach (var checkpoint in _dbCheckpoints.Where(kv => kv.Key.StartsWith(productName + "|", StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Value).ToList())
        {
            var filePath = GetDatabaseCheckpointPath(checkpoint.ProductName, checkpoint.TemplateName, checkpoint.Server, checkpoint.DatabaseName, checkpoint.SchemaName);
            try { FileWrapper.GetFromFactory().Delete(filePath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        foreach (var key in _dbCheckpoints.Keys.Where(k => k.StartsWith(productName + "|", StringComparison.OrdinalIgnoreCase)).ToList())
            _dbCheckpoints.TryRemove(key, out _);
        DeleteProductCheckpoint(productName);
        _productCheckpoints.TryRemove(productName, out _);
    }

    /// <summary>Clean up checkpoint files after a successful deployment.</summary>
    public void DeleteCheckpoints(string productName)
    {
        DeleteProductCheckpoint(productName);
        DeleteDatabaseCheckpoints(productName);
        _productCheckpoints.TryRemove(productName, out _);
        var keysToRemove = _dbCheckpoints.Keys.Where(k => k.StartsWith(productName + "|", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var key in keysToRemove)
            _dbCheckpoints.TryRemove(key, out _);
    }

    public bool HasCompleted(TrackingScope scope, string stepName)
    {
        if (IsDatabaseScope(scope))
            return GetOrLoadDatabaseCheckpoint(scope).HasCompletedStep(stepName);
        return GetOrLoadProductCheckpoint(scope.ProductName).HasCompletedTemplate(stepName);
    }

    public bool HasCompletedScript(TrackingScope scope, string slot, string scriptPath)
    {
        if (IsDatabaseScope(scope))
        {
            if (!Enum.TryParse<DatabaseScriptSlot>(slot, true, out var scriptSlot)) return false;
            return GetOrLoadDatabaseCheckpoint(scope).HasCompletedScript(scriptSlot, scriptPath);
        }

        var productCheckpoint = GetOrLoadProductCheckpoint(scope.ProductName);
        var server = scope.Server ?? "default";
        var normalizedPath = scriptPath.Replace('\\', '/');
        return slot.Equals("Before", StringComparison.OrdinalIgnoreCase)
            ? productCheckpoint.HasCompletedBeforeScript(server, normalizedPath)
            : slot.Equals("After", StringComparison.OrdinalIgnoreCase)
                && productCheckpoint.HasCompletedAfterScript(server, normalizedPath);
    }

    public ProductCheckpointSummary GetProductCheckpointSummary(string productName)
    {
        var filePath = GetProductCheckpointPath(productName);
        if (!FileWrapper.GetFromFactory().Exists(filePath)) return ProductCheckpointSummary.Empty;

        var checkpoint = GetOrLoadProductCheckpoint(productName);
        return new ProductCheckpointSummary(
            TotalBeforeScripts: checkpoint.CompletedBeforeScripts.Values.Sum(s => s.Count),
            ServersWithBeforeScripts: checkpoint.CompletedBeforeScripts.Count,
            CompletedTemplates: checkpoint.CompletedTemplates.Count,
            TotalAfterScripts: checkpoint.CompletedAfterScripts.Values.Sum(s => s.Count),
            ServersWithAfterScripts: checkpoint.CompletedAfterScripts.Count);
    }

    public void MarkStepCompleted(TrackingScope scope, string stepName)
    {
        if (IsDatabaseScope(scope))
        {
            var checkpoint = GetOrLoadDatabaseCheckpoint(scope);
            checkpoint.AddStep(stepName);
            SaveDatabaseCheckpoint(checkpoint);
        }
        else
        {
            var checkpoint = GetOrLoadProductCheckpoint(scope.ProductName);
            checkpoint.AddTemplate(stepName);
            SaveProductCheckpoint(checkpoint);
        }
    }

    public void MarkScriptCompleted(TrackingScope scope, string slot, string scriptPath)
    {
        if (IsDatabaseScope(scope))
        {
            if (!Enum.TryParse<DatabaseScriptSlot>(slot, true, out var scriptSlot)) return;
            var checkpoint = GetOrLoadDatabaseCheckpoint(scope);
            checkpoint.AddCompletedScript(scriptSlot, scriptPath);
            SaveDatabaseCheckpoint(checkpoint);
        }
        else
        {
            var checkpoint = GetOrLoadProductCheckpoint(scope.ProductName);
            var server = scope.Server ?? "default";
            var normalizedPath = scriptPath.Replace('\\', '/');
            if (slot.Equals("Before", StringComparison.OrdinalIgnoreCase))
                checkpoint.AddBeforeScript(server, normalizedPath);
            else if (slot.Equals("After", StringComparison.OrdinalIgnoreCase))
                checkpoint.AddAfterScript(server, normalizedPath);
            SaveProductCheckpoint(checkpoint);
        }
    }

    public DatabaseCheckpointSummary GetDatabaseCheckpointSummary(TrackingScope scope)
    {
        if (!IsDatabaseScope(scope)) return DatabaseCheckpointSummary.Empty;

        var filePath = GetDatabaseCheckpointPath(scope.ProductName, scope.TemplateName, scope.Server, scope.DatabaseName, scope.SchemaName);
        if (!FileWrapper.GetFromFactory().Exists(filePath)) return DatabaseCheckpointSummary.Empty;

        var checkpoint = GetOrLoadDatabaseCheckpoint(scope);
        return new DatabaseCheckpointSummary(
            CompletedSteps: checkpoint.CompletedSteps.Count,
            TotalCompletedScripts: checkpoint.CompletedScripts.Values.Sum(s => s.Count));
    }

    private static bool IsDatabaseScope(TrackingScope scope) =>
        !string.IsNullOrEmpty(scope.TemplateName) && !string.IsNullOrEmpty(scope.DatabaseName);

    #region Database Checkpoint I/O

    private DatabaseCheckpoint GetOrLoadDatabaseCheckpoint(TrackingScope scope)
    {
        // CompositeKey includes SchemaName so two iterations of the same template against
        // the same DB but for different tenant schemas don't collide.
        return _dbCheckpoints.GetOrAdd(scope.CompositeKey, _ => LoadDatabaseCheckpoint(scope));
    }

    private DatabaseCheckpoint LoadDatabaseCheckpoint(TrackingScope scope)
    {
        var filePath = GetDatabaseCheckpointPath(scope.ProductName, scope.TemplateName, scope.Server, scope.DatabaseName, scope.SchemaName);
        var exists = FileWrapper.GetFromFactory().Exists(filePath);
        // A run that is not resuming starts this target fresh, so a resume after it fails cannot take up an older run's steps.
        if (exists && !_adoptExisting)
        {
            try { FileWrapper.GetFromFactory().Delete(filePath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        if (exists && _adoptExisting) ResumedAny = true;
        if (!_adoptExisting || !exists)
            return new DatabaseCheckpoint
            {
                ProductName = scope.ProductName,
                TemplateName = scope.TemplateName,
                Server = scope.Server,
                DatabaseName = scope.DatabaseName,
                SchemaName = scope.SchemaName ?? ""
            };

        try
        {
            var content = FileWrapper.GetFromFactory().ReadAllText(filePath);
            return ParseDatabaseCheckpoint(content, scope.ProductName, scope.TemplateName, scope.Server, scope.DatabaseName, scope.SchemaName);
        }
        catch
        {
            return new DatabaseCheckpoint
            {
                ProductName = scope.ProductName,
                TemplateName = scope.TemplateName,
                Server = scope.Server,
                DatabaseName = scope.DatabaseName,
                SchemaName = scope.SchemaName ?? ""
            };
        }
    }

    private void SaveDatabaseCheckpoint(DatabaseCheckpoint checkpoint)
    {
        var filePath = GetDatabaseCheckpointPath(checkpoint.ProductName, checkpoint.TemplateName, checkpoint.Server, checkpoint.DatabaseName, checkpoint.SchemaName);
        WriteFileThreadSafe(filePath, FormatDatabaseCheckpoint(checkpoint));
    }

    private void DeleteDatabaseCheckpoints(string productName)
    {
        var pattern = $"{FileNameEncoder.Encode(productName)}.*.checkpoint";
        try
        {
            var files = DirectoryWrapper.GetFromFactory().GetFiles(_checkpointDirectory, pattern, SearchOption.TopDirectoryOnly);
            foreach (var file in files.Where(f => !f.EndsWith(".product.checkpoint")))
            {
                try { FileWrapper.GetFromFactory().Delete(file); }
                catch { }
            }
        }
        catch { }
    }

    private string GetDatabaseCheckpointPath(string productName, string templateName, string server, string databaseName, string schemaName)
    {
        // SchemaName segment is included in the filename so per-iteration checkpoints
        // for schema templates don't collide on disk. Empty schema_name produces an
        // empty segment which FileNameEncoder represents stably for regular templates.
        return Path.Combine(_checkpointDirectory,
            $"{FileNameEncoder.Encode(productName)}.{FileNameEncoder.Encode(templateName)}.{FileNameEncoder.Encode(server)}.{FileNameEncoder.Encode(databaseName)}.{FileNameEncoder.Encode(schemaName ?? "")}.checkpoint");
    }

    internal static DatabaseCheckpoint ParseDatabaseCheckpoint(string content, string productName, string templateName, string server, string databaseName, string schemaName = "")
    {
        var checkpoint = new DatabaseCheckpoint
        {
            ProductName = productName,
            TemplateName = templateName,
            Server = server,
            DatabaseName = databaseName,
            SchemaName = schemaName ?? ""
        };

        var lines = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        string currentSection = null;

        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmedLine)) continue;

            if (trimmedLine.StartsWith("# Started:"))
            {
                var dateStr = trimmedLine.Substring("# Started:".Length).Trim();
                if (DateTime.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var startedAt))
                    checkpoint.StartedAt = startedAt;
                continue;
            }

            if (trimmedLine.StartsWith("#")) continue;

            if (trimmedLine.StartsWith("[") && trimmedLine.EndsWith("]"))
            {
                currentSection = trimmedLine.Substring(1, trimmedLine.Length - 2);
                continue;
            }

            var slotMap = new Dictionary<string, DatabaseScriptSlot>(StringComparer.OrdinalIgnoreCase)
            {
                ["Completed Steps"] = (DatabaseScriptSlot)(-1),
                ["Before Scripts"] = DatabaseScriptSlot.Before,
                ["Object Scripts"] = DatabaseScriptSlot.Object,
                ["After Tables Object Scripts"] = DatabaseScriptSlot.AfterTablesObject,
                ["Between Tables And Keys Scripts"] = DatabaseScriptSlot.BetweenTablesAndKeys,
                ["After Table Scripts"] = DatabaseScriptSlot.AfterTable,
                ["Table Data Scripts"] = DatabaseScriptSlot.TableData,
                ["After Scripts"] = DatabaseScriptSlot.After
            };

            if (currentSection != null && slotMap.TryGetValue(currentSection, out var slot))
            {
                if ((int)slot == -1)
                    checkpoint.CompletedSteps.Add(trimmedLine);
                else
                    checkpoint.AddCompletedScript(slot, trimmedLine);
            }
        }

        return checkpoint;
    }

    internal static string FormatDatabaseCheckpoint(DatabaseCheckpoint checkpoint)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# SchemaQuench Database Checkpoint");
        sb.AppendLine($"# Product: {checkpoint.ProductName}");
        sb.AppendLine($"# Template: {checkpoint.TemplateName}");
        sb.AppendLine($"# Server: {checkpoint.Server}");
        sb.AppendLine($"# Database: {checkpoint.DatabaseName}");
        if (!string.IsNullOrEmpty(checkpoint.SchemaName))
            sb.AppendLine($"# Schema: {checkpoint.SchemaName}");
        sb.AppendLine($"# Started: {checkpoint.StartedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        sb.AppendLine("[Completed Steps]");
        foreach (var step in checkpoint.CompletedSteps) sb.AppendLine(step);
        sb.AppendLine();

        WriteSlotScripts(checkpoint, DatabaseScriptSlot.Before, "Before Scripts", sb);
        WriteSlotScripts(checkpoint, DatabaseScriptSlot.Object, "Object Scripts", sb);
        WriteSlotScripts(checkpoint, DatabaseScriptSlot.AfterTablesObject, "After Tables Object Scripts", sb);
        WriteSlotScripts(checkpoint, DatabaseScriptSlot.BetweenTablesAndKeys, "Between Tables And Keys Scripts", sb);
        WriteSlotScripts(checkpoint, DatabaseScriptSlot.AfterTable, "After Table Scripts", sb);
        WriteSlotScripts(checkpoint, DatabaseScriptSlot.TableData, "Table Data Scripts", sb);
        WriteSlotScripts(checkpoint, DatabaseScriptSlot.After, "After Scripts", sb);

        return sb.ToString();
    }

    private static void WriteSlotScripts(DatabaseCheckpoint checkpoint, DatabaseScriptSlot slot, string sectionName, StringBuilder sb)
    {
        sb.AppendLine($"[{sectionName}]");
        if (checkpoint.CompletedScripts.TryGetValue(slot, out var scripts))
            foreach (var script in scripts)
                sb.AppendLine(script);
        sb.AppendLine();
    }

    #endregion

    #region Product Checkpoint I/O

    private ProductCheckpoint GetOrLoadProductCheckpoint(string productName)
    {
        return _productCheckpoints.GetOrAdd(productName, _ => LoadProductCheckpoint(productName));
    }

    private ProductCheckpoint LoadProductCheckpoint(string productName)
    {
        var filePath = GetProductCheckpointPath(productName);
        if (!_adoptExisting || !FileWrapper.GetFromFactory().Exists(filePath))
            return new ProductCheckpoint { ProductName = productName };

        try
        {
            var content = FileWrapper.GetFromFactory().ReadAllText(filePath);
            return ParseProductCheckpoint(content, productName);
        }
        catch
        {
            return new ProductCheckpoint { ProductName = productName };
        }
    }

    private void SaveProductCheckpoint(ProductCheckpoint checkpoint)
    {
        var filePath = GetProductCheckpointPath(checkpoint.ProductName);
        WriteFileThreadSafe(filePath, FormatProductCheckpoint(checkpoint));
    }

    private void DeleteProductCheckpoint(string productName)
    {
        var filePath = GetProductCheckpointPath(productName);
        try { if (FileWrapper.GetFromFactory().Exists(filePath)) FileWrapper.GetFromFactory().Delete(filePath); }
        catch { }
    }

    private string GetProductCheckpointPath(string productName)
    {
        return Path.Combine(_checkpointDirectory, $"{FileNameEncoder.Encode(productName)}.product.checkpoint");
    }

    internal static ProductCheckpoint ParseProductCheckpoint(string content, string productName)
    {
        var checkpoint = new ProductCheckpoint { ProductName = productName };
        var lines = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        string currentSection = null;
        string currentServer = null;

        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmedLine)) continue;

            if (trimmedLine.StartsWith("# Started:"))
            {
                var dateStr = trimmedLine.Substring("# Started:".Length).Trim();
                if (DateTime.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var startedAt))
                    checkpoint.StartedAt = startedAt;
                continue;
            }

            if (trimmedLine.StartsWith("#")) continue;

            if (trimmedLine.StartsWith("[") && trimmedLine.EndsWith("]"))
            {
                var sectionContent = trimmedLine.Substring(1, trimmedLine.Length - 2);

                if (sectionContent.StartsWith("Before Product Scripts - "))
                {
                    currentSection = "Before Product Scripts";
                    currentServer = sectionContent.Substring("Before Product Scripts - ".Length);
                }
                else if (sectionContent.StartsWith("After Product Scripts - "))
                {
                    currentSection = "After Product Scripts";
                    currentServer = sectionContent.Substring("After Product Scripts - ".Length);
                }
                else
                {
                    currentSection = sectionContent;
                    currentServer = null;
                }
                continue;
            }

            switch (currentSection)
            {
                case "Before Product Scripts" when currentServer != null:
                    checkpoint.AddBeforeScript(currentServer, trimmedLine);
                    break;
                case "Completed Templates":
                    checkpoint.CompletedTemplates.Add(trimmedLine);
                    break;
                case "After Product Scripts" when currentServer != null:
                    checkpoint.AddAfterScript(currentServer, trimmedLine);
                    break;
            }
        }

        return checkpoint;
    }

    internal static string FormatProductCheckpoint(ProductCheckpoint checkpoint)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# SchemaQuench Product Checkpoint");
        sb.AppendLine($"# Product: {checkpoint.ProductName}");
        sb.AppendLine($"# Started: {checkpoint.StartedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        foreach (var serverScripts in checkpoint.CompletedBeforeScripts.OrderBy(kvp => kvp.Key))
        {
            sb.AppendLine($"[Before Product Scripts - {serverScripts.Key}]");
            foreach (var script in serverScripts.Value) sb.AppendLine(script);
            sb.AppendLine();
        }

        sb.AppendLine("[Completed Templates]");
        foreach (var template in checkpoint.CompletedTemplates) sb.AppendLine(template);
        sb.AppendLine();

        foreach (var serverScripts in checkpoint.CompletedAfterScripts.OrderBy(kvp => kvp.Key))
        {
            sb.AppendLine($"[After Product Scripts - {serverScripts.Key}]");
            foreach (var script in serverScripts.Value) sb.AppendLine(script);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    #endregion

    private void WriteFileThreadSafe(string filePath, string content)
    {
        var lockObj = FileLocks.GetOrAdd(filePath, _ => new object());
        lock (lockObj)
        {
            FileWrapper.GetFromFactory().WriteAllText(filePath, content);
        }
    }
}
