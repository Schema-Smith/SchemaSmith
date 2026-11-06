// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Schema.Configuration;
using Schema.Utility;

namespace SchemaQuench;

/// <summary>
/// What a deploy does about a change that removes data -- a table or a column dropped because the package no longer declares
/// it (H3). <c>allow</c> makes it and says so in the log, <c>warn</c> (the default) also warns and lists it in the summary,
/// <c>fail</c> refuses the step before anything in it is dropped, unless the change is named in <see cref="Accepted"/>.
/// Environment scope only: a package travels to every environment, and an acceptance in the package would carry itself into
/// production.
/// </summary>
public sealed record DestructivePolicy(string Policy, IReadOnlyList<string> Accepted)
{
    public const string Allow = "allow";
    public const string Warn = "warn";
    public const string Fail = "fail";

    public static DestructivePolicy Default { get; } = new(Warn, []);

    public static DestructivePolicy Read(IConfigurationRoot config)
    {
        var raw = config?[SettingsKeys.DestructiveChangePolicy];
        var policy = string.IsNullOrWhiteSpace(raw) ? Warn : raw.Trim().ToLowerInvariant();
        // A typo must not quietly disarm a safety setting, so an unknown value stops the run.
        if (policy is not (Allow or Warn or Fail))
            throw new RunFailedException($"{SettingsKeys.DestructiveChangePolicy} is '{raw}'; it must be allow, warn or fail.");
        return new DestructivePolicy(policy, ReadAccepted(config));
    }

    // A string of names separated by ';' or ',', or a JSON array in the settings file.
    private static IReadOnlyList<string> ReadAccepted(IConfigurationRoot config)
    {
        if (config == null) return [];
        var section = config.GetSection(SettingsKeys.AcceptDestructive);
        var values = section.GetChildren().Select(c => c.Value).ToList();
        if (values.Count == 0 && !string.IsNullOrWhiteSpace(section.Value)) values.Add(section.Value);
        return values.SelectMany(v => (v ?? "").Split([';', ','], StringSplitOptions.RemoveEmptyEntries))
            .Select(Normalize)
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // schema.table or schema.table.column, compared without delimiters and without case.
    internal static string Normalize(string name) =>
        string.Join(".", name.Trim().Split('.').Select(part => part.Trim().Trim('[', ']', '"', '`').Trim())).ToLowerInvariant();

    /// <summary>The accepted names as the procedures match them: <c>;name;name;</c>, lower case, no delimiters.</summary>
    public string AcceptedForSql => Accepted.Count == 0 ? "" : ";" + string.Join(";", Accepted) + ";";
}
