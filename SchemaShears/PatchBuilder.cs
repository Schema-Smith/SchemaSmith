// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.IO;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaShears;

public class PatchBuildRequest
{
    public string SourcePath { get; set; }
    public string ManifestPath { get; set; }
    public string AlwaysIncludePath { get; set; }
    public string OutputPath { get; set; }
    public bool Zip { get; set; }
    public string[] AllowDrops { get; set; }
}

public class PatchBuilder
{
    public virtual void Build(PatchBuildRequest request)
    {
        var log = LogFactory.GetLogger("ProgressLog");

        if (string.IsNullOrWhiteSpace(request.SourcePath) || !DirectoryWrapper.GetFromFactory().Exists(request.SourcePath))
            throw new PatchBuildException($"Source product folder not found: '{request.SourcePath}'.");
        if (!FileWrapper.GetFromFactory().Exists(Path.Join(request.SourcePath, "Product.json")))
            throw new PatchBuildException($"Source folder is not a product (no Product.json): '{request.SourcePath}'.");
        if (string.IsNullOrWhiteSpace(request.OutputPath))
            throw new PatchBuildException("Output path is required.");
        DropSuppressionStamp.ValidateCategories(request.AllowDrops ?? Array.Empty<string>());
        if (request.Zip)
            PatchZipper.RequireNoExistingZip(request.OutputPath);

        var manifest = PatchManifest.Read(request.ManifestPath, request.SourcePath);
        var alwaysInclude = AlwaysIncludeList.Expand(request.AlwaysIncludePath, request.SourcePath);
        var includeSet = IncludeSetResolver.Resolve(manifest, alwaysInclude, request.SourcePath);

        log.Info($"SchemaShears: {includeSet.Count} files into '{request.OutputPath}' ({manifest.Count} from manifest).");

        PatchPackageWriter.Write(includeSet, request.SourcePath, request.OutputPath);
        DropSuppressionStamp.Apply(Path.Join(request.OutputPath, "Product.json"), request.AllowDrops ?? Array.Empty<string>());

        if (request.Zip)
        {
            var zip = PatchZipper.Zip(request.OutputPath);
            log.Info($"SchemaShears: wrote '{zip}'.");
        }
    }
}
