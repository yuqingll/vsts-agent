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

namespace Agent.DropPlugin
{
    public class ArtifactUploadCommand : IAgentCommandPlugin
    {
        public Task ProcessCommandAsync(AgentCommandPluginExecutionContext executionContext, CancellationToken token)
        {
            executionContext.Output("Upload Drop!!!");
            executionContext.Debug(executionContext.Properties.Count.ToString());
            executionContext.Debug(executionContext.Data);

            //executionContext.Fail(new InvalidOperationException("SomethingHappened!").ToString());

            return Task.CompletedTask;
        }
    }
}
