// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.IO;
using System.Linq;
using NJsonSchema;
using NUnit.Framework;
using SchemaQuench.Reporting;

namespace SchemaQuench.IntegrationTests;

/// <summary>A summary a real run wrote validates against the published schema for its version.</summary>
internal static class SummaryConformance
{
    private static readonly Lazy<JsonSchema> Schema =
        new(() => JsonSchema.FromJsonAsync(DeploymentSummarySchema.Render()).GetAwaiter().GetResult());

    internal static void AssertConforms(string summaryPath)
    {
        Assert.That(File.Exists(summaryPath), Is.True, $"no deployment summary at {summaryPath}");
        var errors = Schema.Value.Validate(File.ReadAllText(summaryPath)).Select(e => $"{e.Path}: {e.Kind}").ToList();
        Assert.That(errors, Is.Empty, $"{summaryPath} does not match {DeploymentSummarySchema.FileName}");
    }
}
