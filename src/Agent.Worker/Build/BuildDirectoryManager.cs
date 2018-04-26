using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Pipelines = Microsoft.TeamFoundation.DistributedTask.Pipelines;
using Microsoft.VisualStudio.Services.Agent.Util;
using System;
using System.IO;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.VisualStudio.Services.Agent.Worker.Maintenance;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Security.Cryptography;

namespace Microsoft.VisualStudio.Services.Agent.Worker.Build
{
    [ServiceLocator(Default = typeof(BuildDirectoryManager))]
    public interface IBuildDirectoryManager : IDirectoryManager
    {
    }

    public sealed class BuildDirectoryManager : DirectoryManager
    {
        public override TrackingConfig ConvertLegacyTrackingConfig(IExecutionContext executionContext)
        {
            // Convert build single repository tracking file into the system tracking file that support tracking multiple resources.
            // Step 1. convert 1.x agent tracking file to 2.x agent version
            // Step 2. convert single repo tracking file to multi-resources tracking file.
            string legacyTrackingFile = Path.Combine(
                IOUtil.GetWorkPath(HostContext),
                Constants.Build.Path.SourceRootMappingDirectory,
                executionContext.Variables.System_CollectionId,
                executionContext.Variables.System_DefinitionId,
                Constants.Build.Path.TrackingConfigFile);
            Trace.Verbose($"Loading tracking config if exists: {legacyTrackingFile}");

            var legacyTrackingManager = HostContext.GetService<ILegacyTrackingManager>();
            LegacyTrackingConfigBase existingLegacyConfig = legacyTrackingManager.LoadIfExists(executionContext, legacyTrackingFile);
            if (existingLegacyConfig != null)
            {
                // Convert legacy format to the new format if required. (1.x agent -> 2.x agent)
                LegacyTrackingConfig2 legacyConfig = ConvertToNewFormat(executionContext, existingConfig);

                // Convert to new format that support multi-repositories
                var trackingConfig = new TrackingConfig();

                // Set basic properties
                trackingConfig.System = legacyConfig.System;
                trackingConfig.CollectionId = legacyConfig.CollectionId;
                trackingConfig.CollectionUrl = executionContext.Variables.System_TFCollectionUrl;
                trackingConfig.DefinitionId = executionContext.Variables.System_DefinitionId;
                switch (executionContext.Variables.System_HostType)
                {
                    case HostTypes.Build:
                        trackingConfig.DefinitionName = executionContext.Variables.Build_DefinitionName;
                        break;
                    case HostTypes.Release | HostTypes.Deployment:
                        trackingConfig.DefinitionName = executionContext.Variables.Get(Constants.Variables.Release.ReleaseDefinitionName);
                        break;
                    default:
                        break;
                }
                trackingConfig.LastRunOn = DateTimeOffset.Now;

                // populate the self repo into the tracking file.
                trackingConfig.JobDirectory = legacyConfig.BuildDirectory;
                trackingConfig.Resources = new ResourceTrackingConfig();
                trackingConfig.Resources.RepositoriesDirectory = legacyConfig.SourcesDirectory;
                trackingConfig.Resources.Repositories["self"] = new RepositoryTrackingConfig()
                {
                    RepositoryType = legacyConfig.RepositoryType,
                    RepositoryUrl = legacyConfig.RepositoryUrl,
                    SourceDirectory = legacyConfig.SourcesDirectory,
                    LastMaintenanceAttemptedOn = legacyConfig.LastMaintenanceAttemptedOn,
                    LastMaintenanceCompletedOn = legacyConfig.LastMaintenanceCompletedOn
                };

                IOUtil.DeleteFile(legacyTrackingFile);
                return trackingConfig;
            }
            else
            {
                return null;
            }
        }

        public override void PrepareDirectory(IExecutionContext executionContext, TrackingConfig trackingConfig)
        {
            // Validate parameters.
            Trace.Entering();
            ArgUtil.NotNull(executionContext, nameof(executionContext));
            ArgUtil.NotNull(trackingConfig, nameof(trackingConfig));

            // Prepare additional build directory.
            WorkspaceCleanOption? workspaceCleanOption = EnumUtil.TryParse<WorkspaceCleanOption>(executionContext.Variables.Get("system.workspace.cleanoption"));
            string binaryDir = Path.Combine(IOUtil.GetWorkPath(HostContext), Path.Combine(trackingConfig.JobDirectory, Constants.Build.Path.BinariesDirectory));
            string artifactDir = Path.Combine(IOUtil.GetWorkPath(HostContext), Path.Combine(trackingConfig.JobDirectory, Constants.Build.Path.ArtifactsDirectory));
            string testResultDir = Path.Combine(IOUtil.GetWorkPath(HostContext), Path.Combine(trackingConfig.JobDirectory, Constants.Build.Path.TestResultsDirectory));
            CreateDirectory(
                executionContext,
                description: "binaries directory",
                path: binaryDir,
                deleteExisting: workspaceCleanOption == WorkspaceCleanOption.Binary);
            CreateDirectory(
                executionContext,
                description: "artifacts directory",
                path: artifactDir,
                deleteExisting: true);
            CreateDirectory(
                executionContext,
                description: "test results directory",
                path: testResultDir,
                deleteExisting: true);

            executionContext.Variables.Set(Constants.Variables.System.ArtifactsDirectory, artifactDir);
            executionContext.Variables.Set(Constants.Variables.Build.StagingDirectory, artifactDir);
            executionContext.Variables.Set(Constants.Variables.Build.ArtifactStagingDirectory, artifactDir);
            executionContext.Variables.Set(Constants.Variables.Common.TestResultsDirectory, testResultDir);
            executionContext.Variables.Set(Constants.Variables.Build.BinariesDirectory, binaryDir);
        }

