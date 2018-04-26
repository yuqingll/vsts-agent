using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Agent.Util;
using Microsoft.VisualStudio.Services.Agent.Worker;
using Microsoft.VisualStudio.Services.Agent.Worker.Build;
using Microsoft.VisualStudio.Services.Agent.Capabilities;
using Microsoft.VisualStudio.Services.WebApi;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.Win32;
using System.Diagnostics;
using System.Linq;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Pipelines = Microsoft.TeamFoundation.DistributedTask.Pipelines;
using Newtonsoft.Json;
using System.ComponentModel;
using Microsoft.VisualStudio.Services.Agent.Worker.Maintenance;

namespace Microsoft.VisualStudio.Services.Agent.Worker
{
    public interface IDirectoryManager : IAgentService
    {
        TrackingConfig PrepareDirectory(IExecutionContext executionContext);

        string GetRootedPath(IExecutionContext executionContext, string inputPath);
    }

    public abstract class DirectoryManager : AgentService, IMaintenanceServiceProvider
    {
        public string MaintenanceDescription => StringUtil.Loc("DeleteUnusedBuildDir");
        public Type ExtensionType => typeof(IMaintenanceServiceProvider);

        public abstract TrackingConfig ConvertLegacyTrackingConfig(IExecutionContext executionContext);
        public abstract void PrepareDirectory(IExecutionContext executionContext, TrackingConfig trackingConfig);

        public TrackingConfig PrepareDirectory(IExecutionContext executionContext)
        {
            // Validate parameters.
            Trace.Entering();
            ArgUtil.NotNull(executionContext, nameof(executionContext));
            ArgUtil.NotNull(executionContext.Variables, nameof(executionContext.Variables));
            ArgUtil.NotNull(executionContext.Repositories, nameof(executionContext.Repositories));

            // Not support multi-repo with TFVC
            if (executionContext.Repositories.Count > 1 && executionContext.Repositories.Any(x => x.Type == RepositoryTypes.TfsVersionControl))
            {
                throw new NotSupportedException(string.Join(" + ", executionContext.Repositories.Select(x => x.Type)));
            }

            // Load the existing tracking file if one already exists.
            var trackingManager = HostContext.GetService<ITrackingManager>();
            string trackingFile = Path.Combine(
                IOUtil.GetWorkPath(HostContext),
                Constants.Agent.Path.JobRootMappingDirectory,
                executionContext.Variables.System_CollectionId,
                executionContext.Variables.System_DefinitionId,
                Constants.Agent.Path.TrackingConfigFile);
            Trace.Verbose($"Loading tracking config if exists: {trackingFile}");
            TrackingConfig trackingConfig = trackingManager.LoadIfExists(executionContext, trackingFile);

            // the tracking config doesn't exists, this may indicate the job folder tracking is done in Build/Release, try convert them to new format
            if (trackingConfig == null)
            {
                trackingConfig = ConvertLegacyTrackingConfig(executionContext);
            }

            // Create a new tracking config if required.
            if (trackingConfig == null)
            {
                Trace.Verbose("Creating a new tracking config file.");
                trackingConfig = trackingManager.Create(executionContext, trackingFile);
            }
            else
            {
                // Update tracking information for existing tracking config file:
                // 1. update the job execution properties. (collection url/definition url)
                // 2. update the job resources. (repositories/drops)
                Trace.Verbose("Updating job execution properties and resources into tracking config file.");
                trackingManager.Update(executionContext, trackingConfig, trackingFile);
            }

            // We should have a tracking config at this point.
            ArgUtil.NotNull(trackingConfig, nameof(trackingConfig));

            // Set variable for every system directory.
            // Create the directory base on workspace clean option.
            // None: not recreate any folder
            // Resource: delete and recreate all repository and drop folder
            // All: delete and recreate the entire job folder
            WorkspaceCleanOption? workspaceCleanOption = EnumUtil.TryParse<WorkspaceCleanOption>(executionContext.Variables.Get("system.workspace.cleanoption"));
            CreateDirectory(
                executionContext,
                description: "job directory",
                path: Path.Combine(IOUtil.GetWorkPath(HostContext), trackingConfig.JobDirectory),
                deleteExisting: workspaceCleanOption == WorkspaceCleanOption.All);
            CreateDirectory(
                executionContext,
                description: "sources directory",
                path: Path.Combine(IOUtil.GetWorkPath(HostContext), trackingConfig.Resources.RepositoriesDirectory),
                deleteExisting: workspaceCleanOption == WorkspaceCleanOption.Resource);
            // CreateDirectory(
            //     executionContext,
            //     description: "drops directory",
            //     path: Path.Combine(IOUtil.GetWorkPath(HostContext), trackingConfig.Resources.DropsDirectory),
            //     deleteExisting: workspaceCleanOption == WorkspaceCleanOption.Resource);

            //executionContext.Variables.Set("system.repositoriesdirectory", Path.Combine(IOUtil.GetWorkPath(HostContext), trackingConfig.Resources.RepositoriesDirectory));
            //executionContext.Variables.Set("system.dropsdirectory", Path.Combine(IOUtil.GetWorkPath(HostContext), trackingConfig.Resources.DropsDirectory));
            //executionContext.Variables.Set("system.jobdirectory", Path.Combine(IOUtil.GetWorkPath(HostContext), trackingConfig.JobDirectory));

            // Create directory for each repository resource
            // Set each repository's local path (calculated by tracking file) back to repository's property
            foreach (var repo in executionContext.Repositories)
            {
                string sourceDirectory = Path.Combine(IOUtil.GetWorkPath(HostContext), trackingConfig.Resources.Repositories[repo.Alias].SourceDirectory);
                CreateDirectory(
                    executionContext,
                    description: $"source directory {repo.Alias}",
                    path: sourceDirectory,
                    deleteExisting: false);
                repo.Properties.Set<string>("sourcedirectory", sourceDirectory);
                // executionContext.Variables.Set($"system.repository.{repo.Alias}.id", repo.Id);
                // executionContext.Variables.Set($"system.repository.{repo.Alias}.name", repo.Properties.Get<string>("name") ?? string.Empty);
                // executionContext.Variables.Set($"system.repository.{repo.Alias}.provider", repo.Type);
                // executionContext.Variables.Set($"system.repository.{repo.Alias}.uri", repo.Url?.AbsoluteUri);
                // executionContext.Variables.Set($"system.repository.{repo.Alias}.clean", repo.Properties.Get<string>("clean") ?? string.Empty);
                // executionContext.Variables.Set($"system.repository.{repo.Alias}.localpath", sourceDirectory);
            }

            // Let Build/Release setup additional directory required for Build/Release
            PrepareDirectory(executionContext, trackingConfig);
            return trackingConfig;
        }

