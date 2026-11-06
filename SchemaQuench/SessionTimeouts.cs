// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Data;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Schema.Configuration;
using Schema.Domain;
using Schema.Utility;

namespace SchemaQuench;

/// <summary>
/// How long a deploy's commands may run and how long it waits for another session's lock. Every command SchemaQuench
/// creates takes <see cref="CommandTimeoutSeconds"/>, and every connection it opens sets the lock timeout as a session
/// setting, so it covers the procedures too. Unset, both are what they always were: no command limit, and the engine's
/// own lock wait.
/// </summary>
public sealed record SessionTimeouts(int CommandTimeoutSeconds, int? LockTimeoutSeconds, int ContentionRetryAttempts)
{
    public const int DefaultContentionRetryAttempts = 20;

    public static SessionTimeouts Default { get; } = new(0, null, DefaultContentionRetryAttempts);

    public static SessionTimeouts Read(IConfigurationRoot config) => new(
        WholeNumber(config, SettingsKeys.CommandTimeoutSeconds, min: 0, max: int.MaxValue) ?? 0,
        WholeNumber(config, SettingsKeys.LockTimeoutSeconds, min: 1, max: 86400),
        WholeNumber(config, SettingsKeys.ContentionRetryAttempts, min: 1, max: 100) ?? DefaultContentionRetryAttempts);

    private static int? WholeNumber(IConfigurationRoot config, string key, int min, int max)
    {
        var raw = config?[key];
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!int.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
            throw new RunFailedException($"{key} is '{raw}'; it must be a whole number from {min} to {(max == int.MaxValue ? "any" : max.ToString(CultureInfo.InvariantCulture))}.");
        return value;
    }

    // MySQL and MariaDB bound a metadata lock (what DDL waits on) by lock_wait_timeout and a row lock by
    // innodb_lock_wait_timeout; both raise 1205.
    public static string LockTimeoutStatement(Platform platform, int seconds) => platform.GetBasePlatform() switch
    {
        Platform.SqlServer => $"SET LOCK_TIMEOUT {seconds * 1000}",
        Platform.PostgreSQL => $"SET lock_timeout = '{seconds}s'",
        _ => $"SET SESSION lock_wait_timeout = {seconds}, SESSION innodb_lock_wait_timeout = {seconds}",
    };

    public IDbCommand NewCommand(IDbConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandTimeout = CommandTimeoutSeconds;
        return command;
    }

    /// <summary>Sets the lock timeout on an open connection, before anything else runs on it.</summary>
    public void ApplyLockTimeout(IDbConnection connection, Platform platform)
    {
        if (LockTimeoutSeconds is not { } seconds) return;
        using var command = NewCommand(connection);
        command.CommandText = LockTimeoutStatement(platform, seconds);
        command.ExecuteNonQuery();
    }
}
