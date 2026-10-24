// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Schema.Isolators;

namespace Schema.Utility;

/// <summary>
/// Writes the fully token-expanded SQL of a step to a re-runnable debug file. Always the real SQL: the file exists to
/// reproduce a failure, and scrubbing SQL text either leaks a value or leaves runnable SQL that writes the placeholder
/// (#576). The shippable log carries only the returned path, never the SQL itself. Every file opens with an owner line
/// naming product, server, database, schema and this run, which is how a later run knows which files it may delete.
/// </summary>
public static class ResolvedSqlArtifactWriter
{
    /// <summary>One per process: the files a run wrote are the ones carrying its id.</summary>
    public static string RunId { get; } = Guid.NewGuid().ToString("N");

    public const string OwnerLinePrefix = "-- SchemaSmith debug SQL";
    private const string RealValuesLine = "-- Holds real values: local debugging only. Do not attach it to a ticket or a CI artifact.";

    public static string OwnerLine(string product, string server, string database, string schema) =>
        $"{OwnerLinePrefix} | Product: {product} | Server: {server} | Database: {database} | Schema: {schema ?? ""} | Run: {RunId}";

    /// <summary>The fields of a file's owner line, or null when the file does not open with one.</summary>
    public sealed record Owner(string Product, string Server, string Database, string Schema, string Run);

    public static Owner ReadOwner(string firstLine)
    {
        if (firstLine == null || !firstLine.StartsWith(OwnerLinePrefix + " | ", StringComparison.Ordinal)) return null;
        var fields = firstLine[(OwnerLinePrefix.Length + 3)..].Split(" | ")
            .Select(f => f.Split(": ", 2)).Where(kv => kv.Length == 2)
            .ToDictionary(kv => kv[0], kv => kv[1].TrimEnd('\r'), StringComparer.Ordinal);
        return fields.TryGetValue("Product", out var product) && fields.TryGetValue("Server", out var server)
               && fields.TryGetValue("Database", out var database) && fields.TryGetValue("Run", out var run)
            ? new Owner(product, server, database, fields.GetValueOrDefault("Schema", ""), run)
            : null;
    }

    /// <summary>
    /// Deletes the debug files in <paramref name="directory"/> whose owner line <paramref name="matches"/>. A file without
    /// an owner line -- an older version's, or the user's own -- is never touched (#576).
    /// </summary>
    public static void DeleteOwned(string directory, Func<Owner, bool> matches)
    {
        var dir = DirectoryWrapper.GetFromFactory();
        var file = FileWrapper.GetFromFactory();
        if (string.IsNullOrWhiteSpace(directory) || !dir.Exists(directory)) return;
        foreach (var path in dir.GetFiles(directory, "SchemaQuench - *.sql", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var text = file.ReadAllText(path);
                var end = text.IndexOf('\n');
                var owner = ReadOwner(end < 0 ? text : text[..end]);
                if (owner != null && matches(owner)) file.Delete(path);
            }
            catch (IOException) { /* in use or gone: leave it */ }
            catch (UnauthorizedAccessException) { /* not ours to delete */ }
        }
    }

    /// <summary>A generated step's SQL with the owner and real-values lines in front of it.</summary>
    public static string WithOwner(string owner, string sql) =>
        owner + Environment.NewLine + RealValuesLine + Environment.NewLine + sql;

    public static string BuildArtifact(string owner, string header, IReadOnlyList<string> batches, int failingBatchIndex)
    {
        var sb = new StringBuilder();
        var singleLineHeader = (header ?? "").Replace("\r", " ").Replace("\n", " ");
        sb.AppendLine(owner);
        sb.AppendLine(RealValuesLine);
        sb.AppendLine("-- ============================================================");
        sb.AppendLine($"-- {singleLineHeader}");
        sb.AppendLine("-- ============================================================");
        for (var i = 0; i < batches.Count; i++)
        {
            if (i == failingBatchIndex) sb.AppendLine($"-- >>> FAILING BATCH (#{i + 1}) >>>");
            sb.AppendLine(batches[i]);
            sb.AppendLine("GO");
        }
        return sb.ToString();
    }

    /// <returns>The full path written, for surfacing in the log.</returns>
    public static string Write(string directory, string fileName, string content)
    {
        var path = Path.Combine(directory, fileName);
        // Always write to real disk via FileWrapper, NOT ProductFileWrapper: a product loaded from a
        // .zip package resolves ProductFileWrapper to the in-memory ZipFileWrapper, which would bury
        // the failure artifact inside the package instead of landing it on a path the user can open.
        DirectoryWrapper.GetFromFactory().CreateDirectory(directory); // idempotent; ensures a configured-but-missing ArtifactPath works
        FileWrapper.GetFromFactory().WriteAllText(path, content);
        return path;
    }

    /// <summary>Builds a failing script's artifact and writes it to disk.</summary>
    /// <returns>The full path written, for surfacing in the log.</returns>
    public static string WriteFailureArtifact(string directory, string owner, string header, IReadOnlyList<string> batches,
        int failingBatchIndex, string fileName) =>
        Write(directory, fileName, BuildArtifact(owner, header, batches, failingBatchIndex));

    /// <summary>
    /// Label-sanitizing overload: callers pass a raw <paramref name="label"/> (e.g. a
    /// "schema.table#variant" delivery key) that may contain characters illegal in a filename.
    /// The label is sanitized (invalid filename chars replaced with '_') and handed to
    /// <paramref name="fileNameFromSafeLabel"/> to compose the final file name, before the shared
    /// BuildArtifact→Scrub→Write sequence runs.
    /// </summary>
    /// <returns>The full path written, for surfacing in the log.</returns>
    public static string WriteFailureArtifact(string directory, string owner, string header,
        IReadOnlyList<string> batches, int failingBatchIndex, string label,
        Func<string, string> fileNameFromSafeLabel)
    {
        var safeLabel = string.Concat((label ?? "").Select(c =>
            Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return WriteFailureArtifact(directory, owner, header, batches, failingBatchIndex, fileNameFromSafeLabel(safeLabel));
    }
}
