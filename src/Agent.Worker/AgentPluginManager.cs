using Microsoft.TeamFoundation.DistributedTask.WebApi;
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
        Dictionary<Guid, Definition> SupportedTasks { get; }
        Dictionary<string, HashSet<string>> SupportedLoggingCommands { get; }
        void ProcessCommand(IExecutionContext context, Command command);
    }

    public sealed class AgentPluginManager : AgentService, IAgentPluginManager
    {
        private readonly Dictionary<string, HashSet<string>> _supportedLoggingCommands = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<Guid, Definition> _supportedTasks = new Dictionary<Guid, Definition>();
        private readonly Dictionary<string, Dictionary<string, AgentPluginInfo>> _loggingCommandAgentPlugins = new Dictionary<string, Dictionary<string, AgentPluginInfo>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<Guid, AgentPluginInfo> _taskAgentPlugins = new Dictionary<Guid, AgentPluginInfo>();
        public Dictionary<string, HashSet<string>> SupportedLoggingCommands => _supportedLoggingCommands;
        public Dictionary<Guid, Definition> SupportedTasks => _supportedTasks;

        public override void Initialize(IHostContext hostContext)
        {
            base.Initialize(hostContext);
            //_supportedTasks[WellKnownAgentPluginTasks.CheckoutTaskId];
            _supportedLoggingCommands["artifact"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "upload" };
            _loggingCommandAgentPlugins["artifact"] = new Dictionary<string, AgentPluginInfo>(StringComparer.OrdinalIgnoreCase)
            {
                {
                    "upload",
                    new AgentPluginInfo()
                    {
                        AgentPluginPath = Path.Combine(hostContext.GetDirectory(WellKnownDirectory.Bin), $"Agent.DropPlugin.dll"),
                        AgentPluginEntryPoint = "AgentDropUploadPlugin"
                    }
                }
            };

            _taskAgentPlugins[WellKnownAgentPluginTasks.CheckoutTaskId] = new AgentPluginInfo()
            {
                AgentPluginPath = Path.Combine(hostContext.GetDirectory(WellKnownDirectory.Bin), $"Agent.RepositoryPlugin.dll"),
                AgentPluginEntryPoint = "CheckoutTask"
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

    public static class WellKnownAgentPluginTasks
    {
        public static Guid CheckoutTaskId = new Guid("c61807ba-5e20-4b70-bd8c-3683c9f74003");

        public static Definition CheckoutTask = new Definition()
        {
            Directory = string.Empty,
            Data = new DefinitionData()
            {
                Author = "Ting",
                Description = "Checkout",
                HelpMarkDown = "Call Ting",
                FriendlyName = "Get Source",
                Inputs = new TaskInputDefinition[]
                {
                    new TaskInputDefinition()
                    {
                        Name="repository",
                        InputType = TaskInputType.String,
                        DefaultValue="self",
                        Required=true
                    }
                },
                Execution = new ExecutionData()
                {
                    AgentPlugin = new AgentPluginHandlerData()
                    {
                        Target = "Agent.RepositoryPlugin.dll",
                        EntryPoint = "AgentRepositoryCheckoutPlugin"
                    }
                },
                PostJobExecution = new ExecutionData()
                {
                    AgentPlugin = new AgentPluginHandlerData()
                    {
                        Target = "Agent.RepositoryPlugin.dll",
                        EntryPoint = "AgentRepositoryCleanupPlugin"
                    }
                }
            }
        };
    }
}
