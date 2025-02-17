﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Sbom.Extensions;
using Microsoft.Sbom.Extensions.Entities;
using Microsoft.Sbom.Api.Entities;
using Microsoft.Sbom.Api.Output.Telemetry;
using Microsoft.Sbom.Api.Providers;
using Microsoft.Sbom.Api.Utils;
using Serilog;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System;

namespace Microsoft.Sbom.Api.Workflows.Helpers
{
    /// <summary>
    /// Generates a packages array that contains a list of all the packages that are referenced in this project.
    /// </summary>
    public class PackageArrayGenerator : IJsonArrayGenerator
    {
        private ILogger Log { get; }

        private ISbomConfigProvider SBOMConfigs { get; }

        private IList<ISourcesProvider> SourcesProviders { get; }

        private IRecorder Recorder { get; }

        public PackageArrayGenerator(
            ILogger log,
            ISbomConfigProvider sbomConfigs,
            IList<ISourcesProvider> sourcesProviders,
            IRecorder recorder)
        {
            Log = log ?? throw new ArgumentNullException(nameof(log));
            SBOMConfigs = sbomConfigs ?? throw new ArgumentNullException(nameof(sbomConfigs));
            SourcesProviders = sourcesProviders ?? throw new ArgumentNullException(nameof(sourcesProviders));
            Recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        }

        public async Task<IList<FileValidationResult>> GenerateAsync()
        {
            using (Recorder.TraceEvent(Events.PackagesGeneration))
            {
                IList<FileValidationResult> totalErrors = new List<FileValidationResult>();

                ISourcesProvider sourcesProvider = SourcesProviders
                                                    .Where(s => s.IsSupported(ProviderType.Packages))
                                                    .FirstOrDefault();

                // Write the start of the array, if supported.
                IList<ISbomConfig> packagesArraySupportingConfigs = new List<ISbomConfig>();
                foreach (var manifestInfo in SBOMConfigs.GetManifestInfos())
                {
                    var config = SBOMConfigs.Get(manifestInfo);
                    if (config.MetadataBuilder.TryGetPackageArrayHeaderName(out string packagesArrayHeaderName))
                    {
                        packagesArraySupportingConfigs.Add(config);
                        config.JsonSerializer.StartJsonArray(packagesArrayHeaderName);
                    }
                }

                var (jsonDocResults, errors) = sourcesProvider.Get(packagesArraySupportingConfigs);

                // 6. Collect all the json elements and write to the serializer.
                int totalJsonDocumentsWritten = 0;

                await foreach (JsonDocWithSerializer jsonDocResult in jsonDocResults.ReadAllAsync())
                {
                    jsonDocResult.Serializer.Write(jsonDocResult.Document);
                    totalJsonDocumentsWritten++;
                }

                Log.Debug($"Wrote {totalJsonDocumentsWritten} package elements in the SBOM.");
                await foreach (FileValidationResult error in errors.ReadAllAsync())
                {
                    totalErrors.Add(error);
                }

                foreach (ISbomConfig sbomConfig in packagesArraySupportingConfigs)
                {
                    // Write the root package information to the packages array.
                    if (sbomConfig.MetadataBuilder.TryGetRootPackageJson(SBOMConfigs, out GenerationResult generationResult))
                    {
                        sbomConfig.JsonSerializer.Write(generationResult?.Document);
                        sbomConfig.Recorder.RecordRootPackageId(generationResult?.ResultMetadata?.EntityId);
                        sbomConfig.Recorder.RecordDocumentId(generationResult?.ResultMetadata?.DocumentId);
                    }

                    // Write the end of the array.
                    sbomConfig.JsonSerializer.EndJsonArray();
                }

                return totalErrors;
            }
        }
    }
}
