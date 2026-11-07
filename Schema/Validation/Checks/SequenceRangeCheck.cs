// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.Numerics;

namespace Schema.Validation.Checks;

/// <summary>
/// Reports a declared sequence every engine refuses when the deploy reaches it (<c>SS-SEQ-003</c>, Error): an increment
/// of zero, a minimum not below the maximum, a start outside the declared range, or a negative cache. Caught here, the
/// package is fixed before a deploy stops halfway.
/// </summary>
public sealed class SequenceRangeCheck : ISchemaCheck
{
    public const string Code = "SS-SEQ-003";
    private const string Category = "Sequence";

    public IEnumerable<Finding> Run(ValidationContext ctx)
    {
        foreach (var template in ctx.Templates)
        {
            foreach (var s in template.Sequences)
                foreach (var f in Check(template.Name, s.Schema, s.Name, s.Start, s.Increment, s.MinValue, s.MaxValue, s.Cache)) yield return f;
            foreach (var s in template.MariaDbSequences)
                foreach (var f in Check(template.Name, null, s.Name, s.Start, s.Increment, s.MinValue, s.MaxValue, s.Cache)) yield return f;
            foreach (var s in template.SqlServerSequences)
                foreach (var f in Check(template.Name, s.Schema, s.Name, s.Start, s.Increment, s.MinValue, s.MaxValue, s.Cache)) yield return f;
        }
    }

    private static IEnumerable<Finding> Check(string template, string schema, string name, BigInteger? start, BigInteger increment,
                                              BigInteger? min, BigInteger? max, long? cache)
    {
        var location = $"Template '{template}' / Sequence '{(string.IsNullOrEmpty(schema) ? name : schema + "." + name)}'";
        if (increment == 0)
            yield return Error(location, "Increment is 0. A sequence has to step; use a positive increment to count up or a negative one to count down.");
        if (min != null && max != null && min >= max)
            yield return Error(location, $"MinValue ({min}) is not below MaxValue ({max}).");
        else if (start != null && ((min != null && start < min) || (max != null && start > max)))
            yield return Error(location, $"Start ({start}) is outside the declared range ({min?.ToString() ?? "the type's minimum"} to {max?.ToString() ?? "the type's maximum"}).");
        if (cache < 0)
            yield return Error(location, $"Cache is {cache}; it cannot be negative.");
    }

    private static Finding Error(string location, string message) => new(Severity.Error, Code, Category, location, message + " Every engine refuses this sequence when the deploy reaches it.");
}
