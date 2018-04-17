using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Services.Agent.PluginCore;

namespace Agent.RepositoryPlugin
{
    public class AgentRepositoryCheckoutPlugin : IAgentPlugin
    {
        public async Task RunAsync(AgentPluginExecutionContext executionContext, CancellationToken token)
        {
            // call different source provider base on repository type, then call GetSource() on the provider.
            executionContext.Debug("Get Source!!!!");
            var sourceProvider = new TfsGitSourceProvider();
            await sourceProvider.GetSourceAsync(executionContext, "self", token);
        }
    }

    public class AgentRepositoryCleanupPlugin : IAgentPlugin
    {
        public Task RunAsync(AgentPluginExecutionContext executionContext, CancellationToken token)
        {
            return Task.CompletedTask;
        }
    }
}
