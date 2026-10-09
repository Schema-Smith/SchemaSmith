// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.IO;
using NUnit.Framework;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

namespace Schema.UnitTests.Domain;

/// <summary>
/// A <c>TemplateOrder</c> entry names a folder under <c>Templates/</c>, never a path, and resolves the same way on every
/// OS. It was pasted into a file path, so the host file system decided: <c>main</c> loaded folder <c>Main</c> on
/// Windows and nothing on Linux, while <c>./Main</c> loaded on Linux and failed on Windows.
/// </summary>
[TestFixture]
public class TemplateOrderResolutionTests
{
    private string _root;
    private Product _product;

    [SetUp]
    public void SetUp()
    {
        FactoryContainer.Clear();
        _root = Path.Join(Path.GetTempPath(), "schemasmith-templateorder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Join(_root, "Templates", "Main"));
        Directory.CreateDirectory(Path.Join(_root, "Templates", "Other"));
        _product = new Product { FilePath = Path.Join(_root, "Product.json") };
    }

    [TearDown]
    public void TearDown()
    {
        FactoryContainer.Clear();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [TestCase("Main", "Main")]
    [TestCase("main", "Main")]
    [TestCase("OTHER", "Other")]
    public void AnEntry_ResolvesToItsFolder_WhateverItsCase(string entry, string folder)
    {
        Assert.That(Template.ResolveTemplateFolder(_product, entry), Is.EqualTo(folder));
    }

    [TestCase("./Main")]
    [TestCase(".\\Main")]
    [TestCase("Main/")]
    [TestCase("Main\\")]
    [TestCase("../Templates/Main")]
    [TestCase("Main//")]
    [TestCase("Main.")]
    [TestCase(".")]
    [TestCase("..")]
    [TestCase(" Main")]
    [TestCase("Main ")]
    [TestCase("Ma:in")]
    [TestCase("Ma*in")]
    [TestCase("Ma?in")]
    [TestCase("Ma|in")]
    [TestCase("Ma\"in")]
    public void AnEntryThatIsNotAPlainFolderName_IsRefused(string entry)
    {
        var ex = Assert.Throws<RunFailedException>(() => Template.ResolveTemplateFolder(_product, entry));

        Assert.That(ex!.Message, Does.Contain($"'{entry}' is not a template folder name"));
    }

    [Test]
    public void AnEntryThatMatchesNoFolder_IsRefused_ListingTheFolders()
    {
        var ex = Assert.Throws<RunFailedException>(() => Template.ResolveTemplateFolder(_product, "Mian"));

        Assert.That(ex!.Message, Does.Contain("matches no folder").And.Contain("Main, Other"));
    }

    // Only a case-sensitive file system can hold both; where it can, the entry is ambiguous unless it is exact.
    [Test]
    public void FoldersDifferingOnlyInCase_AreRefused_UnlessTheEntryMatchesOneExactly()
    {
        Directory.CreateDirectory(Path.Join(_root, "Templates", "main"));
        if (Directory.GetDirectories(Path.Join(_root, "Templates")).Length < 3)
            Assert.Ignore("This file system ignores case, so it cannot hold two folders differing only in case.");

        Assert.Multiple(() =>
        {
            Assert.That(Template.ResolveTemplateFolder(_product, "Main"), Is.EqualTo("Main"));
            Assert.That(() => Template.ResolveTemplateFolder(_product, "MAIN"),
                Throws.TypeOf<RunFailedException>().With.Message.Contains("more than one folder"));
        });
    }
}
