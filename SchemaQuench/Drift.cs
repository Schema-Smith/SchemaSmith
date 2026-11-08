// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Schema.Domain;
using Schema.Utility;

namespace SchemaQuench;

/// <summary>
/// How a drift check reads the targets (H1). Tier 0 calls the helpers already on the target when kindling is off. Tier 1
/// kindles inside one transaction that is always rolled back (SQL Server, PostgreSQL). Tier 2 kindles into a separate helper
/// database (MySQL, MariaDB), because their DDL commits on its own and kindling into the target would leave a footprint.
/// </summary>
public static class DriftTiers
{
    public static int Choose(Platform platform, bool kindlingOff, string helperDatabase)
    {
        if (kindlingOff) return 0;
        if (platform.GetBasePlatform() != Platform.MySQL) return 1;
        if (!string.IsNullOrWhiteSpace(helperDatabase)) return 2;
        throw new RunFailedException($"--Drift on {platform.ToDisplayName()} cannot kindle the SchemaSmith helpers into the target without leaving them "
                                     + "there. Either kindle them first (any deploy does) and set KindleTheForge to false, or set "
                                     + "Target:DriftHelperDatabase to a database the drift account owns.");
    }

    public static string Explain(int tier) => tier switch
    {
        0 => "Drift check, tier 0: KindleTheForge is false, so the helpers already on each target answer, after a check that they are this build's.",
        1 => "Drift check, tier 1: the helpers are kindled inside a transaction on one connection per target, and everything is rolled back.",
        _ => "Drift check, tier 2: the helpers are kindled into Target:DriftHelperDatabase and read each target from there.",
    };
}

/// <summary>
/// What --DriftFailOn counts: actions (create, modify, drop) and object types, each defaulting to all. A row counts when its
/// action and its type are both listed. Modules are a type ("modules"); run-once migrations are one too ("migrations"), and
/// the only type left out by default -- a pending migration means "this release is not deployed yet".
/// </summary>
public sealed class DriftFailOn
{
    public const string Modules = "modules";
    public const string Migrations = "migrations";

    public static readonly IReadOnlyList<string> ActionTokens = ["create", "modify", "drop"];

    public static readonly IReadOnlyList<string> TypeTokens =
    [
        "table", "column", "index", "constraint", "foreignKey", "statistic", "xmlIndex", "fullTextIndex", "sequence", "domainType",
        "domainConstraint", "enumType", "enumValue", "temporal", "changeDataCapture", "changeTracking", "database", "schema",
        "indexedView", "materializedView", "event", "period", "serverSetting", "databaseSetting", Modules, Migrations,
    ];

    private readonly HashSet<string> _actions;
    private readonly HashSet<string> _types;

    private DriftFailOn(IReadOnlyList<string> actions, IReadOnlyList<string> types)
    {
        _actions = new HashSet<string>(actions.Count > 0 ? actions : ActionTokens);
        _types = types.Count > 0 ? new HashSet<string>(types.Select(Normalize)) : null;
        Tokens = [.. actions.Count > 0 ? actions : ActionTokens, .. types.Count > 0 ? types : [Modules]];
    }

    /// <summary>The effective list, as the summary reports it.</summary>
    public IReadOnlyList<string> Tokens { get; }

    public bool CountsMigrations => _types?.Contains(Migrations) == true;

    /// <summary>Whether a change of <paramref name="actionClass"/> (create, modify, drop) to an object of this type counts.</summary>
    public bool Counts(string actionClass, string objectType) =>
        actionClass != null && _actions.Contains(actionClass)
        && (_types == null ? Normalize(objectType) != Migrations : _types.Contains(Normalize(objectType)));

    public static DriftFailOn Parse(string value)
    {
        var actions = new List<string>();
        var types = new List<string>();
        foreach (var raw in (value ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = Normalize(raw);
            if (ActionTokens.Contains(token)) actions.Add(token);
            else if (TypeTokens.FirstOrDefault(t => Normalize(t) == token) is { } type) types.Add(type);
            else
                throw new RunFailedException($"--DriftFailOn: '{raw}' is neither an action ({string.Join(", ", ActionTokens)}) nor an object type "
                                            + $"({string.Join(", ", TypeTokens)}).");
        }
        return new DriftFailOn(actions, types);
    }

    // The audit spells some types with a space ("foreign key", "domain type"); a token is matched without spaces or case.
    internal static string Normalize(string value) => (value ?? "").Replace(" ", "").ToLowerInvariant();
}

public enum DriftModuleState
{
    /// <summary>Its stamp says the script or the module the server renders changed since the last apply.</summary>
    WouldApply,
    /// <summary>Not on the target at all.</summary>
    Missing,
    /// <summary>Never stamped, so whether it changed cannot be said.</summary>
    Unverifiable,
}

public sealed record DriftModule(ChangeTarget Target, string Script, DriftModuleState State);

public sealed record DriftMigration(ChangeTarget Target, string Script, string Slot);

/// <summary>Modules and run-once migrations a drift run found, which the change audit does not carry. Safe under the fan-out.</summary>
public sealed class DriftCapture
{
    private readonly ConcurrentQueue<DriftModule> _modules = new();
    private readonly ConcurrentQueue<DriftMigration> _migrations = new();

