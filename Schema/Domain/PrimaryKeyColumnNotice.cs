// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

namespace Schema.Domain;

/// <summary>
/// A primary-key column declared Nullable that load made NOT NULL, since a key column cannot hold NULL. Recorded so
/// the deploy can warn before it changes the column and <c>--Validate</c> can report the contradiction.
/// </summary>
public sealed record PrimaryKeyColumnNotice(string Location, string Message);