        public string GetRootedPath(IExecutionContext executionContext, string inputPath)
        {
            if (!string.IsNullOrEmpty(inputPath))
            {
                int lastIndex = inputPath.LastIndexOf('@');
                if (lastIndex > 0 &&
                    lastIndex < inputPath.Length - 1)
                {
                    string resourceAlias = inputPath.Substring(lastIndex + 1);
                    if (!string.IsNullOrEmpty(resourceAlias))
                    {
                        Trace.Verbose($"Prepand resource directory for resource: {resourceAlias}");
                        var repo = executionContext.Repositories.SingleOrDefault(x => string.Equals(x.Alias, resourceAlias, StringComparison.OrdinalIgnoreCase));
                        return Path.Combine(repo.Properties.Get<string>("sourcedirectory"), inputPath.Substring(0, lastIndex));
                    }
                }
            }

            return inputPath;
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

        public void DeleteDirectory(IExecutionContext executionContext, string description, string path)
        {
            Trace.Info($"Checking if {description} exists: '{path}'");
            if (Directory.Exists(path))
            {
                executionContext.Debug($"Deleting {description}: '{path}'");
                IOUtil.DeleteDirectory(path, executionContext.CancellationToken);
            }
        }

        public async Task RunMaintenanceOperation(IExecutionContext executionContext)
        {
            Trace.Entering();
            ArgUtil.NotNull(executionContext, nameof(executionContext));

            // this might be not accurate when the agent is configured for old TFS server
            // int totalAvailableTimeInMinutes = executionContext.Variables.GetInt("maintenance.jobtimeoutinminutes") ?? 60;

            // // start a timer to track how much time we used
            // Stopwatch totalTimeSpent = Stopwatch.StartNew();

            // var trackingManager = HostContext.GetService<ITrackingManager>();
            // int staleBuildDirThreshold = executionContext.Variables.GetInt("maintenance.deleteworkingdirectory.daysthreshold") ?? 0;
            // if (staleBuildDirThreshold > 0)
            // {
            //     // scan unused build directories
            //     executionContext.Output(StringUtil.Loc("DiscoverBuildDir", staleBuildDirThreshold));
            //     trackingManager.MarkExpiredForGarbageCollection(executionContext, TimeSpan.FromDays(staleBuildDirThreshold));
            // }
            // else
            // {
            //     executionContext.Output(StringUtil.Loc("GCBuildDirNotEnabled"));
            //     return;
            // }

            // executionContext.Output(StringUtil.Loc("GCBuildDir"));

            // // delete unused build directories
            // trackingManager.DisposeCollectedGarbage(executionContext);

            // // give source provider a chance to run maintenance operation
            // Trace.Info("Scan all SourceFolder tracking files.");
            // string searchRoot = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Work), Constants.Build.Path.SourceRootMappingDirectory);
            // if (!Directory.Exists(searchRoot))
            // {
            //     executionContext.Output(StringUtil.Loc("GCDirNotExist", searchRoot));
            //     return;
            // }

            // <tracking config, tracking file path>
            // List<Tuple<TrackingConfig, string>> optimizeTrackingFiles = new List<Tuple<TrackingConfig, string>>();
            // var allTrackingFiles = Directory.EnumerateFiles(searchRoot, Constants.Build.Path.TrackingConfigFile, SearchOption.AllDirectories);
            // Trace.Verbose($"Find {allTrackingFiles.Count()} tracking files.");
            // foreach (var trackingFile in allTrackingFiles)
            // {
            //     executionContext.Output(StringUtil.Loc("EvaluateTrackingFile", trackingFile));
            //     TrackingConfig tracking = trackingManager.LoadIfExists(executionContext, trackingFile);

            //     // detect whether the tracking file is in new format.
            //     TrackingConfig newTracking = tracking as TrackingConfig;
            //     if (newTracking == null)
            //     {
            //         executionContext.Output(StringUtil.Loc("GCOldFormatTrackingFile", trackingFile));
            //     }
            //     else if (string.IsNullOrEmpty(newTracking.RepositoryType))
            //     {
            //         // repository not been set.
            //         executionContext.Output(StringUtil.Loc("SkipTrackingFileWithoutRepoType", trackingFile));
            //     }
            //     else
            //     {
            //         optimizeTrackingFiles.Add(new Tuple<TrackingConfig, string>(newTracking, trackingFile));
            //     }
            // }

            // // Sort the all tracking file ASC by last maintenance attempted time
            // foreach (var trackingInfo in optimizeTrackingFiles.OrderBy(x => x.Item1.LastMaintenanceAttemptedOn))
            // {
            //     // maintenance has been cancelled.
            //     executionContext.CancellationToken.ThrowIfCancellationRequested();

            //     bool runMainenance = false;
            //     TrackingConfig trackingConfig = trackingInfo.Item1;
            //     string trackingFile = trackingInfo.Item2;
            //     if (trackingConfig.LastMaintenanceAttemptedOn == null)
            //     {
            //         // this folder never run maintenance before, we will do maintenance if there is more than half of the time remains.
            //         if (totalTimeSpent.Elapsed.TotalMinutes < totalAvailableTimeInMinutes / 2)  // 50% time left
            //         {
            //             runMainenance = true;
            //         }
            //         else
            //         {
            //             executionContext.Output($"Working directory '{trackingConfig.BuildDirectory}' has never run maintenance before. Skip since we may not have enough time.");
            //         }
            //     }
            //     else if (trackingConfig.LastMaintenanceCompletedOn == null)
            //     {
            //         // this folder did finish maintenance last time, this might indicate we need more time for this working directory
            //         if (totalTimeSpent.Elapsed.TotalMinutes < totalAvailableTimeInMinutes / 4)  // 75% time left
            //         {
            //             runMainenance = true;
            //         }
            //         else
            //         {
            //             executionContext.Output($"Working directory '{trackingConfig.BuildDirectory}' didn't finish maintenance last time. Skip since we may not have enough time.");
            //         }
            //     }
            //     else
            //     {
            //         // estimate time for running maintenance
            //         TimeSpan estimateTime = trackingConfig.LastMaintenanceCompletedOn.Value - trackingConfig.LastMaintenanceAttemptedOn.Value;

            //         // there is more than 10 mins left after we run maintenance on this repository directory
            //         if (totalAvailableTimeInMinutes > totalTimeSpent.Elapsed.TotalMinutes + estimateTime.TotalMinutes + 10)
            //         {
            //             runMainenance = true;
            //         }
            //         else
            //         {
            //             executionContext.Output($"Working directory '{trackingConfig.BuildDirectory}' may take about '{estimateTime.TotalMinutes}' mins to finish maintenance. It's too risky since we only have '{totalAvailableTimeInMinutes - totalTimeSpent.Elapsed.TotalMinutes}' mins left for maintenance.");
            //         }
            //     }

            //     if (runMainenance)
            //     {
            //         var extensionManager = HostContext.GetService<IExtensionManager>();
            //         ISourceProvider sourceProvider = extensionManager.GetExtensions<ISourceProvider>().FirstOrDefault(x => string.Equals(x.RepositoryType, trackingConfig.RepositoryType, StringComparison.OrdinalIgnoreCase));
            //         if (sourceProvider != null)
            //         {
            //             try
            //             {
            //                 trackingManager.MaintenanceStarted(trackingConfig, trackingFile);
            //                 string repositoryPath = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Work), trackingConfig.SourcesDirectory);
            //                 await sourceProvider.RunMaintenanceOperations(executionContext, repositoryPath);
            //                 trackingManager.MaintenanceCompleted(trackingConfig, trackingFile);
            //             }
            //             catch (Exception ex)
            //             {
            //                 executionContext.Error(StringUtil.Loc("ErrorDuringBuildGC", trackingFile));
            //                 executionContext.Error(ex);
            //             }
            //         }
            //     }
            // }
        }
    }

