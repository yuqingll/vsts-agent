using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Agent.PluginCore;
using Pipelines = Microsoft.TeamFoundation.DistributedTask.Pipelines;

namespace Agent.RepositoryPlugin
{
    public interface ISourceProvider
    {
        Task GetSourceAsync(AgentTaskPluginExecutionContext executionContext, Pipelines.RepositoryResource repository, CancellationToken cancellationToken);

        Task PostJobCleanupAsync(AgentTaskPluginExecutionContext executionContext, Pipelines.RepositoryResource repository);
    }

    public class CheckoutTask : IAgentTaskPlugin
    {
        public string FriendlyName => "Get Sources";
        public string Version => "1.0.0";
        public string Description => "Get Sources";
        public string HelpMarkDown => "";
        public string Author => "Microsoft";

        public TaskInputDefinition[] Inputs => new TaskInputDefinition[] {
            new TaskInputDefinition()
            {
                Name="repository",
                InputType = TaskInputType.String,
                DefaultValue="self",
                Required=true
            }
        };

        public HashSet<string> Stages => new HashSet<string>() { "main", "post" };

        public async Task RunAsync(AgentTaskPluginExecutionContext executionContext, CancellationToken token)
        {
            var repoAlias = executionContext.GetInput("repository", true);
            var repo = executionContext.Repositories.Single(x => string.Equals(x.Alias, repoAlias, StringComparison.OrdinalIgnoreCase));
            ISourceProvider sourceProvider = null;
            switch (repo.Type)
            {
                case RepositoryTypes.Bitbucket:
                case RepositoryTypes.GitHub:
                case RepositoryTypes.GitHubEnterprise:
                    sourceProvider = new AuthenticatedGitSourceProvider();
                    break;
                case RepositoryTypes.Git:
                    sourceProvider = new ExternalGitSourceProvider();
                    break;
                case RepositoryTypes.TfsGit:
                    sourceProvider = new TfsGitSourceProvider();
                    break;
                case RepositoryTypes.TfsVersionControl:
                    //sourceProvider = new BitbucketSourceProvider();
                    break;
                case RepositoryTypes.Svn:
                    //sourceProvider = new BitbucketSourceProvider();
                    break;
                default:
                    throw new NotSupportedException(repo.Type);
            }

            if (executionContext.Stage == "main")
            {
                await sourceProvider.GetSourceAsync(executionContext, repo, token);
            }
            else if (executionContext.Stage == "post")
            {
                await sourceProvider.PostJobCleanupAsync(executionContext, repo);
            }
        }
    }
}
