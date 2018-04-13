using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using Microsoft.VisualStudio.Services.Agent.PluginCore;

namespace Microsoft.VisualStudio.Services.Agent.PluginHost
{
    public static class Program
    {
        private static CancellationTokenSource tokenSource = new CancellationTokenSource();
        private static string assemblyPath;

        public static void Main(string[] args)
        {
            Console.CancelKeyPress += Console_CancelKeyPress;
            AssemblyLoadContext.Default.Resolving += ResolveAssembly;

            AgentPluginExecutionContext executionContext = new AgentPluginExecutionContext();
            //TaskLib taskContext = new TaskLib();
            //taskContext.Initialize();
            try
            {
                ArgUtil.NotNull(args, nameof(args));
                ArgUtil.Equal(2, args.Length, nameof(args.Length));

                assemblyPath = args[0];
                ArgUtil.File(assemblyPath, nameof(assemblyPath));

                string entryPoint = args[1];
                ArgUtil.NotNullOrEmpty(entryPoint, nameof(entryPoint));

                executionContext.Debug(assemblyPath);
                executionContext.Debug(entryPoint);

                Assembly pluginAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
                ArgUtil.NotNull(pluginAssembly, nameof(pluginAssembly));

                IAgentPlugin agentPlugin = pluginAssembly.CreateInstance($"{pluginAssembly.GetName().Name}.{entryPoint}", true) as IAgentPlugin;
                ArgUtil.NotNull(agentPlugin, nameof(agentPlugin));

                agentPlugin.RunAsync(executionContext, tokenSource.Token).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                // if (ex.InnerException != null)
                // {
                //     taskContext.Debug(ex.InnerException.ToString());
                // }

                // taskContext.SetTaskResult("Failed", ex.ToString());
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
            return context.LoadFromAssemblyPath(Path.Combine(Path.GetDirectoryName(assemblyPath), assemblyFilename));
        }

        private static void Console_CancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            tokenSource.Cancel();
        }
    }
}