    [ServiceLocator(Default = typeof(TrackingManager))]
    public interface ITrackingManager : IAgentService
    {
        TrackingConfig Create(IExecutionContext executionContext, string file);

        TrackingConfig LoadIfExists(IExecutionContext executionContext, string file);

        //void MarkForGarbageCollection(IExecutionContext executionContext, TrackingConfigBase config);

        void Update(IExecutionContext executionContext, TrackingConfig config, string file);

        // void MarkExpiredForGarbageCollection(IExecutionContext executionContext, TimeSpan expiration);

        // void DisposeCollectedGarbage(IExecutionContext executionContext);

        // void MaintenanceStarted(TrackingConfig config, string file);

        // void MaintenanceCompleted(TrackingConfig config, string file);
    }

    public sealed class TrackingManager : AgentService, ITrackingManager
    {
        public TrackingConfig Create(IExecutionContext executionContext, string file)
        {
            Trace.Entering();

            // Get or create the top-level tracking config.
            TopLevelTrackingConfig topLevelConfig;
            string topLevelFile = Path.Combine(
                IOUtil.GetWorkPath(HostContext),
                Constants.Agent.Path.JobRootMappingDirectory,
                Constants.Agent.Path.TopLevelTrackingConfigFile);
            Trace.Verbose($"Loading top-level tracking config if exists: {topLevelFile}");
            if (!File.Exists(topLevelFile))
            {
                topLevelConfig = new TopLevelTrackingConfig();

                // Build/Release were tracking this property themselves in a different file
                // Try to populate the right directory number base on the current folder structure.
                bool populatedDirectoryNumber = false;
                DirectoryInfo workDir = new DirectoryInfo(HostContext.GetDirectory(WellKnownDirectory.Work));
                foreach (var dir in workDir.EnumerateDirectories())
                {
                    // we scan the entire _work directory and find the directory with the highest integer number.
                    if (int.TryParse(dir.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int lastBuildNumber) &&
                        lastBuildNumber > topLevelConfig.LastJobDirectoryNumber)
                    {
                        populatedDirectoryNumber = true;
                        topLevelConfig.LastJobDirectoryNumber = lastBuildNumber;
                    }
                }

                if (populatedDirectoryNumber)
                {
                    Trace.Info("Populate last job directory number '{topLevelConfig.LastJobDirectoryNumber}' base on existing job directories.");
                }
            }
            else
            {
                topLevelConfig = JsonConvert.DeserializeObject<TopLevelTrackingConfig>(File.ReadAllText(topLevelFile));
                if (topLevelConfig == null)
                {
                    executionContext.Warning($"Rebuild corruptted top-level tracking configure file {topLevelFile}.");
                    // save the corruptted file in case we need to investigate more.
                    File.Copy(topLevelFile, $"{topLevelFile}.corruptted", true);

                    topLevelConfig = new TopLevelTrackingConfig();
                    DirectoryInfo workDir = new DirectoryInfo(HostContext.GetDirectory(WellKnownDirectory.Work));
                    foreach (var dir in workDir.EnumerateDirectories())
                    {
                        // we scan the entire _work directory and find the directory with the highest integer number.
                        if (int.TryParse(dir.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int lastBuildNumber) &&
                            lastBuildNumber > topLevelConfig.LastJobDirectoryNumber)
                        {
                            topLevelConfig.LastJobDirectoryNumber = lastBuildNumber;
                        }
                    }
                }
            }

            // Determine the build directory.
            var configurationStore = HostContext.GetService<IConfigurationStore>();
            AgentSettings settings = configurationStore.GetSettings();
            if (settings.IsHosted && (executionContext.Repositories.Any(x => x.Type == "TfsVersionControl")))
            {
                // This should only occur during hosted builds. This was added due to TFVC.
                // TFVC does not allow a local path for a single machine to be mapped in multiple
                // workspaces. The machine name for a hosted images is not unique.
                //
                // So if a customer is running two hosted builds at the same time, they could run
                // into the local mapping conflict.
                //
                // The workaround is to force the build directory to be different across all concurrent
                // hosted builds (for TFVC). The agent ID will be unique across all concurrent hosted
                // builds so that can safely be used as the build directory.
                ArgUtil.Equal(default(int), topLevelConfig.LastJobDirectoryNumber, nameof(topLevelConfig.LastJobDirectoryNumber));
                topLevelConfig.LastJobDirectoryNumber = settings.AgentId;
            }
            else
            {
                topLevelConfig.LastJobDirectoryNumber++;
            }

            // Update the top-level tracking config.
            topLevelConfig.LastJobDirectoryCreatedOn = DateTimeOffset.Now;
            WriteToFile(topLevelFile, topLevelConfig);

            // Create the new tracking config.
            TrackingConfig config = new TrackingConfig(executionContext, topLevelConfig.LastJobDirectoryNumber);
            WriteToFile(file, config);
            return config;
        }

