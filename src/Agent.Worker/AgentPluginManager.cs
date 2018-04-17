using Microsoft.VisualStudio.Services.Agent.Util;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Microsoft.VisualStudio.Services.Agent.Worker
{
    [ServiceLocator(Default = typeof(AgentPluginManager))]
    public interface IAgentPluginManager : IAgentService
    {
        Dictionary<string, HashSet<string>> SupportedLoggingCommands { get; }
        void ProcessCommand(IExecutionContext context, Command command);
    }

    public sealed class AgentPluginManager : AgentService, IAgentPluginManager
    {
        private readonly Dictionary<string, HashSet<string>> _supportedLoggingCommands = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, AgentPluginInfo>> _loggingCommandAgentPlugins = new Dictionary<string, Dictionary<string, AgentPluginInfo>>(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, HashSet<string>> SupportedLoggingCommands => _supportedLoggingCommands;

        public override void Initialize(IHostContext hostContext)
        {
            base.Initialize(hostContext);
            _supportedLoggingCommands["artifact"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "upload" };
            _loggingCommandAgentPlugins["artifact"] = new Dictionary<string, AgentPluginInfo>(StringComparer.OrdinalIgnoreCase)
            {
                {
                    "upload",
                    new AgentPluginInfo()
                    {
                        AgentPluginPath = Path.Combine(hostContext.GetDirectory(WellKnownDirectory.Bin), $"Agent.DropPlugin{IOUtil.ExeExtension}"),
                        AgentPluginEntryPoint = "AgentDropUploadPlugin"
                    }
                }
            };
        }


        public void ProcessCommand(IExecutionContext context, Command command)
        {
            if (_loggingCommandAgentPlugins.ContainsKey(command.Area) && _loggingCommandAgentPlugins[command.Area].ContainsKey(command.Event))
            {
                var plugin = _loggingCommandAgentPlugins[command.Area][command.Event];

                // queue async command task to associate artifact.
                //context.Debug($"Associate artifact: {artifactName} with build: {buildId.Value} at backend.");
                // var commandContext = HostContext.CreateService<IAsyncCommandContext>();
                // commandContext.InitializeCommandContext(context, StringUtil.Loc("AssociateArtifact"));
                // commandContext.Task = AssociateArtifactAsync(commandContext,
                //                                              WorkerUtilities.GetVssConnection(context),
                //                                              projectId,
                //                                              buildId.Value,
                //                                              artifactName,
                //                                              artifactType,
                //                                              artifactData,
                //                                              propertyDictionary,
                //                                              context.CancellationToken);
                // context.AsyncCommands.Add(commandContext);

            }
            else
            {
                throw new NotSupportedException(command.ToString());
            }
        }

        // public async Task ExecuteAgentPluginAsync()
        // { 

        // }
    }

    public class AgentPluginInfo
    {
        public string AgentPluginPath { get; set; }
        public string AgentPluginEntryPoint { get; set; }
    }
}