    public void RecordModule(ChangeTarget target, string script, DriftModuleState state) => _modules.Enqueue(new DriftModule(target, script, state));

    public void RecordMigration(ChangeTarget target, string script, string slot) => _migrations.Enqueue(new DriftMigration(target, script, slot));

    public IReadOnlyList<DriftModule> Modules => _modules.ToArray();

    public IReadOnlyList<DriftMigration> Migrations => _migrations.ToArray();
}

/// <summary>One work unit a drift run checked.</summary>
public sealed record DriftUnit(string Server, string Database, string Schema, string Template, bool Failed, bool Skipped)
{
    public ChangeTarget Target => new(Server, Database, string.IsNullOrEmpty(Schema) ? null : Schema);
    public string Display => string.IsNullOrEmpty(Schema) ? $"{Server}.{Database}" : $"{Server}.{Database}.{Schema}";
}

/// <summary>
/// Status: drifted, clean, unverifiable (nothing counted, something could not be compared) or failed. Changes are the
/// counted ones, each with its action class (create, modify, drop).
/// </summary>
public sealed record DriftTargetReport(string Server, string Database, string Schema, string Template, string Status, int Counted, string Plan,
    IReadOnlyList<DriftItem> Changes);

public sealed record DriftPlanGroup(string Template, string Plan, int Targets, string Sample);

public sealed record DriftItem(string Server, string Database, string Schema, string ObjectType, string ObjectName, string Action);

public sealed record DriftReport(
    bool Drifted,
    IReadOnlyList<string> FailOn,
    IReadOnlyList<DriftTargetReport> Targets,
    IReadOnlyList<DriftPlanGroup> PlanGroups,
    IReadOnlyList<DriftItem> PendingMigrations,
    IReadOnlyList<DriftItem> Unverifiable,
    IReadOnlyList<DriftItem> Extra);

/// <summary>
/// H1: a target has drifted when the plan a real deploy with the same settings would execute is not empty -- the preview's
/// own decisions, read back from the change audit, not a second comparison.
/// </summary>
public static class DriftVerdict
{
    /// <summary>2 a target failed or could not be checked; 5 drift; 1 finished but not everything applies; 0 clean.</summary>
    public static int ExitCode(bool anyFailed, bool drifted, bool incomplete) => anyFailed ? 2 : drifted ? 5 : incomplete ? 1 : 0;

    /// <summary>
    /// The class a preview action belongs to, or null for one that says nothing about state: a delivery runs every deploy, a
    /// downgrade or a refusal is not a change, and the grants a module gets back follow the module, which counts itself.
    /// </summary>
    internal static string ActionClass(string action) => action switch
    {
        "wouldCreate" => "create",
        "wouldModify" or "wouldRebuild" or "wouldChange" => "modify",
        "wouldDrop" => "drop",
        _ => null,
    };

    private static bool OnServerOf(ChangeTarget row, ChangeTarget target) =>
        row != null && row.Database == null && string.Equals(row.Server, target.Server, StringComparison.OrdinalIgnoreCase);

    public static DriftReport Decide(IReadOnlyList<DriftUnit> units, IReadOnlyList<ChangeAuditRow> audit, DriftCapture capture, DriftFailOn failOn)
    {
        var targets = new List<DriftTargetReport>();
        var pending = new List<DriftItem>();
        var unverifiable = new List<DriftItem>();
        var extra = new List<DriftItem>();
        foreach (var unit in units)
        {
            var target = unit.Target;
            var counted = new List<(string Type, string Name, string Class)>();
            // A server setting is recorded for the server alone: every target on it is one the deploy would change.
            foreach (var row in audit.Where(r => r.Target == target || OnServerOf(r.Target, target)))
            {
                if (row.Action == "dropSuppressed")
                    extra.Add(Item(unit, row.ObjectType, row.ObjectName, row.Action));
                else if (ActionClass(row.Action) is { } cls && failOn.Counts(cls, row.ObjectType))
                    counted.Add((row.ObjectType, row.ObjectName, cls));
            }
            var unitUnverifiable = 0;
            foreach (var module in capture.Modules.Where(m => m.Target == target))
            {
                if (module.State == DriftModuleState.Unverifiable)
                {
                    unverifiable.Add(Item(unit, DriftFailOn.Modules, module.Script, "unverifiable"));
                    unitUnverifiable++;
                    continue;
                }
                var cls = module.State == DriftModuleState.Missing ? "create" : "modify";
                if (failOn.Counts(cls, DriftFailOn.Modules)) counted.Add((DriftFailOn.Modules, module.Script, cls));
            }
            foreach (var migration in capture.Migrations.Where(m => m.Target == target))
            {
                pending.Add(Item(unit, DriftFailOn.Migrations, migration.Script, "pending"));
                if (failOn.CountsMigrations) counted.Add((DriftFailOn.Migrations, migration.Script, "create"));
            }

            var status = unit.Failed ? "failed"
                : counted.Count > 0 ? "drifted"
                : unitUnverifiable > 0 || unit.Skipped ? "unverifiable"
                : "clean";
            targets.Add(new DriftTargetReport(unit.Server, unit.Database, unit.Schema ?? "", unit.Template, status, counted.Count,
                Fingerprint(counted, unit.Schema), counted.Select(c => Item(unit, c.Type, c.Name, c.Class)).ToList()));
        }

        var groups = targets.Where(t => t.Status is "drifted" or "clean")
            .GroupBy(t => (t.Template, t.Plan))
            .Select(g => new DriftPlanGroup(g.Key.Template, g.Key.Plan, g.Count(),
                g.Select(t => Display(t)).OrderBy(s => s, StringComparer.Ordinal).First()))
            .OrderBy(g => g.Template, StringComparer.Ordinal).ThenByDescending(g => g.Targets)
            .ToList();

        return new DriftReport(targets.Any(t => t.Status == "drifted"), failOn.Tokens, targets, groups, pending, unverifiable, extra);
    }