        public void Update(
            IExecutionContext executionContext,
            TrackingConfig config,
            string file)
        {
            config.CollectionUrl = executionContext.Variables.System_TFCollectionUrl;
            config.LastRunOn = DateTimeOffset.Now;
            switch (executionContext.Variables.System_HostType)
            {
                case HostTypes.Build:
                    config.DefinitionName = executionContext.Variables.Build_DefinitionName;
                    break;
                case HostTypes.Release | HostTypes.Deployment:
                    config.DefinitionName = executionContext.Variables.Get(Constants.Variables.Release.ReleaseDefinitionName);
                    break;
                default:
                    break;
            }

            // Use to be single repo and never have multiple repositories, now have multiple repositories
            if (config.Resources.Repositories.Count == 1 && executionContext.Repositories.Count > 1)
            {
                var selfRepo = executionContext.Repositories.Single(x => string.Equals(x.Alias, "self", StringComparison.OrdinalIgnoreCase));
                ArgUtil.NotNull(selfRepo, nameof(selfRepo));
                config.Resources.Repositories.TryGetValue("self", out RepositoryTrackingConfig selfRepoTracking);
                ArgUtil.NotNull(selfRepoTracking, nameof(selfRepoTracking));

                if (string.Equals(selfRepoTracking.SourceDirectory, config.Resources.RepositoriesDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    var currentSourceDirectory = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Work), selfRepoTracking.SourceDirectory);
                    var newSourceDirectory = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Work), config.Resources.RepositoriesDirectory, selfRepo.Alias);
                    if (Directory.Exists(currentSourceDirectory))
                    {
                        // move current /s to /s/self since we have more repositories need to stored
                        executionContext.Debug($"Move current source directory from '{currentSourceDirectory}' to '{newSourceDirectory}'");
                        var stagingDir = Path.Combine(executionContext.Variables.Agent_TempDirectory, Guid.NewGuid().ToString("D"));
                        try
                        {
                            Directory.Move(currentSourceDirectory, stagingDir);
                            Directory.Move(stagingDir, newSourceDirectory);
                        }
                        catch (Exception ex)
                        {
                            Trace.Error(ex);
                            // if we can't move the folder and we can't delete the folder, just fail the job.
                            IOUtil.DeleteDirectory(currentSourceDirectory, CancellationToken.None);
                        }
                    }

