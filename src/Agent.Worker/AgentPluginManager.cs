using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Agent.PluginCore;
using Microsoft.VisualStudio.Services.Agent.Util;
using Microsoft.VisualStudio.Services.WebApi;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.VisualStudio.Services.Agent.Worker
{
    [ServiceLocator(Default = typeof(AgentPluginManager))]
    public interface IAgentPluginManager : IAgentService
    {
        Dictionary<Guid, Dictionary<string, Definition>> SupportedTasks { get; }
        Dictionary<string, Dictionary<string, string>> SupportedLoggingCommands { get; }

        Task RunPluginTaskAsync(IExecutionContext context, string plugin, Dictionary<string, string> inputs, string stage, EventHandler<ProcessDataReceivedEventArgs> outputHandler);
        void ProcessCommand(IExecutionContext context, Command command);
    }

    public sealed class AgentPluginManager : AgentService, IAgentPluginManager
    {
        private readonly Dictionary<string, Dictionary<string, string>> _supportedLoggingCommands = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<Guid, Dictionary<string, Definition>> _supportedTasks = new Dictionary<Guid, Dictionary<string, Definition>>();

        private readonly List<AgentPluginInfo> _taskPlugins = new List<AgentPluginInfo>()
        {
            new AgentPluginInfo()
            {
                AgentPluginAssembly = "Agent.RepositoryPlugin",
                AgentPluginEntryPoint = "Agent.RepositoryPlugin.CheckoutTask"
            }
        };

        private readonly List<AgentPluginInfo> _commandPlugins = new List<AgentPluginInfo>()
        {
            new AgentPluginInfo()
            {
                AgentPluginAssembly = "Agent.DropPlugin",
                AgentPluginEntryPoint = "Agent.DropPlugin.ArtifactUploadCommand"
            }
        };

        public Dictionary<string, Dictionary<string, string>> SupportedLoggingCommands => _supportedLoggingCommands;
        public Dictionary<Guid, Dictionary<string, Definition>> SupportedTasks => _supportedTasks;

        public override void Initialize(IHostContext hostContext)
        {
            base.Initialize(hostContext);

            // Load task plugins
            foreach (var plugin in _taskPlugins)
            {
                IAgentTaskPlugin taskPlugin = null;
                string typeName = $"{plugin.AgentPluginEntryPoint}, {plugin.AgentPluginAssembly}";
                AssemblyLoadContext.Default.Resolving += ResolveAssembly;
                try
                {
                    Type type = Type.GetType(typeName, throwOnError: true);
                    taskPlugin = Activator.CreateInstance(type) as IAgentTaskPlugin;
                }
                finally
                {
                    AssemblyLoadContext.Default.Resolving -= ResolveAssembly;
                }

                Util.ArgUtil.NotNull(taskPlugin, nameof(taskPlugin));
                Util.ArgUtil.NotNull(taskPlugin.Id, nameof(taskPlugin.Id));
                Util.ArgUtil.NotNullOrEmpty(taskPlugin.Version, nameof(taskPlugin.Version));
                if (!_supportedTasks.ContainsKey(taskPlugin.Id))
                {
                    _supportedTasks[taskPlugin.Id] = new Dictionary<string, Definition>(StringComparer.OrdinalIgnoreCase);
                }

                _supportedTasks[taskPlugin.Id][taskPlugin.Version] = new Definition() { Directory = HostContext.GetDirectory(WellKnownDirectory.Work) };
                _supportedTasks[taskPlugin.Id][taskPlugin.Version].Data = new DefinitionData()
                {
                    Author = taskPlugin.Author,
                    Description = taskPlugin.Description,
                    HelpMarkDown = taskPlugin.HelpMarkDown,
                    FriendlyName = taskPlugin.FriendlyName,
                    Inputs = taskPlugin.Inputs
                };

                if (taskPlugin.Stages.Contains("pre"))
                {
                    _supportedTasks[taskPlugin.Id][taskPlugin.Version].Data.PreJobExecution = new ExecutionData()
                    {
                        AgentPlugin = new AgentPluginHandlerData()
                        {
                            Target = typeName,
                            Stage = "pre"
                        }
                    };
                }

                if (taskPlugin.Stages.Contains("main"))
                {
                    _supportedTasks[taskPlugin.Id][taskPlugin.Version].Data.Execution = new ExecutionData()
                    {
                        AgentPlugin = new AgentPluginHandlerData()
                        {
                            Target = typeName,
                            Stage = "main"
                        }
                    };
                }

                if (taskPlugin.Stages.Contains("post"))
                {
                    _supportedTasks[taskPlugin.Id][taskPlugin.Version].Data.PostJobExecution = new ExecutionData()
                    {
                        AgentPlugin = new AgentPluginHandlerData()
                        {
                            Target = typeName,
                            Stage = "post"
                        }
                    };
                }
            }

            // Load command plugin
            foreach (var plugin in _commandPlugins)
            {
                IAgentCommandPlugin commandPlugin = null;
                string typeName = $"{plugin.AgentPluginEntryPoint}, {plugin.AgentPluginAssembly}";
                AssemblyLoadContext.Default.Resolving += ResolveAssembly;
                try
                {
                    Type type = Type.GetType(typeName, throwOnError: true);
                    commandPlugin = Activator.CreateInstance(type) as IAgentCommandPlugin;
                }
                finally
                {
                    AssemblyLoadContext.Default.Resolving -= ResolveAssembly;
                }

                Util.ArgUtil.NotNull(commandPlugin, nameof(commandPlugin));
                Util.ArgUtil.NotNullOrEmpty(commandPlugin.Area, nameof(commandPlugin.Area));
                Util.ArgUtil.NotNullOrEmpty(commandPlugin.Event, nameof(commandPlugin.Event));

                if (!_supportedLoggingCommands.ContainsKey(commandPlugin.Area))
                {
                    _supportedLoggingCommands[commandPlugin.Area] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
                _supportedLoggingCommands[commandPlugin.Area][commandPlugin.Event] = typeName;
            }
        }

        private Assembly ResolveAssembly(AssemblyLoadContext context, AssemblyName assembly)
        {
            string assemblyFilename = assembly.Name + ".dll";
            return context.LoadFromAssemblyPath(Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Bin), assemblyFilename));
        }

        public async Task RunPluginTaskAsync(IExecutionContext context, string plugin, Dictionary<string, string> inputs, string stage, EventHandler<ProcessDataReceivedEventArgs> outputHandler)
        {
            Util.ArgUtil.NotNullOrEmpty(plugin, nameof(plugin));

            // Resolve the working directory.
            string workingDirectory = HostContext.GetDirectory(WellKnownDirectory.Bin);
            Util.ArgUtil.Directory(workingDirectory, nameof(workingDirectory));

            // Agent.PluginHost
            string file = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Bin), $"Agent.PluginHost{Util.IOUtil.ExeExtension}");

            // Agent.PluginHost's arguments
            string arguments = $"task \"{plugin}\"";

            // construct plugin context
            AgentTaskPluginExecutionContext pluginContext = new AgentTaskPluginExecutionContext
            {
                Inputs = inputs,
                Stage = stage,
                Repositories = context.Repositories,
                Endpoints = context.Endpoints
            };
            // variables
            foreach (var publicVar in context.Variables.Public)
            {
                pluginContext.Variables[publicVar.Key] = publicVar.Value;
            }
            foreach (var publicVar in context.Variables.Private)
            {
                pluginContext.Variables[publicVar.Key] = new VariableValue(publicVar.Value, true);
            }
            // task variables (used by wrapper task)
            foreach (var publicVar in context.TaskVariables.Public)
            {
                pluginContext.TaskVariables[publicVar.Key] = publicVar.Value;
            }
            foreach (var publicVar in context.TaskVariables.Private)
            {
                pluginContext.TaskVariables[publicVar.Key] = new VariableValue(publicVar.Value, true);
            }

            using (var processInvoker = HostContext.CreateService<IProcessInvoker>())
            {
                processInvoker.OutputDataReceived += outputHandler;
                processInvoker.ErrorDataReceived += outputHandler;

                // Execute the process. Exit code 0 should always be returned.
                // A non-zero exit code indicates infrastructural failure.
                // Task failure should be communicated over STDOUT using ## commands.
                await processInvoker.ExecuteAsync(workingDirectory: workingDirectory,
                                                  fileName: file,
                                                  arguments: arguments,
                                                  environment: GetTaskPluginEnvironment(context),
                                                  requireExitCodeZero: true,
                                                  outputEncoding: null,
                                                  killProcessOnCancel: false,
                                                  contentsToStandardIn: new List<string>() { JsonUtility.ToString(pluginContext) },
                                                  cancellationToken: context.CancellationToken);
            }
        }

        public void ProcessCommand(IExecutionContext context, Command command)
        {
            // queue async command task to run agent plugin.
            context.Debug($"Process {command.Area}.{command.Event} command at backend.");
            var commandContext = HostContext.CreateService<IAsyncCommandContext>();
            commandContext.InitializeCommandContext(context, "Process Command");
            commandContext.Task = ProcessPluginCommandAsync(commandContext, command, context.CancellationToken);
            context.AsyncCommands.Add(commandContext);
        }

        private async Task ProcessPluginCommandAsync(IAsyncCommandContext context, Command command, CancellationToken token)
        {
            if (_supportedLoggingCommands.ContainsKey(command.Area) && _supportedLoggingCommands[command.Area].ContainsKey(command.Event))
            {
                var plugin = _supportedLoggingCommands[command.Area][command.Event];
                Util.ArgUtil.NotNullOrEmpty(plugin, nameof(plugin));

                // Resolve the working directory.
                string workingDirectory = HostContext.GetDirectory(WellKnownDirectory.Bin);
                Util.ArgUtil.Directory(workingDirectory, nameof(workingDirectory));

                // Agent.PluginHost
                string file = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Bin), $"Agent.PluginHost{Util.IOUtil.ExeExtension}");

                // Agent.PluginHost's arguments
                string arguments = $"command \"{plugin}\"";

                // construct plugin context
                AgentCommandPluginExecutionContext pluginContext = new AgentCommandPluginExecutionContext
                {
                    Data = command.Data,
                    Properties = command.Properties,
                    Repositories = context.RawContext.Repositories,
                    Endpoints = context.RawContext.Endpoints,
                };
                // variables
                foreach (var publicVar in context.RawContext.Variables.Public)
                {
                    pluginContext.Variables[publicVar.Key] = publicVar.Value;
                }
                foreach (var publicVar in context.RawContext.Variables.Private)
                {
                    pluginContext.Variables[publicVar.Key] = new VariableValue(publicVar.Value, true);
                }

                using (var processInvoker = HostContext.CreateService<IProcessInvoker>())
                {
                    object stderrLock = new object();
                    List<string> stderr = new List<string>();
                    processInvoker.OutputDataReceived += (object sender, ProcessDataReceivedEventArgs e) =>
                    {
                        context.Output(e.Data);
                    };
                    processInvoker.ErrorDataReceived += (object sender, ProcessDataReceivedEventArgs e) =>
                    {
                        lock (stderrLock)
                        {
                            stderr.Add(e.Data);
                        };
                    }; ;

                    // Execute the process. Exit code 0 should always be returned.
                    // A non-zero exit code indicates infrastructural failure.
                    // Task failure should be communicated over STDOUT using ## commands.
                    int returnCode = await processInvoker.ExecuteAsync(workingDirectory: workingDirectory,
                                                      fileName: file,
                                                      arguments: arguments,
                                                      environment: null,
                                                      requireExitCodeZero: false,
                                                      outputEncoding: null,
                                                      killProcessOnCancel: false,
                                                      contentsToStandardIn: new List<string>() { JsonUtility.ToString(pluginContext) },
                                                      cancellationToken: token);

                    if (returnCode != 0)
                    {
                        context.Output(string.Join(Environment.NewLine, stderr));
                        throw new ProcessExitCodeException(returnCode, file, arguments);
                    }
                    else if (stderr.Count > 0)
                    {
                        throw new InvalidOperationException(string.Join(Environment.NewLine, stderr));
                    }
                }
            }
            else
            {
                throw new NotSupportedException(command.ToString());
            }
        }

        private Dictionary<string, string> GetTaskPluginEnvironment(IExecutionContext context)
        {
            ArgUtil.NotNull(context, nameof(context));
            if (context.PrependPath.Count == 0)
            {
                return null;
            }

            // Prepend path.
            string prepend = string.Join(Path.PathSeparator.ToString(), context.PrependPath.Reverse<string>());
            string originalPath = context.Variables.Get(Constants.PathVariable) ?? // Prefer a job variable.
                System.Environment.GetEnvironmentVariable(Constants.PathVariable) ?? // Then an environment variable.
                string.Empty;
            string newPath = VarUtil.PrependPath(prepend, originalPath);

            Dictionary<string, string> env = new Dictionary<string, string>(VarUtil.EnvironmentVariableKeyComparer);
            env[Constants.PathVariable] = newPath;

            return env;
        }
    }

    public class AgentPluginInfo
    {
        public string AgentPluginAssembly { get; set; }
        public string AgentPluginEntryPoint { get; set; }
    }
}
