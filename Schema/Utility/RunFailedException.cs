// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;

namespace Schema.Utility;

/// <summary>
/// A failure the user can act on: a missing setting, a refused target, a validation or script that failed. The tool
/// reports it in one line and exits 2. Anything else that escapes is treated as a defect in the tool and exits 3.
/// </summary>
public class RunFailedException : Exception
{
    public RunFailedException(string message) : base(message) { }

    public RunFailedException(string message, Exception inner) : base(message, inner) { }
}