                    config.Resources.Repositories[selfRepo.Alias].SourceDirectory = Path.Combine(config.Resources.RepositoriesDirectory, selfRepo.Alias);
                }
            }

            // delete local repository if it's no longer need for the definition.
            List<string> staleRepo = new List<string>();
            foreach (var repo in config.Resources.Repositories)
            {
                var existingRepo = executionContext.Repositories.SingleOrDefault(x => string.Equals(x.Alias, repo.Key, StringComparison.OrdinalIgnoreCase));
                if (existingRepo == null || !string.Equals(existingRepo.Url.AbsoluteUri, repo.Value.RepositoryUrl, StringComparison.OrdinalIgnoreCase))
                {
                    staleRepo.Add(repo.Key);
                }
            }
            foreach (var stale in staleRepo)
            {
                executionContext.Debug($"Delete stale local source directory '{config.Resources.Repositories[stale].SourceDirectory}' for repository '{config.Resources.Repositories[stale].RepositoryUrl}' ({stale}).");
                var sourceDir = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Work), config.Resources.Repositories[stale].SourceDirectory);
                // if we can't move the folder and we can't delete the folder, just fail the job.
                IOUtil.DeleteDirectory(sourceDir, CancellationToken.None);
                config.Resources.Repositories.Remove(stale);
            }

            // add any new repositories' information
            foreach (var repo in executionContext.Repositories)
            {
                if (!config.Resources.Repositories.ContainsKey(repo.Alias))
                {
                    executionContext.Debug($"Add new repository '{repo.Url.AbsoluteUri}' ({repo.Alias}) at '{Path.Combine(config.Resources.RepositoriesDirectory, repo.Alias)}'.");
                    config.Resources.Repositories[repo.Alias] = new RepositoryTrackingConfig()
                    {
                        RepositoryType = repo.Type,
                        RepositoryUrl = repo.Url.AbsoluteUri,
                        SourceDirectory = Path.Combine(config.Resources.RepositoriesDirectory, repo.Alias)
                    };
                }
            }

            // Set repository resource variable
            // foreach (var repoResource in config.Resources.Repositories)
            // {
            //     var repo = executionContext.Repositories.Single(x => x.Alias == repoResource.Key);
            //     executionContext.Variables.Set($"system.repository.{repo.Alias}.id", repo.Id);
            //     executionContext.Variables.Set($"system.repository.{repo.Alias}.name", repo.Properties.Get<string>("name") ?? string.Empty);
            //     executionContext.Variables.Set($"system.repository.{repo.Alias}.provider", repo.Type);
            //     executionContext.Variables.Set($"system.repository.{repo.Alias}.uri", repo.Url?.AbsoluteUri);
            //     executionContext.Variables.Set($"system.repository.{repo.Alias}.clean", repo.Properties.Get<string>("clean") ?? string.Empty);
            //     executionContext.Variables.Set($"system.repository.{repo.Alias}.localpath", Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Work), repoResource.Value.SourceDirectory));
            // }

            // Set build drop resource variable
            // foreach (var dropResource in config.Resources.Drops)
            // {
            //     var build = executionContext.Builds.Single(x => x.Alias == dropResource.Key);
            //     executionContext.Variables.Set($"system.build.{build.Alias}.version", build.Version);
            //     executionContext.Variables.Set($"system.build.{build.Alias}.type", build.Type);
            //     executionContext.Variables.Set($"system.build.{build.Alias}.localpath", Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Work), dropResource.Value.DropDirectory));
            // }

            WriteToFile(file, config);
        }

        public TrackingConfig LoadIfExists(IExecutionContext executionContext, string file)
        {
            Trace.Entering();

            // The tracking config will not exist for a new definition.
            if (!File.Exists(file))
            {
                return null;
            }
            else
            {
                string content = File.ReadAllText(file);
                return JsonConvert.DeserializeObject<TrackingConfig>(content);
            }
        }

        //         public void MarkForGarbageCollection(IExecutionContext executionContext, TrackingConfigBase config)
        //         {
        //             Trace.Entering();

        //             // Convert legacy format to the new format.
        //             LegacyTrackingConfig legacyConfig = config as LegacyTrackingConfig;
        //             if (legacyConfig != null)
        //             {
        //                 // Convert legacy format to the new format.
        //                 config = new TrackingConfig(
        //                     executionContext,
        //                     legacyConfig,
        //                     // The repository type and sources folder wasn't stored in the legacy format - only the
        //                     // build folder was stored. Since the hash key has changed, it is
        //                     // unknown what the source folder was named. Just set the folder name
        //                     // to "s" so the property isn't left blank. 
        //                     repositoryType: string.Empty,
        //                     sourcesDirectoryNameOnly: Constants.Build.Path.SourcesDirectory);
        //             }

        //             // Write a copy of the tracking config to the GC folder.
        //             string gcDirectory = Path.Combine(
        //                 IOUtil.GetWorkPath(HostContext),
        //                 Constants.Build.Path.SourceRootMappingDirectory,
        //                 Constants.Build.Path.GarbageCollectionDirectory);
        //             string file = Path.Combine(
        //                 gcDirectory,
        //                 StringUtil.Format("{0}.json", Guid.NewGuid()));
        //             WriteToFile(file, config);
        //         }

        //         public void MaintenanceStarted(TrackingConfig config, string file)
        //         {
        //             Trace.Entering();
        //             config.LastMaintenanceAttemptedOn = DateTimeOffset.Now;
        //             config.LastMaintenanceCompletedOn = null;
        //             WriteToFile(file, config);
        //         }

        //         public void MaintenanceCompleted(TrackingConfig config, string file)
        //         {
        //             Trace.Entering();
        //             config.LastMaintenanceCompletedOn = DateTimeOffset.Now;
        //             WriteToFile(file, config);
        //         }

        //         public void MarkExpiredForGarbageCollection(IExecutionContext executionContext, TimeSpan expiration)
        //         {
        //             Trace.Entering();
        //             Trace.Info("Scan all SourceFolder tracking files.");
        //             string searchRoot = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Work), Constants.Build.Path.SourceRootMappingDirectory);
        //             if (!Directory.Exists(searchRoot))
        //             {
        //                 executionContext.Output(StringUtil.Loc("GCDirNotExist", searchRoot));
        //                 return;
        //             }

        //             var allTrackingFiles = Directory.EnumerateFiles(searchRoot, Constants.Build.Path.TrackingConfigFile, SearchOption.AllDirectories);
        //             Trace.Verbose($"Find {allTrackingFiles.Count()} tracking files.");

        //             executionContext.Output(StringUtil.Loc("DirExpireLimit", expiration.TotalDays));
        //             executionContext.Output(StringUtil.Loc("CurrentUTC", DateTime.UtcNow.ToString("o")));

        //             // scan all sourcefolder tracking file, find which folder has never been used since UTC-expiration
        //             // the scan and garbage discovery should be best effort.
        //             // if the tracking file is in old format, just delete the folder since the first time the folder been use we will convert the tracking file to new format.
        //             foreach (var trackingFile in allTrackingFiles)
        //             {
        //                 try
        //                 {
        //                     executionContext.Output(StringUtil.Loc("EvaluateTrackingFile", trackingFile));
        //                     TrackingConfigBase tracking = LoadIfExists(executionContext, trackingFile);

        //                     // detect whether the tracking file is in new format.
        //                     TrackingConfig newTracking = tracking as TrackingConfig;
        //                     if (newTracking == null)
        //                     {
        //                         LegacyTrackingConfig legacyConfig = tracking as LegacyTrackingConfig;
        //                         ArgUtil.NotNull(legacyConfig, nameof(LegacyTrackingConfig));

        //                         Trace.Verbose($"{trackingFile} is a old format tracking file.");

        //                         executionContext.Output(StringUtil.Loc("GCOldFormatTrackingFile", trackingFile));
        //                         MarkForGarbageCollection(executionContext, legacyConfig);
        //                         IOUtil.DeleteFile(trackingFile);
        //                     }
        //                     else
        //                     {
        //                         Trace.Verbose($"{trackingFile} is a new format tracking file.");
        //                         ArgUtil.NotNull(newTracking.LastRunOn, nameof(newTracking.LastRunOn));
        //                         executionContext.Output(StringUtil.Loc("BuildDirLastUseTIme", Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Work), newTracking.BuildDirectory), newTracking.LastRunOnString));
        //                         if (DateTime.UtcNow - expiration > newTracking.LastRunOn)
        //                         {
        //                             executionContext.Output(StringUtil.Loc("GCUnusedTrackingFile", trackingFile, expiration.TotalDays));
        //                             MarkForGarbageCollection(executionContext, newTracking);
        //                             IOUtil.DeleteFile(trackingFile);
        //                         }
        //                     }
        //                 }
        //                 catch (Exception ex)
        //                 {
        //                     executionContext.Error(StringUtil.Loc("ErrorDuringBuildGC", trackingFile));
        //                     executionContext.Error(ex);
        //                 }
        //             }
        //         }

        //         public void DisposeCollectedGarbage(IExecutionContext executionContext)
        //         {
        //             Trace.Entering();
        //             PrintOutDiskUsage(executionContext);

        //             string gcDirectory = Path.Combine(
        //                 HostContext.GetDirectory(WellKnownDirectory.Work),
        //                 Constants.Build.Path.SourceRootMappingDirectory,
        //                 Constants.Build.Path.GarbageCollectionDirectory);

        //             if (!Directory.Exists(gcDirectory))
        //             {
        //                 executionContext.Output(StringUtil.Loc("GCDirNotExist", gcDirectory));
        //                 return;
        //             }

        //             IEnumerable<string> gcTrackingFiles = Directory.EnumerateFiles(gcDirectory, "*.json");
        //             if (gcTrackingFiles == null || gcTrackingFiles.Count() == 0)
        //             {
        //                 executionContext.Output(StringUtil.Loc("GCDirIsEmpty", gcDirectory));
        //                 return;
        //             }

        //             Trace.Info($"Find {gcTrackingFiles.Count()} GC tracking files.");

        //             if (gcTrackingFiles.Count() > 0)
        //             {
        //                 foreach (string gcFile in gcTrackingFiles)
        //                 {
        //                     // maintenance has been cancelled.
        //                     executionContext.CancellationToken.ThrowIfCancellationRequested();

        //                     try
        //                     {
        //                         var gcConfig = LoadIfExists(executionContext, gcFile) as TrackingConfig;
        //                         ArgUtil.NotNull(gcConfig, nameof(TrackingConfig));

        //                         string fullPath = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Work), gcConfig.BuildDirectory);
        //                         executionContext.Output(StringUtil.Loc("Deleting", fullPath));
        //                         IOUtil.DeleteDirectory(fullPath, executionContext.CancellationToken);

        //                         executionContext.Output(StringUtil.Loc("DeleteGCTrackingFile", fullPath));
        //                         IOUtil.DeleteFile(gcFile);
        //                     }
        //                     catch (Exception ex)
        //                     {
        //                         executionContext.Error(StringUtil.Loc("ErrorDuringBuildGCDelete", gcFile));
        //                         executionContext.Error(ex);
        //                     }
        //                 }

        //                 PrintOutDiskUsage(executionContext);
        //             }
        //         }

        //         private void PrintOutDiskUsage(IExecutionContext context)
        //         {
        //             // Print disk usage should be best effort, since DriveInfo can't detect usage of UNC share.
        //             try
        //             {
        //                 context.Output($"Disk usage for working directory: {HostContext.GetDirectory(WellKnownDirectory.Work)}");
        //                 var workDirectoryDrive = new DriveInfo(HostContext.GetDirectory(WellKnownDirectory.Work));
        //                 long freeSpace = workDirectoryDrive.AvailableFreeSpace;
        //                 long totalSpace = workDirectoryDrive.TotalSize;
        // #if OS_WINDOWS
        //                 context.Output($"Working directory belongs to drive: '{workDirectoryDrive.Name}'");
        // #else
        //                 context.Output($"Information about file system on which working directory resides.");
        // #endif
        //                 context.Output($"Total size: '{totalSpace / 1024.0 / 1024.0} MB'");
        //                 context.Output($"Available space: '{freeSpace / 1024.0 / 1024.0} MB'");
        //             }
        //             catch (Exception ex)
        //             {
        //                 context.Warning($"Unable inspect disk usage for working directory {HostContext.GetDirectory(WellKnownDirectory.Work)}.");
        //                 Trace.Error(ex);
        //                 context.Debug(ex.ToString());
        //             }
        //         }

        private void WriteToFile(string file, object value)
        {
            Trace.Entering();
            Trace.Verbose($"Writing config to file: {file}");

            // Create the directory if it does not exist.
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            IOUtil.SaveObject(value, file);
        }
    }

    public sealed class TopLevelTrackingConfig
    {
        [JsonIgnore]
        public DateTimeOffset? LastJobDirectoryCreatedOn { get; set; }

        [JsonProperty("lastJobFolderCreatedOn")]
        [EditorBrowsableAttribute(EditorBrowsableState.Never)]
        public string LastJobDirectoryCreatedOnString
        {
            get
            {
                return string.Format(CultureInfo.InvariantCulture, "{0}", LastJobDirectoryCreatedOn);
            }

            set
            {
                if (string.IsNullOrEmpty(value))
                {
                    LastJobDirectoryCreatedOn = null;
                    return;
                }

                LastJobDirectoryCreatedOn = DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
            }
        }

        [JsonProperty("lastJobFolderNumber")]
        public int LastJobDirectoryNumber { get; set; }
    }

    public sealed class TrackingConfig
    {
        // The parameterless constructor is required for deserialization.
        public TrackingConfig()
        {
        }

        public TrackingConfig(
            IExecutionContext executionContext,
            int jobDirectory)
        {
            // Set basic properties
            System = executionContext.Variables.Get(Constants.Variables.System.HostType);
            CollectionId = executionContext.Variables.System_CollectionId;
            CollectionUrl = executionContext.Variables.System_TFCollectionUrl;
            DefinitionId = executionContext.Variables.System_DefinitionId;
            switch (executionContext.Variables.System_HostType)
            {
                case HostTypes.Build:
                    DefinitionName = executionContext.Variables.Build_DefinitionName;
                    break;
                case HostTypes.Release | HostTypes.Deployment:
                    DefinitionName = executionContext.Variables.Get(Constants.Variables.Release.ReleaseDefinitionName);
                    break;
                default:
                    break;
            }
            LastRunOn = DateTimeOffset.Now;

            // Set the directories.
            JobDirectory = jobDirectory.ToString(CultureInfo.InvariantCulture);
            Resources = new ResourceTrackingConfig(executionContext, JobDirectory);
        }

        public string System { get; set; }
        public string CollectionId { get; set; }
        public string CollectionUrl { get; set; }
        public string DefinitionId { get; set; }
        public string DefinitionName { get; set; }

        [JsonProperty("system_jobdirectory")]
        public string JobDirectory { get; set; }

        [JsonProperty("fileFormatVersion")]
        public int FileFormatVersion => 1;

        [JsonIgnore]
        public DateTimeOffset? LastRunOn { get; set; }

        [JsonProperty("lastRunOn")]
        [EditorBrowsableAttribute(EditorBrowsableState.Never)]
        public string LastRunOnString
        {
            get
            {
                return string.Format(CultureInfo.InvariantCulture, "{0}", LastRunOn);
            }

            set
            {
                if (string.IsNullOrEmpty(value))
                {
                    LastRunOn = null;
                    return;
                }

                LastRunOn = DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
            }
        }

        [JsonProperty("system_resources")]
        public ResourceTrackingConfig Resources { get; set; }
    }

    public sealed class ResourceTrackingConfig
    {
        private Dictionary<string, RepositoryTrackingConfig> _repositories;
        private Dictionary<string, DropTrackingConfig> _drops;

        public ResourceTrackingConfig()
        { }

        public ResourceTrackingConfig(IExecutionContext executionContext, string resourcesRoot)
        {
            RepositoriesDirectory = Path.Combine(resourcesRoot, Constants.Resource.Path.SourcesDirectory);  // 1/s dir
            DropsDirectory = Path.Combine(resourcesRoot, Constants.Resource.Path.DropsDirectory);  // 1/d dir

            // tracking repository resources
            if (executionContext.Repositories.Count > 0)
            {
                // if there is one repository, we will keep using the layout format we have today, _work/1/s 
                // if there are multiple repositories, we will put each repository under the sub-dir of its alias, _work/1/s/self
                if (executionContext.Repositories.Count == 1)
                {
                    var repo = executionContext.Repositories[0];
                    Repositories[repo.Alias] = new RepositoryTrackingConfig()
                    {
                        RepositoryType = repo.Type,
                        RepositoryUrl = repo.Url.AbsoluteUri,
                        SourceDirectory = RepositoriesDirectory
                    };
                }
                else
                {
                    // multiple repositories
                    foreach (var repo in executionContext.Repositories)
                    {
                        Repositories[repo.Alias] = new RepositoryTrackingConfig()
                        {
                            RepositoryType = repo.Type,
                            RepositoryUrl = repo.Url.AbsoluteUri,
                            SourceDirectory = Path.Combine(RepositoriesDirectory, repo.Alias)
                        };
                    }
                }
            }

            // tracking build drop resources
            if (executionContext.Builds.Count > 0)
            {
                // if there is one build drop, we will keep using the layout format we have today, _work/1/d 
                // if there are multiple build drops, we will put each build drop under the sub-dir of its alias, _work/1/d/L0
                if (executionContext.Builds.Count == 1)
                {
                    var build = executionContext.Builds[0];
                    Drops[build.Alias] = new DropTrackingConfig()
                    {
                        DropType = build.Type,
                        DropVersion = build.Version,
                        DropDirectory = DropsDirectory
                    };
                }
                else
                {
                    // multiple repositories
                    foreach (var build in executionContext.Builds)
                    {
                        Drops[build.Alias] = new DropTrackingConfig()
                        {
                            DropType = build.Type,
                            DropVersion = build.Version,
                            DropDirectory = Path.Combine(RepositoriesDirectory, build.Alias)
                        };
                    }
                }
            }
        }

        public string RepositoriesDirectory { get; set; }

        public string DropsDirectory { get; set; }

        public Dictionary<string, RepositoryTrackingConfig> Repositories
        {
            get
            {
                if (_repositories == null)
                {
                    _repositories = new Dictionary<string, RepositoryTrackingConfig>(StringComparer.OrdinalIgnoreCase);
                }
                return _repositories;
            }
        }

        public Dictionary<string, DropTrackingConfig> Drops
        {
            get
            {
                if (_drops == null)
                {
                    _drops = new Dictionary<string, DropTrackingConfig>(StringComparer.OrdinalIgnoreCase);
                }
                return _drops;
            }
        }
    }

    public sealed class DropTrackingConfig
    {
        public string DropVersion { get; set; }
        public string DropType { get; set; }
        public string DropDirectory { get; set; }
    }

    public sealed class RepositoryTrackingConfig
    {
        public string RepositoryUrl { get; set; }
        public string RepositoryType { get; set; }
        public string SourceDirectory { get; set; }

        [JsonIgnore]
        public DateTimeOffset? LastMaintenanceAttemptedOn { get; set; }

        [JsonProperty("lastMaintenanceAttemptedOn")]
        [EditorBrowsableAttribute(EditorBrowsableState.Never)]
        public string LastMaintenanceAttemptedOnString
        {
            get
            {
                return string.Format(CultureInfo.InvariantCulture, "{0}", LastMaintenanceAttemptedOn);
            }

            set
            {
                if (string.IsNullOrEmpty(value))
                {
                    LastMaintenanceAttemptedOn = null;
                    return;
                }

                LastMaintenanceAttemptedOn = DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
            }
        }

        [JsonIgnore]
        public DateTimeOffset? LastMaintenanceCompletedOn { get; set; }

        [JsonProperty("lastMaintenanceCompletedOn")]
        [EditorBrowsableAttribute(EditorBrowsableState.Never)]
        public string LastMaintenanceCompletedOnString
        {
            get
            {
                return string.Format(CultureInfo.InvariantCulture, "{0}", LastMaintenanceCompletedOn);
            }

            set
            {
                if (string.IsNullOrEmpty(value))
                {
                    LastMaintenanceCompletedOn = null;
                    return;
                }

                LastMaintenanceCompletedOn = DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
            }
        }
    }
}