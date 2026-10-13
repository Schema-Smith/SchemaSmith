// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;

namespace Schema.Domain;

/// <summary>
/// The package, as written, cannot be loaded: something the user fixes in its files. Kept separate from other
/// <see cref="InvalidOperationException"/>s so a fault in the tool is never reported as a package to fix.
/// </summary>
public class PackageException : InvalidOperationException
{
    public PackageException(string message) : base(message) { }

    public PackageException(string message, Exception inner) : base(message, inner) { }
}