        private LegacyTrackingConfig2 ConvertToNewFormat(
            IExecutionContext executionContext,
            LegacyTrackingConfigBase config)
        {
            Trace.Entering();

            // If it's already in the new format, return it.
            LegacyTrackingConfig2 newConfig = config as LegacyTrackingConfig2;
            if (newConfig != null)
            {
                return newConfig;
            }

            // Delete the legacy artifact/staging directories.
            LegacyTrackingConfig legacyConfig = config as LegacyTrackingConfig;
            DeleteDirectory(
                executionContext,
                description: "legacy artifacts directory",
                path: Path.Combine(legacyConfig.BuildDirectory, Constants.Build.Path.LegacyArtifactsDirectory));
            DeleteDirectory(
                executionContext,
                description: "legacy staging directory",
                path: Path.Combine(legacyConfig.BuildDirectory, Constants.Build.Path.LegacyStagingDirectory));

            var selfRepoName = executionContext.Repositories.Single(x => x.Alias == "self").Properties.Get<string>("name");
            // Determine the source directory name. Check if the directory is named "s" already.
            // Convert the source directory to be named "s" if there is a problem with the old name.
            string sourcesDirectoryNameOnly = Constants.Build.Path.SourcesDirectory;
            if (!Directory.Exists(Path.Combine(legacyConfig.BuildDirectory, sourcesDirectoryNameOnly))
                && !String.Equals(selfRepoName, Constants.Build.Path.ArtifactsDirectory, StringComparison.OrdinalIgnoreCase)
                && !String.Equals(selfRepoName, Constants.Build.Path.LegacyArtifactsDirectory, StringComparison.OrdinalIgnoreCase)
                && !String.Equals(selfRepoName, Constants.Build.Path.LegacyStagingDirectory, StringComparison.OrdinalIgnoreCase)
                && !String.Equals(selfRepoName, Constants.Build.Path.TestResultsDirectory, StringComparison.OrdinalIgnoreCase)
                && !selfRepoName.Contains("\\")
                && !selfRepoName.Contains("/")
                && Directory.Exists(Path.Combine(legacyConfig.BuildDirectory, selfRepoName)))
            {
                sourcesDirectoryNameOnly = selfRepoName;
            }

            // Convert to the new format.
            newConfig = new LegacyTrackingConfig2(
                executionContext,
                legacyConfig,
                sourcesDirectoryNameOnly,
                // The legacy artifacts directory has been deleted at this point - see above - so
                // switch the configuration to using the new naming scheme.
                useNewArtifactsDirectoryName: true);
            return newConfig;
        }

        private TrackingConfig ConvertToNewFormat(
            IExecutionContext executionContext,
            ServiceEndpoint endpoint,
            TrackingConfigBase config)
        {
            Trace.Entering();

            // If it's already in the new format, return it.
            TrackingConfig newConfig = config as TrackingConfig;
            if (newConfig != null)
            {
                return newConfig;
            }

            // Delete the legacy artifact/staging directories.
            LegacyTrackingConfig legacyConfig = config as LegacyTrackingConfig;
            DeleteDirectory(
                executionContext,
                description: "legacy artifacts directory",
                path: Path.Combine(legacyConfig.BuildDirectory, Constants.Build.Path.LegacyArtifactsDirectory));
            DeleteDirectory(
                executionContext,
                description: "legacy staging directory",
                path: Path.Combine(legacyConfig.BuildDirectory, Constants.Build.Path.LegacyStagingDirectory));

            // Determine the source directory name. Check if the directory is named "s" already.
            // Convert the source directory to be named "s" if there is a problem with the old name.
            string sourcesDirectoryNameOnly = Constants.Build.Path.SourcesDirectory;
            if (!Directory.Exists(Path.Combine(legacyConfig.BuildDirectory, sourcesDirectoryNameOnly))
                && !String.Equals(endpoint.Name, Constants.Build.Path.ArtifactsDirectory, StringComparison.OrdinalIgnoreCase)
                && !String.Equals(endpoint.Name, Constants.Build.Path.LegacyArtifactsDirectory, StringComparison.OrdinalIgnoreCase)
                && !String.Equals(endpoint.Name, Constants.Build.Path.LegacyStagingDirectory, StringComparison.OrdinalIgnoreCase)
                && !String.Equals(endpoint.Name, Constants.Build.Path.TestResultsDirectory, StringComparison.OrdinalIgnoreCase)
                && !endpoint.Name.Contains("\\")
                && !endpoint.Name.Contains("/")
                && Directory.Exists(Path.Combine(legacyConfig.BuildDirectory, endpoint.Name)))
            {
                sourcesDirectoryNameOnly = endpoint.Name;
            }

            // Convert to the new format.
            newConfig = new TrackingConfig(
                executionContext,
                legacyConfig,
                sourcesDirectoryNameOnly,
                endpoint.Type,
                // The legacy artifacts directory has been deleted at this point - see above - so
                // switch the configuration to using the new naming scheme.
                useNewArtifactsDirectoryName: true);
            return newConfig;
        }

