using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Pipelines = Microsoft.TeamFoundation.DistributedTask.Pipelines;
using Newtonsoft.Json;
using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Collections.Generic;
using System.Linq;

namespace Microsoft.VisualStudio.Services.Agent.Worker.Build
{
    public sealed class ResourceTrackingConfig
    {
        public ResourceTrackingConfig()
        { }

        public ResourceTrackingConfig(IExecutionContext executionContext, string resourcesRoot)
        {
            // tracking repository resources
            if (executionContext.Repositories.Count > 0)
            {
                SourcesDirectory = Path.Combine(resourcesRoot, Constants.Resource.Path.SourcesDirectory);  // 1/s dir

                // if there is one repository, we will keep using the layout format we have today, _work/1/s 
                // if there are multiple repositories, we will put each repository under the sub-dir of its alias, _work/1/s/self
                if (executionContext.Repositories.Count == 1)
                {
                    var repo = executionContext.Repositories[0];
                    Repositories[repo.Alias] = new RepositoryTrackingConfig()
                    {
                        RepositoryType = repo.Type,
                        RepositoryUrl = repo.Url.AbsoluteUri,
                        SourceDirectory = SourcesDirectory
                    };

                    if (repo.Properties.Get<bool>("sharerepository"))
                    {
                        throw new NotSupportedException("sharerepository");
                    }
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
                            SourceDirectory = Path.Combine(SourcesDirectory, repo.Alias)
                        };
                    }
                }
            }

