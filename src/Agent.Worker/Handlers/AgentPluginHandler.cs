using Microsoft.VisualStudio.Services.Agent.Util;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Linq;
using System;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.Services.Agent.Worker.Container;
using Microsoft.VisualStudio.Services.Agent.PluginCore;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.WebApi;

namespace Microsoft.VisualStudio.Services.Agent.Worker.Handlers
{
    public static class AgentPluginWhiteLists
    {
        public static string RepositoryPlugin = "Agent.RepositoryPlugin.dll";
    }

    [ServiceLocator(Default = typeof(AgentPluginHandler))]
    public interface IAgentPluginHandler : IHandler
    {
        AgentPluginHandlerData Data { get; set; }
    }

    public sealed class AgentPluginHandler : Handler, IAgentPluginHandler
    {
        public AgentPluginHandlerData Data { get; set; }

        public async Task RunAsync()
        {
            // Validate args.
            Trace.Entering();
            Util.ArgUtil.NotNull(Data, nameof(Data));
            Util.ArgUtil.NotNull(ExecutionContext, nameof(ExecutionContext));
            Util.ArgUtil.NotNull(Inputs, nameof(Inputs));

            // Resolve the target assembly.
            string target = Data.Target;
            Util.ArgUtil.NotNullOrEmpty(target, nameof(target));
            if (!string.Equals(target, AgentPluginWhiteLists.RepositoryPlugin, StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException(target);
            }
            target = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Bin), target);
            Util.ArgUtil.File(target, nameof(target));

            string entryPoint = Data.EntryPoint;
            Util.ArgUtil.NotNullOrEmpty(entryPoint, nameof(entryPoint));

            // Resolve the working directory.
            string workingDirectory = HostContext.GetDirectory(WellKnownDirectory.Work);
            Util.ArgUtil.Directory(workingDirectory, nameof(workingDirectory));

            // Agent.PluginHost
            string file = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Bin), $"Agent.PluginHost{Util.IOUtil.ExeExtension}");

            // Agent.PluginHost's arguments
            string arguments = $"\"{target.Replace("\"", "\\\"")}\" {entryPoint}";

            // AgentPluginExecutionContext
            AgentPluginExecutionContext context = new AgentPluginExecutionContext
            {
                Repositories = ExecutionContext.Repositories,
                Endpoints = ExecutionContext.Endpoints
            };
            foreach (var publicVar in ExecutionContext.Variables.Public)
            {
                context.Variables[publicVar.Key] = publicVar.Value;
            }
            foreach (var publicVar in ExecutionContext.Variables.Private)
            {
                context.Variables[publicVar.Key] = new VariableValue(publicVar.Value, true);
            }

            using (var processInvoker = HostContext.CreateService<IProcessInvoker>())
            {
                processInvoker.OutputDataReceived += OnDataReceived;
                processInvoker.ErrorDataReceived += OnDataReceived;

                // Execute the process. Exit code 0 should always be returned.
                // A non-zero exit code indicates infrastructural failure.
                // Task failure should be communicated over STDOUT using ## commands.
                await processInvoker.ExecuteAsync(workingDirectory: workingDirectory,
                                                  fileName: file,
                                                  arguments: arguments,
                                                  environment: null,
                                                  requireExitCodeZero: true,
                                                  outputEncoding: null,
                                                  killProcessOnCancel: false,
                                                  contentsToStandardIn: new List<string>() { JsonUtility.ToString(context) },
                                                  cancellationToken: ExecutionContext.CancellationToken);
            }
        }

        private void OnDataReceived(object sender, ProcessDataReceivedEventArgs e)
        {
            // This does not need to be inside of a critical section.
            // The logging queues and command handlers are thread-safe.
            if (!CommandManager.TryProcessCommand(ExecutionContext, e.Data))
            {
                ExecutionContext.Output(e.Data);
            }
        }
    }
}