        public void CreateDirectory(IExecutionContext executionContext, string description, string path, bool deleteExisting)
        {
            // Delete.
            if (deleteExisting)
            {
                executionContext.Debug($"Delete existing {description}: '{path}'");
                DeleteDirectory(executionContext, description, path);
            }

            // Create.
            if (!Directory.Exists(path))
            {
                executionContext.Debug($"Creating {description}: '{path}'");
                Trace.Info($"Creating {description}.");
                Directory.CreateDirectory(path);
            }
        }

        private void DeleteDirectory(IExecutionContext executionContext, string description, string path)
        {
            Trace.Info($"Checking if {description} exists: '{path}'");
            if (Directory.Exists(path))
            {
                executionContext.Debug($"Deleting {description}: '{path}'");
                IOUtil.DeleteDirectory(path, executionContext.CancellationToken);
            }
        }

        // Prefer variable over endpoint data when get build directory clean option.
        // Prefer agent.clean.builddirectory over build.clean when use variable
        // available value for build.clean or agent.clean.builddirectory:
        //      Delete entire build directory if build.clean=all is set.
        //      Recreate binaries dir if clean=binary is set.
        //      Recreate source dir if clean=src is set.
        private BuildCleanOption GetBuildDirectoryCleanOption(IExecutionContext executionContext, Pipelines.RepositoryResource repository)
        {
            bool clean = StringUtil.ConvertToBoolean(repository.Properties.Get<string>(EndpointData.Clean));
            if (clean)
            {
                string cleanOptionData = repository.Properties.Get<string>(EndpointData.CleanOptions);
                RepositoryCleanOptions? cleanOptionFromEndpoint = EnumUtil.TryParse<RepositoryCleanOptions>(cleanOptionData);
                if (cleanOptionFromEndpoint != null)
                {
                    if (cleanOptionFromEndpoint == RepositoryCleanOptions.SourceDir)
                    {
                        return BuildCleanOption.Source;
                    }
                }
            }

            return BuildCleanOption.None;
        }

        // Prefer variable over endpoint data when get build directory clean option.
        // Prefer agent.clean.builddirectory over build.clean when use variable
        // available value for build.clean or agent.clean.builddirectory:
        //      Delete entire build directory if build.clean=all is set.
        //      Recreate binaries dir if clean=binary is set.
        //      Recreate source dir if clean=src is set.
        private BuildCleanOption GetBuildDirectoryCleanOption(IExecutionContext executionContext, ServiceEndpoint endpoint)
        {
            BuildCleanOption? cleanOption = executionContext.Variables.Build_Clean;
            if (cleanOption != null)
            {
                return cleanOption.Value;
            }

            bool clean = false;
            if (endpoint.Data.ContainsKey(EndpointData.Clean))
            {
                clean = StringUtil.ConvertToBoolean(endpoint.Data[EndpointData.Clean]);
            }

            if (clean && endpoint.Data.ContainsKey("cleanOptions"))
            {
                RepositoryCleanOptions? cleanOptionFromEndpoint = EnumUtil.TryParse<RepositoryCleanOptions>(endpoint.Data["cleanOptions"]);
                if (cleanOptionFromEndpoint != null)
                {
                    if (cleanOptionFromEndpoint == RepositoryCleanOptions.AllBuildDir)
                    {
                        return BuildCleanOption.All;
                    }
                    else if (cleanOptionFromEndpoint == RepositoryCleanOptions.SourceDir)
                    {
                        return BuildCleanOption.Source;
                    }
                    else if (cleanOptionFromEndpoint == RepositoryCleanOptions.SourceAndOutput)
                    {
                        return BuildCleanOption.Binary;
                    }
                }
            }

            return BuildCleanOption.None;
        }
    }


    // TODO: use enum defined in build2.webapi when it's available.
    public enum RepositoryCleanOptions
    {
        Source,
        SourceAndOutput,
        SourceDir,
        AllBuildDir,
    }
}