    /// <summary>
    /// The console verdict: one line per plan of each template rather than the detail of every target, then the answer.
    /// </summary>
    public static IEnumerable<string> ConsoleLines(DriftReport report)
    {
        foreach (var template in report.Targets.GroupBy(t => t.Template).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var total = template.Count();
            var groups = report.PlanGroups.Where(g => g.Template == template.Key).ToList();
            foreach (var group in groups)
            {
                var drifted = template.First(t => t.Plan == group.Plan && t.Status is "drifted" or "clean").Status == "drifted";
                var what = drifted ? $"drift, plan {group.Plan}" : "no drift";
                yield return groups.Count == 1 && group.Targets == total
                    ? $"  {template.Key}: {what} on all {total} target(s)"
                    : $"  {template.Key}: {what} on {group.Targets} of {total} target(s), e.g. {group.Sample}";
            }
            foreach (var status in new[] { "failed", "unverifiable" })
            {
                var these = template.Where(t => t.Status == status).Select(Display).ToList();
                if (these.Count == 0) continue;
                var word = status == "failed" ? "could not be checked" : "nothing counted, but some modules cannot be compared";
                yield return $"  {template.Key}: {word} on {these.Count} of {total} target(s): {string.Join(", ", these.Take(5))}{(these.Count > 5 ? ", ..." : "")}";
            }
        }
        if (report.PendingMigrations.Count > 0)
            yield return $"  {report.PendingMigrations.Count} run-once migration script(s) not yet run"
                         + (report.FailOn.Contains(DriftFailOn.Migrations) ? "" : " (not counted; --DriftFailOn:migrations counts them)");
        if (report.Unverifiable.Count > 0)
            yield return $"  {report.Unverifiable.Count} module script(s) were never stamped, so whether they changed cannot be said; the summary lists them";
        var driftedTargets = report.Targets.Count(t => t.Status == "drifted");
        yield return driftedTargets > 0
            ? $"Drift found on {driftedTargets} of {report.Targets.Count} target(s)"
            : report.Targets.Any(t => t.Status == "failed")
                ? $"No drift found on the targets that could be checked; {report.Targets.Count(t => t.Status == "failed")} could not be"
                : $"No drift on {report.Targets.Count} target(s)";
    }

    private static string Display(DriftTargetReport t) =>
        string.IsNullOrEmpty(t.Schema) ? $"{t.Server}.{t.Database}" : $"{t.Server}.{t.Database}.{t.Schema}";

    private static DriftItem Item(DriftUnit unit, string type, string name, string action) =>
        new(unit.Server, unit.Database, unit.Schema ?? "", type, name, action);

    /// <summary>
    /// A hash of the counted changes with the iteration schema written as {{SchemaName}}, so tenants of one template that
    /// need the same change share a plan.
    /// </summary>
    internal static string Fingerprint(IEnumerable<(string Type, string Name, string Class)> rows, string schema)
    {
        var lines = rows.Select(r => $"{DriftFailOn.Normalize(r.Type)}|{WithoutSchema(r.Name, schema)}|{r.Class}")
            .OrderBy(l => l, StringComparer.Ordinal);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines)));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    private static string WithoutSchema(string name, string schema) =>
        string.IsNullOrEmpty(schema) || name == null
            ? name
            : string.Join(".", name.Split('.').Select(part => string.Equals(part, schema, StringComparison.OrdinalIgnoreCase) ? "{{SchemaName}}" : part));
}
