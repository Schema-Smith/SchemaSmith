// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Schema.Domain;

namespace SchemaQuench;

/// <summary>
/// What a run writes about itself into each target's deployment record. The package hash and the run a resume continued
/// can arrive after the record starts (the hash is computed in the background, the resume is known once the target's
/// checkpoint loads), so they are asked for again when it finishes; <c>wait</c> says whether the hash may be waited for.
/// </summary>
internal sealed record DeploymentRecordRun(Guid RunId, string Product, string ToolVersion, string SourceCommit, string SourceBranch,
    string RunLabel, string PipelineUrl, string Actor, int RetentionDays, Func<bool, string> PackageHash, Func<Guid?> ResumedFromRunId);

/// <summary>
/// The deployment record (DeploymentHistory): one row per deployment of a template to a database, or to one schema of it.
/// Written by SchemaQuench and never read by it. Times are the tool's UTC clock, passed without a kind so PostgreSQL stores
/// them as given in its plain timestamp column instead of converting them through the session's time zone.
/// </summary>
internal static class DeploymentHistory
{
    /// <summary>The audit actions that changed the target; previews, unchanged objects, downgrades and refusals are not changes.</summary>
    internal static readonly HashSet<string> ExecutedActions =
        new(["created", "modified", "dropped", "renamed", "rebuilt", "changed", "ran", "delivered"], StringComparer.Ordinal);

    internal static string Table(Platform platform) => platform.GetBasePlatform() switch
    {
        Platform.SqlServer => "[SchemaSmith].[DeploymentHistory]",
        Platform.PostgreSQL => "\"SchemaSmith\".\"DeploymentHistory\"",
        _ => "SchemaSmith_DeploymentHistory"
    };

    internal static string Column(Platform platform, string name) => platform.GetBasePlatform() switch
    {
        Platform.SqlServer => $"[{name}]",
        Platform.PostgreSQL => $"\"{name}\"",
        _ => $"`{name}`"
    };

    internal static long Start(IDbCommand command, Platform platform, DeploymentRecordRun run, string template, string schema,
        string deployedAs, Guid? resumedFromRunId, DateTime startedUtc)
    {
        var values = new (string Column, object Value)[]
        {
            ("RunId", run.RunId.ToString()), ("Product", run.Product), ("Template", template ?? ""),
            ("SchemaName", string.IsNullOrEmpty(schema) ? null : schema), ("StartedUtc", Unkinded(startedUtc)), ("Outcome", "Started"),
            ("ToolVersion", run.ToolVersion ?? ""), ("PackageHash", run.PackageHash?.Invoke(false)), ("SourceCommit", run.SourceCommit),
            ("SourceBranch", run.SourceBranch), ("RunLabel", run.RunLabel), ("PipelineUrl", run.PipelineUrl), ("Actor", run.Actor),
            ("DeployedAs", deployedAs ?? ""), ("ResumedFromRunId", resumedFromRunId?.ToString())
        };
        command.Parameters.Clear();
        foreach (var (column, value) in values) Add(command, column, value);
        var columns = string.Join(", ", values.Select(v => Column(platform, v.Column)));
        var parameters = string.Join(", ", values.Select(v => "@" + v.Column));
        command.CommandText = platform.GetBasePlatform() switch
        {
            Platform.SqlServer => $"INSERT INTO {Table(platform)} ({columns}) OUTPUT INSERTED.[Id] VALUES ({parameters})",
            Platform.PostgreSQL => $"INSERT INTO {Table(platform)} ({columns}) VALUES ({parameters}) RETURNING \"Id\"",
            _ => $"INSERT INTO {Table(platform)} ({columns}) VALUES ({parameters}); SELECT LAST_INSERT_ID()"
        };
        return Convert.ToInt64(command.ExecuteScalar());
    }

    internal static void Finish(IDbCommand command, Platform platform, long id, string outcome, int? changesApplied, DateTime finishedUtc,
        string packageHash, Guid? resumedFromRunId)
    {
        command.Parameters.Clear();
        Add(command, "Id", id);
        Add(command, "FinishedUtc", Unkinded(finishedUtc));
        Add(command, "Outcome", outcome);
        Add(command, "ChangesApplied", changesApplied, DbType.Int32);
        Add(command, "PackageHash", packageHash);
        Add(command, "ResumedFromRunId", resumedFromRunId?.ToString());
        string C(string name) => Column(platform, name);
        command.CommandText = $"UPDATE {Table(platform)} SET {C("FinishedUtc")} = @FinishedUtc, {C("Outcome")} = @Outcome, "
                              + $"{C("ChangesApplied")} = @ChangesApplied, {C("PackageHash")} = COALESCE(@PackageHash, {C("PackageHash")}), "
                              + $"{C("ResumedFromRunId")} = COALESCE(@ResumedFromRunId, {C("ResumedFromRunId")}) WHERE {C("Id")} = @Id";
        command.ExecuteNonQuery();
    }

    /// <summary>Removes this product's rows that started before the retention period; one statement, on the indexed column.</summary>
    internal static void Prune(IDbCommand command, Platform platform, string product, int retentionDays, DateTime nowUtc)
    {
        if (retentionDays <= 0) return;
        command.Parameters.Clear();
        Add(command, "Product", product);
        Add(command, "Cutoff", Unkinded(nowUtc.AddDays(-retentionDays)));
        command.CommandText = $"DELETE FROM {Table(platform)} WHERE {Column(platform, "Product")} = @Product AND {Column(platform, "StartedUtc")} < @Cutoff";
        command.ExecuteNonQuery();
    }

    private static object Unkinded(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Unspecified);

    private static void Add(IDbCommand command, string name, object value, DbType nullType = DbType.String)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@" + name;
        parameter.Value = value ?? DBNull.Value;
        if (value == null) parameter.DbType = nullType;
        command.Parameters.Add(parameter);
    }
}
