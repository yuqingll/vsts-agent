using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using Microsoft.VisualStudio.Services.Agent.PluginCore;
using Microsoft.TeamFoundation.DistributedTask.WebApi;

namespace Microsoft.VisualStudio.Services.Agent.PluginHost
{
    public static class Program
    {
        private static CancellationTokenSource tokenSource = new CancellationTokenSource();

        public static int Main(string[] args)
        {
            Console.CancelKeyPress += Console_CancelKeyPress;
            AssemblyLoadContext.Default.Resolving += ResolveAssembly;

            try
            {
                PluginUtil.NotNull(args, nameof(args));
                PluginUtil.Equal(2, args.Length, nameof(args.Length));

                string pluginType = args[0];
                if (string.Equals("task", pluginType, StringComparison.OrdinalIgnoreCase))
                {
                    string assemblyQualifiedName = args[1];
                    PluginUtil.NotNullOrEmpty(assemblyQualifiedName, nameof(assemblyQualifiedName));

                    string serializedContext = Console.ReadLine();
                    AgentTaskPluginExecutionContext executionContext = PluginUtil.ConvertFromJson<AgentTaskPluginExecutionContext>(serializedContext);

                    executionContext.Debug(assemblyQualifiedName);
                    executionContext.Debug(serializedContext);

                    Type type = Type.GetType(assemblyQualifiedName, throwOnError: true);
                    var taskPlugin = Activator.CreateInstance(type) as IAgentTaskPlugin;
                    PluginUtil.NotNull(taskPlugin, nameof(taskPlugin));

                    try
                    {
                        taskPlugin.RunAsync(executionContext, tokenSource.Token).GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        // any exception throw from plugin will fail the task.
                        executionContext.Fail(ex.ToString());
                    }
                    finally
                    {
                        AssemblyLoadContext.Default.Resolving -= ResolveAssembly;
                        Console.CancelKeyPress -= Console_CancelKeyPress;
                    }

                    return 0;
                }
                else if (string.Equals("command", pluginType, StringComparison.OrdinalIgnoreCase))
                {
                    string assemblyQualifiedName = args[1];
                    PluginUtil.NotNullOrEmpty(assemblyQualifiedName, nameof(assemblyQualifiedName));

                    string serializedContext = Console.ReadLine();
                    AgentCommandPluginExecutionContext executionContext = PluginUtil.ConvertFromJson<AgentCommandPluginExecutionContext>(serializedContext);

                    executionContext.Debug(assemblyQualifiedName);
                    executionContext.Debug(serializedContext);

                    Type type = Type.GetType(assemblyQualifiedName, throwOnError: true);
                    var commandPlugin = Activator.CreateInstance(type) as IAgentCommandPlugin;
                    PluginUtil.NotNull(commandPlugin, nameof(commandPlugin));

                    try
                    {
                        commandPlugin.ProcessCommandAsync(executionContext, tokenSource.Token).GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        // any exception throw from plugin will fail the task.
                        executionContext.Fail(ex.ToString());
                    }
                    finally
                    {
                        AssemblyLoadContext.Default.Resolving -= ResolveAssembly;
                        Console.CancelKeyPress -= Console_CancelKeyPress;
                    }

                    return 0;
                }
                else
                {
                    // infrastructure failure.
                    Console.Error.WriteLine(new ArgumentOutOfRangeException(pluginType).ToString());
                    return 1;
                }
            }
            finally
            {
                AssemblyLoadContext.Default.Resolving -= ResolveAssembly;
                Console.CancelKeyPress -= Console_CancelKeyPress;
            }
        }

        private static Assembly ResolveAssembly(AssemblyLoadContext context, AssemblyName assembly)
        {
            string assemblyFilename = assembly.Name + ".dll";
            return context.LoadFromAssemblyPath(Path.Combine(Directory.GetCurrentDirectory(), assemblyFilename));
        }

        private static void Console_CancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            tokenSource.Cancel();
        }
    }
}
