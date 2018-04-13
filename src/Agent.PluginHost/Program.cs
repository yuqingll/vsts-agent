using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;

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
            //TaskLib taskContext = new TaskLib();
            //taskContext.Initialize();
            try
            {
                // ArgUtil.NotNull(args, nameof(args));
                // ArgUtil.Equal(2, args.Length, nameof(args.Length));

                assemblyPath = args[0];
                string entryPoint = args[1];
                // taskContext.Debug(assemblyPath);
                // taskContext.Debug(typeName);

                // ArgUtil.File(assemblyPath, nameof(assemblyPath));
                // ArgUtil.NotNullOrEmpty(typeName, nameof(typeName));

                Assembly taskAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
                //ArgUtil.NotNull(taskAssembly, nameof(taskAssembly));

                // Type type = taskAssembly.GetType(typeName, throwOnError: true);
                // var vstsTask = Activator.CreateInstance(type) as ITask;
                // ArgUtil.NotNull(vstsTask, nameof(vstsTask));

                // vstsTask.RunAsync(taskContext, tokenSource.Token).GetAwaiter().GetResult();
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
