// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Newtonsoft.Json;
using NUnit.Framework;
using Schema.Domain;
using Schema.Domain.PostgreSQL;
using Schema.Utility;

namespace Schema.UnitTests.Domain;

/// <summary>
/// The OUTCOME behind <see cref="DefaultValueAttributeParityTests"/>: a hand-authored package that omits an
/// optional key must still omit it after any tool loads and saves the file. The parity test asserts the
/// attribute is present; these assert the thing a user actually notices — that load→save adds nothing.
/// <para>Both shapes below were the reported repros. Without <c>[DefaultValue]</c> the initialised value has
/// no declared default for <c>DefaultValueHandling.Ignore</c> to compare against, so it is always written and
/// the key appears from nothing on first save.</para>
/// </summary>
[TestFixture]
public class PolicyLoadSaveIdempotencyTests
{
    [Test]
    public void RebuildPolicy_OmittingMode_SurvivesALoadSaveRoundTrip()
    {
        const string authored = @"{""OnOrderMismatch"":true}";

        var reloaded = JsonConvert.DeserializeObject<RebuildPolicy>(authored);
        var resaved = JsonHelper.Serialize(reloaded);

        Assert.That(resaved, Does.Not.Contain("Mode"),
            "a package that omitted Mode must not gain \"Mode\":\"NEVER\" just by being loaded and saved — "
            + "RebuildPolicy is whole-object precedence, so a materialised Mode is not merely cosmetic noise "
            + "in a file that a human also reads and diffs.");
    }

    [Test]
    public void PostgreSqlPolicy_OmittingTheThreeDefaults_SurvivesALoadSaveRoundTrip()
    {
        const string authored = @"{""Name"":""p1"",""UsingExpression"":""true""}";

        var reloaded = JsonConvert.DeserializeObject<PostgreSqlPolicy>(authored);
        var resaved = JsonHelper.Serialize(reloaded);

        Assert.Multiple(() =>
        {
            Assert.That(resaved, Does.Not.Contain("Permissive"), "Permissive must not materialise");
            Assert.That(resaved, Does.Not.Contain("Command"), "Command must not materialise");
            Assert.That(resaved, Does.Not.Contain("Roles"), "Roles must not materialise");
        });
    }

    /// <summary>
    /// The values still deserialise to the documented defaults — the fix must remove the redundant KEY
    /// without changing what an omitted key MEANS. Deploy behaviour is unchanged; only the file is quieter.
    /// </summary>
    [Test]
    public void TheDefaultsStillApply_WhenTheKeysAreAbsent()
    {
        var rebuild = JsonConvert.DeserializeObject<RebuildPolicy>(@"{""OnOrderMismatch"":true}");
        var policy = JsonConvert.DeserializeObject<PostgreSqlPolicy>(@"{""Name"":""p1""}");

        Assert.Multiple(() =>
        {
            Assert.That(rebuild!.Mode, Is.EqualTo("NEVER"));
            Assert.That(policy!.Permissive, Is.EqualTo("PERMISSIVE"));
            Assert.That(policy.Command, Is.EqualTo("ALL"));
            Assert.That(policy.Roles, Is.EqualTo("PUBLIC"));
        });
    }
}
