// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Schema.Domain.Settings;

namespace Schema.Settings;

// Configured is the value compared; Running is the value in effect when a change waits for a restart. Exists is false for a
// name the server does not have, and UnfixableReason then says why (or why the setting cannot be changed here).
public sealed record SettingReading(bool Exists, string Configured, string Running, bool? PendingRestart, string UnfixableReason)
{
    public static SettingReading Missing(string reason) => new(false, null, null, null, reason);
}

/// <summary>One catalog a setting lives in, on one engine: how to read a setting, compare it, and change it.</summary>
public interface ISettingDescriptor
{
    string Kind { get; }
    SettingScope Scope { get; }

    // False where the change is in effect now but lost at the next restart (SET GLOBAL, DBCC TRACEON).
    bool PersistsAcrossRestart { get; }

    SettingReading Read(IDbCommand cmd, string name, string database);
    bool Equivalent(string declared, SettingReading current);

    // Throws a DbException when the engine refuses the change, or SettingValueException for a value it would never take.
    void Apply(IDbCommand cmd, string name, string value, string database);

    // What to do so a change that a restart loses is kept; null when there is nothing to add.
    string RevertAdvice(string name) => null;
}

public sealed class SettingValueException(string message) : Exception(message);

// The server cannot take the change at all from here (a read-only variable without SET PERSIST_ONLY).
public sealed class SettingUnfixableException(string message) : Exception(message);