            // tracking build drop resources
            if (executionContext.Builds.Count > 0)
            {
                DropsDirectory = Path.Combine(resourcesRoot, Constants.Resource.Path.DropsDirectory);  // 1/d dir
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
                            DropDirectory = Path.Combine(SourcesDirectory, build.Alias)
                        };
                    }
                }
            }
        }

        private Dictionary<string, RepositoryTrackingConfig> _repositories;
        private Dictionary<string, DropTrackingConfig> _drops;

        [JsonProperty("build_sourcesdirectory")]
        public string SourcesDirectory { get; set; }

        [JsonProperty("build_dropsdirectory")]
        public string DropsDirectory { get; set; }

        [JsonProperty("build_repositories")]
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

        [JsonProperty("build_drops")]
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

    public sealed class TrackingConfig : TrackingConfigBase
    {
        public const string FileFormatVersionJsonProperty = "fileFormatVersion";

        // The parameterless constructor is required for deserialization.
        public TrackingConfig()
        {
        }

        public TrackingConfig(
            IExecutionContext executionContext,
            LegacyTrackingConfig copy,
            string sourcesDirectoryNameOnly,
            string repositoryType,
            bool useNewArtifactsDirectoryName = false)
        {
            // Set the directories.
            BuildDirectory = Path.GetFileName(copy.BuildDirectory); // Just take the portion after _work folder.
            string artifactsDirectoryNameOnly =
                useNewArtifactsDirectoryName ? Constants.Build.Path.ArtifactsDirectory : Constants.Build.Path.LegacyArtifactsDirectory;
            ArtifactsDirectory = Path.Combine(BuildDirectory, artifactsDirectoryNameOnly);
            SourcesDirectory = Path.Combine(BuildDirectory, sourcesDirectoryNameOnly);
            TestResultsDirectory = Path.Combine(BuildDirectory, Constants.Build.Path.TestResultsDirectory);

            // Set the other properties.
            CollectionId = copy.CollectionId;
            CollectionUrl = executionContext.Variables.System_TFCollectionUrl;
            DefinitionId = copy.DefinitionId;
            HashKey = copy.HashKey;
            RepositoryUrl = copy.RepositoryUrl;
            System = copy.System;
        }

        public TrackingConfig(IExecutionContext executionContext, ServiceEndpoint endpoint, int buildDirectory, string hashKey)
        {
            // Set the directories.
            BuildDirectory = buildDirectory.ToString(CultureInfo.InvariantCulture);
            ArtifactsDirectory = Path.Combine(BuildDirectory, Constants.Build.Path.ArtifactsDirectory);
            SourcesDirectory = Path.Combine(BuildDirectory, Constants.Build.Path.SourcesDirectory);
            TestResultsDirectory = Path.Combine(BuildDirectory, Constants.Build.Path.TestResultsDirectory);

            // Set the other properties.
            CollectionId = executionContext.Variables.System_CollectionId;
            DefinitionId = executionContext.Variables.System_DefinitionId;
            HashKey = hashKey;
            RepositoryUrl = endpoint.Url.AbsoluteUri;
            System = BuildSystem;
            UpdateJobRunProperties(executionContext);
        }

        // Convert 1.x Legacy tracking file
        public TrackingConfig(
            IExecutionContext executionContext,
            LegacyTrackingConfig copy,
            string sourcesDirectoryNameOnly,
            bool useNewArtifactsDirectoryName = false)
        {
            // Set the directories.
            BuildDirectory = Path.GetFileName(copy.BuildDirectory); // Just take the portion after _work folder.
            string artifactsDirectoryNameOnly =
                useNewArtifactsDirectoryName ? Constants.Build.Path.ArtifactsDirectory : Constants.Build.Path.LegacyArtifactsDirectory;
            ArtifactsDirectory = Path.Combine(BuildDirectory, artifactsDirectoryNameOnly);
            SourcesDirectory = Path.Combine(BuildDirectory, sourcesDirectoryNameOnly);
            TestResultsDirectory = Path.Combine(BuildDirectory, Constants.Build.Path.TestResultsDirectory);

            // Set the other properties.
            CollectionId = copy.CollectionId;
            CollectionUrl = executionContext.Variables.System_TFCollectionUrl;
            DefinitionId = copy.DefinitionId;
            HashKey = copy.HashKey;
            System = copy.System;
        }

        public TrackingConfig(
            IExecutionContext executionContext,
            int buildDirectory,
            string hashKey)
        {
            // Set the directories.
            BuildDirectory = buildDirectory.ToString(CultureInfo.InvariantCulture);
            ArtifactsDirectory = Path.Combine(BuildDirectory, Constants.Build.Path.ArtifactsDirectory);
            TestResultsDirectory = Path.Combine(BuildDirectory, Constants.Build.Path.TestResultsDirectory);
            Resources = new ResourceTrackingConfig(executionContext, BuildDirectory);

            // Set the other properties.
            CollectionId = executionContext.Variables.System_CollectionId;
            DefinitionId = executionContext.Variables.System_DefinitionId;
            HashKey = hashKey;
            System = BuildSystem;
            UpdateJobRunProperties(executionContext);
        }

        [JsonProperty("build_artifactstagingdirectory")]
        public string ArtifactsDirectory { get; set; }

        [JsonProperty("agent_builddirectory")]
        public string BuildDirectory { get; set; }

        public string CollectionUrl { get; set; }

        public string DefinitionName { get; set; }

        [JsonProperty(FileFormatVersionJsonProperty)]
        public int FileFormatVersion
        {
            get
            {
                return 4;
            }

            set
            {
                // Version 4 changes:
                //   Multi-type resources tracking support was added.
                // Version 3 changes:
                //   CollectionName was removed.
                //   CollectionUrl was added.
                switch (value)
                {
                    case 4:
                    case 3:
                    case 2:
                        break;
                    default:
                        // Should never reach here.
                        throw new NotSupportedException();
                }
            }
        }

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

        public string RepositoryType { get; set; }

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

        [JsonProperty("build_sourcesdirectory")]
        public string SourcesDirectory { get; set; }

        [JsonProperty("common_testresultsdirectory")]
        public string TestResultsDirectory { get; set; }

        [JsonProperty("build_resources")]
        public ResourceTrackingConfig Resources { get; set; }

        public void UpdateJobRunProperties(IExecutionContext executionContext)
        {
            CollectionUrl = executionContext.Variables.System_TFCollectionUrl;
            DefinitionName = executionContext.Variables.Build_DefinitionName;
            LastRunOn = DateTimeOffset.Now;
        }
    }
}