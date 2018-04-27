using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Services.Agent.PluginCore;
using Pipelines = Microsoft.TeamFoundation.DistributedTask.Pipelines;
using Microsoft.Win32;

namespace Agent.RepositoryPlugin
{
    public sealed class TfsVCSourceProvider : ISourceProvider
    {
        private bool _undoShelvesetPendingChanges = false;

        public async Task GetSourceAsync(
            AgentTaskPluginExecutionContext executionContext,
            Pipelines.RepositoryResource repository,
            CancellationToken cancellationToken)
        {
            // Validate args.
            PluginUtil.NotNull(executionContext, nameof(executionContext));
            PluginUtil.NotNull(repository, nameof(repository));

#if OS_WINDOWS
            // Validate .NET Framework 4.6 or higher is installed.
            if (!NetFrameworkUtil.Test(executionContext, new Version(4, 6)))
            {
                throw new Exception("MinimumNetFramework46");
            }
#endif

            // Create the tf command manager.
#if OS_WINDOWS
            var tf = new TFCommandManager();
#else
            var tf = new TeeCommandManager();
#endif            
            tf.CancellationToken = cancellationToken;
            tf.Repository = repository;
            tf.ExecutionContext = executionContext;
            if (repository.Endpoint != null)
            {
                var endpoint = executionContext.Endpoints.SingleOrDefault(x => (x.Id == Guid.Empty && x.Name == repository.Endpoint.Name) || (x.Id != Guid.Empty && x.Id == repository.Endpoint.Id));
                PluginUtil.NotNull(endpoint, nameof(endpoint));
                tf.Endpoint = endpoint;
            }

            // Setup proxy.
            var agentProxy = executionContext.GetProxyConfiguration();
            if (!string.IsNullOrEmpty(agentProxy.ProxyAddress) && !agentProxy.IsBypassed(repository.Url))
            {
                executionContext.Debug($"Configure '{tf.FilePath}' to work through proxy server '{agentProxy.ProxyAddress}'.");
                tf.SetupProxy(agentProxy.ProxyAddress, agentProxy.ProxyUsername, agentProxy.ProxyPassword);
            }

            // Setup client certificate.
            var agentCertManager = executionContext.GetCertConfiguration();
            if (agentCertManager.SkipServerCertificateValidation)
            {
#if OS_WINDOWS
                executionContext.Debug("TF.exe does not support ignore SSL certificate validation error.");
#else
                executionContext.Debug("TF does not support ignore SSL certificate validation error.");
#endif
            }

            var configUrl = new Uri(executionContext.Variables.GetValueOrDefault(WellKnownDistributedTaskVariables.TFCollectionUrl)?.Value);
            if (!string.IsNullOrEmpty(agentCertManager.ClientCertificateFile) &&
                Uri.Compare(repository.Url, configUrl, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0)
            {
                executionContext.Debug($"Configure '{tf.FilePath}' to work with client cert '{agentCertManager.ClientCertificateFile}'.");
                tf.SetupClientCertificate(agentCertManager.ClientCertificateFile, agentCertManager.ClientCertificatePrivateKeyFile, agentCertManager.ClientCertificateArchiveFile, agentCertManager.ClientCertificatePassword);
            }

            // Add TF to the PATH.
            string tfPath = tf.FilePath;
            PluginUtil.FileExists(tfPath, nameof(tfPath));
            executionContext.Output("Prepending0WithDirectoryContaining1 {Constants.PathVariable} {Path.GetFileName(tfPath)}");
            PrependPath(Path.GetDirectoryName(tfPath));
            executionContext.Debug($"PATH: '{Environment.GetEnvironmentVariable("PATH")}'");

#if OS_WINDOWS
            // Set TFVC_BUILDAGENT_POLICYPATH
            string policyDllPath = Path.Combine(executionContext.Variables.GetValueOrDefault("Agent.ServerOMDirectory")?.Value, "Microsoft.TeamFoundation.VersionControl.Controls.dll");
            PluginUtil.FileExists(policyDllPath, nameof(policyDllPath));
            const string policyPathEnvKey = "TFVC_BUILDAGENT_POLICYPATH";
            executionContext.Output("SetEnvVar {policyPathEnvKey}");
            Environment.SetEnvironmentVariable(policyPathEnvKey, policyDllPath);
#endif

            // Check if the administrator accepted the license terms of the TEE EULA when configuring the agent.
            // AgentSettings settings = HostContext.GetService<IConfigurationStore>().GetSettings();
            // if (tf.Features.HasFlag(TfsVCFeatures.Eula) && settings.AcceptTeeEula)
            // {
            //     // Check if the "tf eula -accept" command needs to be run for the current user.
            //     bool skipEula = false;
            //     try
            //     {
            //         skipEula = tf.TestEulaAccepted();
            //     }
            //     catch (Exception ex)
            //     {
            //         executionContext.Debug("Unexpected exception while testing whether the TEE EULA has been accepted for the current user.");
            //         executionContext.Debug(ex.ToString());
            //     }

            //     if (!skipEula)
            //     {
            //         // Run the command "tf eula -accept".
            //         try
            //         {
            //             await tf.EulaAsync();
            //         }
            //         catch (Exception ex)
            //         {
            //             executionContext.Debug(ex.ToString());
            //             executionContext.Warning(ex.Message);
            //         }
            //     }
            // }

            // Get the workspaces.
            executionContext.Output("QueryingWorkspaceInfo");
            ITfsVCWorkspace[] tfWorkspaces = await tf.WorkspacesAsync();

            // Determine the workspace name.
            string buildDirectory = executionContext.Variables.GetValueOrDefault("agent.builddirectory")?.Value;
            PluginUtil.NotNullOrEmpty(buildDirectory, nameof(buildDirectory));
            string workspaceName = $"ws_{Path.GetFileName(buildDirectory)}_{executionContext.Variables.GetValueOrDefault("agent.id")?.Value}";
            executionContext.SetVariable("build.repository.tfvc.workspace", workspaceName);

            // Get the definition mappings.
            DefinitionWorkspaceMapping[] definitionMappings =
                JsonConvert.DeserializeObject<DefinitionWorkspaceMappings>(repository.Properties.Get<string>(EndpointData.TfvcWorkspaceMapping))?.Mappings;

            // Determine the sources directory.
            string sourcesDirectory = repository.Properties.Get<string>("sourcedirectory");
            PluginUtil.NotNullOrEmpty(sourcesDirectory, nameof(sourcesDirectory));

            // Attempt to re-use an existing workspace if the command manager supports scorch
            // or if clean is not specified.
            ITfsVCWorkspace existingTFWorkspace = null;
            bool clean = PluginUtil.ConvertToBoolean(repository.Properties.Get<string>(EndpointData.Clean));
            if (tf.Features.HasFlag(TfsVCFeatures.Scorch) || !clean)
            {
                existingTFWorkspace = WorkspaceUtil.MatchExactWorkspace(
                    executionContext: executionContext,
                    tfWorkspaces: tfWorkspaces,
                    name: workspaceName,
                    definitionMappings: definitionMappings,
                    sourcesDirectory: sourcesDirectory);
                if (existingTFWorkspace != null)
                {
                    if (tf.Features.HasFlag(TfsVCFeatures.GetFromUnmappedRoot))
                    {
                        // Undo pending changes.
                        ITfsVCStatus tfStatus = await tf.StatusAsync(localPath: sourcesDirectory);
                        if (tfStatus?.HasPendingChanges ?? false)
                        {
                            await tf.UndoAsync(localPath: sourcesDirectory);

                            // Cleanup remaining files/directories from pend adds.
                            tfStatus.AllAdds
                                .OrderByDescending(x => x.LocalItem) // Sort descending so nested items are deleted before their parent is deleted.
                                .ToList()
                                .ForEach(x =>
                                {
                                    executionContext.Output("Deleting {x.LocalItem}");
                                    PluginUtil.Delete(x.LocalItem, cancellationToken);
                                });
                        }
                    }
                    else
                    {
                        // Perform "undo" for each map.
                        foreach (DefinitionWorkspaceMapping definitionMapping in definitionMappings ?? new DefinitionWorkspaceMapping[0])
                        {
                            if (definitionMapping.MappingType == DefinitionMappingType.Map)
                            {
                                // Check the status.
                                string localPath = definitionMapping.GetRootedLocalPath(sourcesDirectory);
                                ITfsVCStatus tfStatus = await tf.StatusAsync(localPath: localPath);
                                if (tfStatus?.HasPendingChanges ?? false)
                                {
                                    // Undo.
                                    await tf.UndoAsync(localPath: localPath);

                                    // Cleanup remaining files/directories from pend adds.
                                    tfStatus.AllAdds
                                        .OrderByDescending(x => x.LocalItem) // Sort descending so nested items are deleted before their parent is deleted.
                                        .ToList()
                                        .ForEach(x =>
                                        {
                                            executionContext.Output("Deleting  {x.LocalItem}");
                                            PluginUtil.Delete(x.LocalItem, cancellationToken);
                                        });
                                }
                            }
                        }
                    }

                    // Scorch.
                    if (clean)
                    {
                        // Try to scorch.
                        try
                        {
                            await tf.ScorchAsync();
                        }
                        catch (ProcessExitCodeException ex)
                        {
                            // Scorch failed.
                            // Warn, drop the folder, and re-clone.
                            executionContext.Warning(ex.Message);
                            existingTFWorkspace = null;
                        }
                    }
                }
            }

            // Create a new workspace.
            if (existingTFWorkspace == null)
            {
                // Remove any conflicting workspaces.
                await RemoveConflictingWorkspacesAsync(
                    tf: tf,
                    tfWorkspaces: tfWorkspaces,
                    name: workspaceName,
                    directory: sourcesDirectory);

                // Remove any conflicting workspace from a different computer.
                // This is primarily a hosted scenario where a registered hosted
                // agent can land on a different computer each time.
                tfWorkspaces = await tf.WorkspacesAsync(matchWorkspaceNameOnAnyComputer: true);
                foreach (ITfsVCWorkspace tfWorkspace in tfWorkspaces ?? new ITfsVCWorkspace[0])
                {
                    await tf.WorkspaceDeleteAsync(tfWorkspace);
                }

                // Recreate the sources directory.
                executionContext.Debug($"Deleting: '{sourcesDirectory}'.");
                PluginUtil.DeleteDirectory(sourcesDirectory, cancellationToken);
                Directory.CreateDirectory(sourcesDirectory);

                // Create the workspace.
                await tf.WorkspaceNewAsync();

                // Remove the default mapping.
                if (tf.Features.HasFlag(TfsVCFeatures.DefaultWorkfoldMap))
                {
                    await tf.WorkfoldUnmapAsync("$/");
                }

                // Sort the definition mappings.
                definitionMappings =
                                    (definitionMappings ?? new DefinitionWorkspaceMapping[0])
                                    .OrderBy(x => x.NormalizedServerPath?.Length ?? 0) // By server path length.
                                    .ToArray() ?? new DefinitionWorkspaceMapping[0];

                // Add the definition mappings to the workspace.
                foreach (DefinitionWorkspaceMapping definitionMapping in definitionMappings)
                {
                    switch (definitionMapping.MappingType)
                    {
                        case DefinitionMappingType.Cloak:
                            // Add the cloak.
                            await tf.WorkfoldCloakAsync(serverPath: definitionMapping.ServerPath);
                            break;
                        case DefinitionMappingType.Map:
                            // Add the mapping.
                            await tf.WorkfoldMapAsync(
                                serverPath: definitionMapping.ServerPath,
                                localPath: definitionMapping.GetRootedLocalPath(sourcesDirectory));
                            break;
                        default:
                            throw new NotSupportedException();
                    }
                }
            }

            if (tf.Features.HasFlag(TfsVCFeatures.GetFromUnmappedRoot))
            {
                // Get.
                await tf.GetAsync(localPath: sourcesDirectory);
            }
            else
            {
                // Perform "get" for each map.
                foreach (DefinitionWorkspaceMapping definitionMapping in definitionMappings ?? new DefinitionWorkspaceMapping[0])
                {
                    if (definitionMapping.MappingType == DefinitionMappingType.Map)
                    {
                        await tf.GetAsync(localPath: definitionMapping.GetRootedLocalPath(sourcesDirectory));
                    }
                }
            }

            // Steps for shelveset/gated.
            string shelvesetName = repository.Properties.Get<string>("SourceTfvcShelveset");
            if (!string.IsNullOrEmpty(shelvesetName))
            {
                // Steps for gated.
                ITfsVCShelveset tfShelveset = null;
                string gatedShelvesetName = repository.Properties.Get<string>("GatedShelvesetName");
                if (!string.IsNullOrEmpty(gatedShelvesetName))
                {
                    // Clean the last-saved-checkin-metadata for existing workspaces.
                    //
                    // A better long term fix is to add a switch to "tf unshelve" that completely overwrites
                    // the last-saved-checkin-metadata, instead of merging associated work items.
                    //
                    // The targeted workaround for now is to create a trivial change and "tf shelve /move",
                    // which will delete the last-saved-checkin-metadata.
                    if (existingTFWorkspace != null)
                    {
                        executionContext.Output("Cleaning last saved checkin metadata.");

                        // Find a local mapped directory.
                        string firstLocalDirectory =
                            (definitionMappings ?? new DefinitionWorkspaceMapping[0])
                            .Where(x => x.MappingType == DefinitionMappingType.Map)
                            .Select(x => x.GetRootedLocalPath(sourcesDirectory))
                            .FirstOrDefault(x => Directory.Exists(x));
                        if (firstLocalDirectory == null)
                        {
                            executionContext.Warning("No mapped folder found. Unable to clean last-saved-checkin-metadata.");
                        }
                        else
                        {
                            // Create a trival change and "tf shelve /move" to clear the
                            // last-saved-checkin-metadata.
                            string cleanName = "__tf_clean_wksp_metadata";
                            string tempCleanFile = Path.Combine(firstLocalDirectory, cleanName);
                            try
                            {
                                File.WriteAllText(path: tempCleanFile, contents: "clean last-saved-checkin-metadata", encoding: Encoding.UTF8);
                                await tf.AddAsync(tempCleanFile);
                                await tf.ShelveAsync(shelveset: cleanName, commentFile: tempCleanFile, move: true);
                            }
                            catch (Exception ex)
                            {
                                executionContext.Warning($"Unable to clean last-saved-checkin-metadata. {ex.Message}");
                                try
                                {
                                    await tf.UndoAsync(tempCleanFile);
                                }
                                catch (Exception ex2)
                                {
                                    executionContext.Warning($"Unable to undo '{tempCleanFile}'. {ex2.Message}");
                                }

                            }
                            finally
                            {
                                PluginUtil.DeleteFile(tempCleanFile);
                            }
                        }
                    }

                    // Get the shelveset metadata.
                    tfShelveset = await tf.ShelvesetsAsync(shelveset: shelvesetName);
                    // The above command throws if the shelveset is not found,
                    // so the following assertion should never fail.
                    PluginUtil.NotNull(tfShelveset, nameof(tfShelveset));
                }

                // Unshelve.
                await tf.UnshelveAsync(shelveset: shelvesetName);

                // Ensure we undo pending changes for shelveset build at the end.
                _undoShelvesetPendingChanges = true;

                if (!string.IsNullOrEmpty(gatedShelvesetName))
                {
                    // Create the comment file for reshelve.
                    StringBuilder comment = new StringBuilder(tfShelveset.Comment ?? string.Empty);
                    string runCi = repository.Properties.Get<string>("GatedRunCI");
                    bool gatedRunCi = PluginUtil.ConvertToBoolean(runCi, true);
                    if (!gatedRunCi)
                    {
                        if (comment.Length > 0)
                        {
                            comment.AppendLine();
                        }

                        comment.Append("***NO_CI***");
                    }

                    string commentFile = null;
                    try
                    {
                        commentFile = Path.GetTempFileName();
                        File.WriteAllText(path: commentFile, contents: comment.ToString(), encoding: Encoding.UTF8);

                        // Reshelve.
                        await tf.ShelveAsync(shelveset: gatedShelvesetName, commentFile: commentFile, move: false);
                    }
                    finally
                    {
                        // Cleanup the comment file.
                        if (File.Exists(commentFile))
                        {
                            File.Delete(commentFile);
                        }
                    }
                }
            }

            // Cleanup proxy settings.
            if (!string.IsNullOrEmpty(agentProxy.ProxyAddress) && !agentProxy.IsBypassed(repository.Url))
            {
                executionContext.Debug($"Remove proxy setting for '{tf.FilePath}' to work through proxy server '{agentProxy.ProxyAddress}'.");
                tf.CleanupProxySetting();
            }
        }

        public async Task PostJobCleanupAsync(AgentTaskPluginExecutionContext executionContext, Pipelines.RepositoryResource repository)
        {
            if (_undoShelvesetPendingChanges)
            {
                string shelvesetName = repository.Properties.Get<string>("SourceTfvcShelveset");
                executionContext.Debug($"Undo pending changes left by shelveset '{shelvesetName}'.");

                // Create the tf command manager.
#if OS_WINDOWS
                var tf = new TFCommandManager();
#else
                var tf = new TEECommandManager();
#endif
                tf.CancellationToken = CancellationToken.None;
                tf.Repository = repository;
                tf.ExecutionContext = executionContext;
                if (repository.Endpoint != null)
                {
                    var endpoint = executionContext.Endpoints.SingleOrDefault(x => (x.Id == Guid.Empty && x.Name == repository.Endpoint.Name) || (x.Id != Guid.Empty && x.Id == repository.Endpoint.Id));
                    PluginUtil.NotNull(endpoint, nameof(endpoint));
                    tf.Endpoint = endpoint;
                }

                // Get the definition mappings.
                DefinitionWorkspaceMapping[] definitionMappings =
                    JsonConvert.DeserializeObject<DefinitionWorkspaceMappings>(repository.Properties.Get<string>(EndpointData.TfvcWorkspaceMapping))?.Mappings;

                // Determine the sources directory.
                string sourcesDirectory = repository.Properties.Get<string>("sourcedirectory");
                PluginUtil.NotNullOrEmpty(sourcesDirectory, nameof(sourcesDirectory));

                try
                {
                    if (tf.Features.HasFlag(TfsVCFeatures.GetFromUnmappedRoot))
                    {
                        // Undo pending changes.
                        ITfsVCStatus tfStatus = await tf.StatusAsync(localPath: sourcesDirectory);
                        if (tfStatus?.HasPendingChanges ?? false)
                        {
                            await tf.UndoAsync(localPath: sourcesDirectory);

                            // Cleanup remaining files/directories from pend adds.
                            tfStatus.AllAdds
                                .OrderByDescending(x => x.LocalItem) // Sort descending so nested items are deleted before their parent is deleted.
                                .ToList()
                                .ForEach(x =>
                                {
                                    executionContext.Output("Deleting {x.LocalItem}");
                                    PluginUtil.Delete(x.LocalItem, CancellationToken.None);
                                });
                        }
                    }
                    else
                    {
                        // Perform "undo" for each map.
                        foreach (DefinitionWorkspaceMapping definitionMapping in definitionMappings ?? new DefinitionWorkspaceMapping[0])
                        {
                            if (definitionMapping.MappingType == DefinitionMappingType.Map)
                            {
                                // Check the status.
                                string localPath = definitionMapping.GetRootedLocalPath(sourcesDirectory);
                                ITfsVCStatus tfStatus = await tf.StatusAsync(localPath: localPath);
                                if (tfStatus?.HasPendingChanges ?? false)
                                {
                                    // Undo.
                                    await tf.UndoAsync(localPath: localPath);

                                    // Cleanup remaining files/directories from pend adds.
                                    tfStatus.AllAdds
                                        .OrderByDescending(x => x.LocalItem) // Sort descending so nested items are deleted before their parent is deleted.
                                        .ToList()
                                        .ForEach(x =>
                                        {
                                            executionContext.Output("Deleting {x.LocalItem}");
                                            PluginUtil.Delete(x.LocalItem, CancellationToken.None);
                                        });
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // We can't undo pending changes, log a warning and continue.
                    executionContext.Debug(ex.ToString());
                    executionContext.Warning(ex.Message);
                }
            }
        }

        private async Task RemoveConflictingWorkspacesAsync(TfsVCCommandManager tf, ITfsVCWorkspace[] tfWorkspaces, string name, string directory)
        {
            // Validate the args.
            PluginUtil.NotNullOrEmpty(name, nameof(name));
            PluginUtil.NotNullOrEmpty(directory, nameof(directory));

            // Fixup the directory.
            directory = directory.TrimEnd('/', '\\');
            PluginUtil.NotNullOrEmpty(directory, nameof(directory));
            string directorySlash = $"{directory}{Path.DirectorySeparatorChar}";

            foreach (ITfsVCWorkspace tfWorkspace in tfWorkspaces ?? new ITfsVCWorkspace[0])
            {
                // Attempt to match the workspace by name.
                if (string.Equals(tfWorkspace.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    // Try deleting the workspace from the server.
                    if (!(await tf.TryWorkspaceDeleteAsync(tfWorkspace)))
                    {
                        // Otherwise fallback to deleting the workspace from the local computer.
                        await tf.WorkspacesRemoveAsync(tfWorkspace);
                    }

                    // Continue iterating over the rest of the workspaces.
                    continue;
                }

                // Attempt to match the workspace by local path.
                foreach (ITfsVCMapping tfMapping in tfWorkspace.Mappings ?? new ITfsVCMapping[0])
                {
                    // Skip cloaks.
                    if (tfMapping.Cloak)
                    {
                        continue;
                    }

                    if (string.Equals(tfMapping.LocalPath, directory, StringComparison.CurrentCultureIgnoreCase) ||
                        (tfMapping.LocalPath ?? string.Empty).StartsWith(directorySlash, StringComparison.CurrentCultureIgnoreCase))
                    {
                        // Try deleting the workspace from the server.
                        if (!(await tf.TryWorkspaceDeleteAsync(tfWorkspace)))
                        {
                            // Otherwise fallback to deleting the workspace from the local computer.
                            await tf.WorkspacesRemoveAsync(tfWorkspace);
                        }

                        // Break out of this nested for loop only.
                        // Continue iterating over the rest of the workspaces.
                        break;
                    }
                }
            }
        }

        private void PrependPath(string directory)
        {
            PluginUtil.DirectoryExists(directory, nameof(directory));

            // Build the new value.
            string currentPath = Environment.GetEnvironmentVariable("PATH");
            string path = PrependPath(directory, currentPath);

            // Update the PATH environment variable.
            Environment.SetEnvironmentVariable("PATH", path);
        }

        private string PrependPath(string path, string currentPath)
        {
            PluginUtil.NotNullOrEmpty(path, nameof(path));
            if (string.IsNullOrEmpty(currentPath))
            {
                // Careful not to add a trailing separator if the PATH is empty.
                // On OSX/Linux, a trailing separator indicates that "current directory"
                // is added to the PATH, which is considered a security risk.
                return path;
            }

            return path + Path.PathSeparator + currentPath;
        }

        public static class WorkspaceUtil
        {
            public static ITfsVCWorkspace MatchExactWorkspace(
                AgentTaskPluginExecutionContext executionContext,
                ITfsVCWorkspace[] tfWorkspaces,
                string name,
                DefinitionWorkspaceMapping[] definitionMappings,
                string sourcesDirectory)
            {
                PluginUtil.NotNullOrEmpty(name, nameof(name));
                PluginUtil.NotNullOrEmpty(sourcesDirectory, nameof(sourcesDirectory));

                // Short-circuit early if the sources directory is empty.
                //
                // Consider the sources directory to be empty if it only contains a .tf directory exists. This can
                // indicate the workspace is in a corrupted state and the tf commands (e.g. status) will not return
                // reliable information. An easy way to reproduce this is to delete the workspace directory, then
                // run "tf status" on that workspace. The .tf directory will be recreated but the contents will be
                // in a corrupted state.
                if (!Directory.Exists(sourcesDirectory) ||
                    !Directory.EnumerateFileSystemEntries(sourcesDirectory).Any(x => !x.EndsWith($"{Path.DirectorySeparatorChar}.tf")))
                {
                    executionContext.Debug("Sources directory does not exist or is empty.");
                    return null;
                }

                string machineName = Environment.MachineName;
                executionContext.Debug($"Attempting to find a workspace: '{name}'");
                foreach (ITfsVCWorkspace tfWorkspace in tfWorkspaces ?? new ITfsVCWorkspace[0])
                {
                    // Compare the workspace name.
                    if (!string.Equals(tfWorkspace.Name, name, StringComparison.Ordinal))
                    {
                        executionContext.Debug($"Skipping workspace: '{tfWorkspace.Name}'");
                        continue;
                    }

                    executionContext.Debug($"Candidate workspace: '{tfWorkspace.Name}'");

                    // Compare the machine name.
                    if (!string.Equals(tfWorkspace.Computer, machineName, StringComparison.Ordinal))
                    {
                        executionContext.Debug($"Expected computer name: '{machineName}'. Actual: '{tfWorkspace.Computer}'");
                        continue;
                    }

                    // Compare the number of mappings.
                    if ((tfWorkspace.Mappings?.Length ?? 0) != (definitionMappings?.Length ?? 0))
                    {
                        executionContext.Debug($"Expected number of mappings: '{definitionMappings?.Length ?? 0}'. Actual: '{tfWorkspace.Mappings?.Length ?? 0}'");
                        continue;
                    }

                    // Sort the definition mappings.
                    List<DefinitionWorkspaceMapping> sortedDefinitionMappings =
                        (definitionMappings ?? new DefinitionWorkspaceMapping[0])
                        .OrderBy(x => x.MappingType != DefinitionMappingType.Cloak) // Cloaks first
                        .ThenBy(x => !x.Recursive) // Then recursive maps
                        .ThenBy(x => x.NormalizedServerPath) // Then sort by the normalized server path
                        .ToList();
                    for (int i = 0; i < sortedDefinitionMappings.Count; i++)
                    {
                        DefinitionWorkspaceMapping mapping = sortedDefinitionMappings[i];
                        executionContext.Debug($"Definition mapping[{i}]: cloak '{mapping.MappingType == DefinitionMappingType.Cloak}', recursive '{mapping.Recursive}', server path '{mapping.NormalizedServerPath}', local path '{mapping.GetRootedLocalPath(sourcesDirectory)}'");
                    }

                    // Sort the TF mappings.
                    List<ITfsVCMapping> sortedTFMappings =
                        (tfWorkspace.Mappings ?? new ITfsVCMapping[0])
                        .OrderBy(x => !x.Cloak) // Cloaks first
                        .ThenBy(x => !x.Recursive) // Then recursive maps
                        .ThenBy(x => x.ServerPath) // Then sort by server path
                        .ToList();
                    for (int i = 0; i < sortedTFMappings.Count; i++)
                    {
                        ITfsVCMapping mapping = sortedTFMappings[i];
                        executionContext.Debug($"Found mapping[{i}]: cloak '{mapping.Cloak}', recursive '{mapping.Recursive}', server path '{mapping.ServerPath}', local path '{mapping.LocalPath}'");
                    }

                    // Compare the mappings.
                    bool allMatch = true;
                    for (int i = 0; i < sortedTFMappings.Count; i++)
                    {
                        ITfsVCMapping tfMapping = sortedTFMappings[i];
                        DefinitionWorkspaceMapping definitionMapping = sortedDefinitionMappings[i];

                        // Compare the cloak flag.
                        bool expectedCloak = definitionMapping.MappingType == DefinitionMappingType.Cloak;
                        if (tfMapping.Cloak != expectedCloak)
                        {
                            executionContext.Debug($"Expected mapping[{i}] cloak: '{expectedCloak}'. Actual: '{tfMapping.Cloak}'");
                            allMatch = false;
                            break;
                        }

                        // Compare the recursive flag.
                        if (!expectedCloak && tfMapping.Recursive != definitionMapping.Recursive)
                        {
                            executionContext.Debug($"Expected mapping[{i}] recursive: '{definitionMapping.Recursive}'. Actual: '{tfMapping.Recursive}'");
                            allMatch = false;
                            break;
                        }

                        // Compare the server path. Normalize the expected server path for a single-level map.
                        string expectedServerPath = definitionMapping.NormalizedServerPath;
                        if (!string.Equals(tfMapping.ServerPath, expectedServerPath, StringComparison.Ordinal))
                        {
                            executionContext.Debug($"Expected mapping[{i}] server path: '{expectedServerPath}'. Actual: '{tfMapping.ServerPath}'");
                            allMatch = false;
                            break;
                        }

                        // Compare the local path.
                        if (!expectedCloak)
                        {
                            string expectedLocalPath = definitionMapping.GetRootedLocalPath(sourcesDirectory);
                            if (!string.Equals(tfMapping.LocalPath, expectedLocalPath, StringComparison.Ordinal))
                            {
                                executionContext.Debug($"Expected mapping[{i}] local path: '{expectedLocalPath}'. Actual: '{tfMapping.LocalPath}'");
                                allMatch = false;
                                break;
                            }
                        }
                    }

                    if (allMatch)
                    {
                        executionContext.Debug("Matching workspace found.");
                        return tfWorkspace;
                    }
                }

                executionContext.Debug("Matching workspace not found.");
                return null;
            }
        }

        public sealed class DefinitionWorkspaceMappings
        {
            public DefinitionWorkspaceMapping[] Mappings { get; set; }
        }

        public sealed class DefinitionWorkspaceMapping
        {
            public string LocalPath { get; set; }

            public DefinitionMappingType MappingType { get; set; }

            /// <summary>
            /// Remove the trailing "/*" from the single-level mapping server path.
            /// If the ServerPath is "$/*", then the normalized path is returned
            /// as "$/" rather than "$".
            /// </summary>
            public string NormalizedServerPath
            {
                get
                {
                    string path;
                    if (!Recursive)
                    {
                        // Trim the last two characters (i.e. "/*") from the single-level
                        // mapping server path.
                        path = ServerPath.Substring(0, ServerPath.Length - 2);

                        // Check if trimmed too much. This is important when comparing
                        // against workspaces on disk.
                        if (string.Equals(path, "$", StringComparison.Ordinal))
                        {
                            path = "$/";
                        }
                    }
                    else
                    {
                        path = ServerPath ?? string.Empty;
                    }

                    return path;
                }
            }

            /// <summary>
            /// Returns true if the path does not end with "/*".
            /// </summary>
            public bool Recursive => !(ServerPath ?? string.Empty).EndsWith("/*");

            public string ServerPath { get; set; }

            /// <summary>
            /// Gets the rooted local path and normalizes slashes.
            /// </summary>
            public string GetRootedLocalPath(string sourcesDirectory)
            {
                // TEE normalizes all slashes in a workspace mapping to match the OS. It is not
                // possible on OSX/Linux to have a workspace mapping with a backslash, even though
                // backslash is a legal file name character.
                string relativePath =
                    (LocalPath ?? string.Empty)
                    .Replace('/', Path.DirectorySeparatorChar)
                    .Replace('\\', Path.DirectorySeparatorChar)
                    .Trim(Path.DirectorySeparatorChar);
                return Path.Combine(sourcesDirectory, relativePath);
            }
        }

        public enum DefinitionMappingType
        {
            Cloak,
            Map,
        }
    }

    public static class NetFrameworkUtil
    {
        private static List<Version> _versions;

        public static bool Test(AgentTaskPluginExecutionContext executionContext, Version minVersion)
        {
            PluginUtil.NotNull(minVersion, nameof(minVersion));
            InitVersions(executionContext);
            executionContext.Debug($"Testing for min NET Framework version: '{minVersion}'");
            return _versions.Any(x => x >= minVersion);
        }

        private static void InitVersions(AgentTaskPluginExecutionContext executionContext)
        {
            // See http://msdn.microsoft.com/en-us/library/hh925568(v=vs.110).aspx for details on how to detect framework versions
            // Also see http://support.microsoft.com/kb/318785

            if (_versions != null)
            {
                return;
            }

            var versions = new List<Version>();

            // Check for install root.
            string installRoot = GetHklmValue(executionContext, @"SOFTWARE\Microsoft\.NETFramework", "InstallRoot") as string;
            if (!string.IsNullOrEmpty(installRoot))
            {
                // Get the version sub key names.
                string ndpKeyName = @"SOFTWARE\Microsoft\NET Framework Setup\NDP";
                string[] versionSubKeyNames = GetHklmSubKeyNames(executionContext, ndpKeyName)
                    .Where(x => x.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                foreach (string versionSubKeyName in versionSubKeyNames)
                {
                    string versionKeyName = $@"{ndpKeyName}\{versionSubKeyName}";

                    // Test for the version value.
                    string version = GetHklmValue(executionContext, versionKeyName, "Version") as string;
                    if (!string.IsNullOrEmpty(version))
                    {
                        // Test for the install flag.
                        object install = GetHklmValue(executionContext, versionKeyName, "Install");
                        if (!(install is int) || (int)install != 1)
                        {
                            continue;
                        }

                        // Test for the install path.
                        string installPath = Path.Combine(installRoot, versionSubKeyName);
                        executionContext.Debug($"Testing directory: '{installPath}'");
                        if (!Directory.Exists(installPath))
                        {
                            continue;
                        }

                        // Parse the version from the sub key name.
                        Version versionObject;
                        if (!Version.TryParse(versionSubKeyName.Substring(1), out versionObject)) // skip over the leading "v".
                        {
                            executionContext.Debug($"Unable to parse version from sub key name: '{versionSubKeyName}'");
                            continue;
                        }

                        executionContext.Debug($"Found version: {versionObject}");
                        versions.Add(versionObject);
                        continue;
                    }

                    // Test if deprecated.
                    if (string.Equals(GetHklmValue(executionContext, versionKeyName, string.Empty) as string, "deprecated", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Get the profile key names.
                    string[] profileKeyNames = GetHklmSubKeyNames(executionContext, versionKeyName)
                        .Select(x => $@"{versionKeyName}\{x}")
                        .ToArray();
                    foreach (string profileKeyName in profileKeyNames)
                    {
                        // Test for the version value.
                        version = GetHklmValue(executionContext, profileKeyName, "Version") as string;
                        if (string.IsNullOrEmpty(version))
                        {
                            continue;
                        }

                        // Test for the install flag.
                        object install = GetHklmValue(executionContext, profileKeyName, "Install");
                        if (!(install is int) || (int)install != 1)
                        {
                            continue;
                        }

                        // Test for the install path.
                        string installPath = (GetHklmValue(executionContext, profileKeyName, "InstallPath") as string ?? string.Empty)
                            .TrimEnd(Path.DirectorySeparatorChar);
                        if (string.IsNullOrEmpty(installPath))
                        {
                            continue;
                        }

                        // Determine the version string.
                        //
                        // Use a range since customer might install beta/preview .NET Framework.
                        string versionString = null;
                        object releaseObject = GetHklmValue(executionContext, profileKeyName, "Release");
                        if (releaseObject != null)
                        {
                            executionContext.Debug("Type is " + releaseObject.GetType().FullName);
                        }

                        if (releaseObject is int)
                        {
                            int release = (int)releaseObject;
                            if (release == 378389)
                            {
                                versionString = "4.5.0";
                            }
                            else if (release > 378389 && release <= 378758)
                            {
                                versionString = "4.5.1";
                            }
                            else if (release > 378758 && release <= 379893)
                            {
                                versionString = "4.5.2";
                            }
                            else if (release > 379893 && release <= 380995)
                            {
                                versionString = "4.5.3";
                            }
                            else if (release > 380995 && release <= 393297)
                            {
                                versionString = "4.6.0";
                            }
                            else if (release > 393297 && release <= 394271)
                            {
                                versionString = "4.6.1";
                            }
                            else if (release > 394271 && release <= 394806)
                            {
                                versionString = "4.6.2";
                            }
                            else if (release > 394806)
                            {
                                versionString = "4.7.0";
                            }
                            else
                            {
                                executionContext.Debug($"Release '{release}' did not fall into an expected range.");
                            }
                        }

                        if (string.IsNullOrEmpty(versionString))
                        {
                            continue;
                        }

                        executionContext.Debug($"Interpreted version: {versionString}");
                        versions.Add(new Version(versionString));
                    }
                }
            }

            executionContext.Debug($"Found {versions.Count} versions:");
            foreach (Version versionObject in versions)
            {
                executionContext.Debug($" {versionObject}");
            }

            Interlocked.CompareExchange(ref _versions, versions, null);
        }

        private static string[] GetHklmSubKeyNames(AgentTaskPluginExecutionContext executionContext, string keyName)
        {
            RegistryKey key = Registry.LocalMachine.OpenSubKey(keyName);
            if (key == null)
            {
                executionContext.Debug($"Key name '{keyName}' is null.");
                return new string[0];
            }

            try
            {
                string[] subKeyNames = key.GetSubKeyNames() ?? new string[0];
                executionContext.Debug($"Key name '{keyName}' contains sub keys:");
                foreach (string subKeyName in subKeyNames)
                {
                    executionContext.Debug($" '{subKeyName}'");
                }

                return subKeyNames;
            }
            finally
            {
                key.Dispose();
            }
        }

        private static object GetHklmValue(AgentTaskPluginExecutionContext executionContext, string keyName, string valueName)
        {
            keyName = $@"HKEY_LOCAL_MACHINE\{keyName}";
            object value = Registry.GetValue(keyName, valueName, defaultValue: null);
            if (object.ReferenceEquals(value, null))
            {
                executionContext.Debug($"Key name '{keyName}', value name '{valueName}' is null.");
                return null;
            }

            executionContext.Debug($"Key name '{keyName}', value name '{valueName}': '{value}'");
            return value;
        }
    }
}