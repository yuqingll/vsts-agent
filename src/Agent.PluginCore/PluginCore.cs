using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.VisualStudio.Services.Agent.PluginCore
{
    public interface IAgentPlugin
    {
        Task RunAsync(AgentPluginExecutionContext executionContext, CancellationToken token);
    }

    public class AgentPluginExecutionContext
    {
        public void Debug(string message)
        {
            Console.WriteLine($"##vso[task.debug]{message}");
        }
    }

    public static class ArgUtil
    {
        // public static void Directory(string directory, string name)
        // {
        //     ArgUtil.NotNullOrEmpty(directory, name);
        //     if (!System.IO.Directory.Exists(directory))
        //     {
        //         throw new DirectoryNotFoundException(
        //             message: StringUtil.Loc("DirectoryNotFound", directory));
        //     }
        // }

        public static void Equal<T>(T expected, T actual, string name)
        {
            if (object.ReferenceEquals(expected, actual))
            {
                return;
            }

            if (object.ReferenceEquals(expected, null) ||
                !expected.Equals(actual))
            {
                throw new ArgumentOutOfRangeException(
                    paramName: name,
                    actualValue: actual,
                    message: $"{name} does not equal expected value. Expected '{expected}'. Actual '{actual}'.");
            }
        }

        public static void File(string fileName, string name)
        {
            ArgUtil.NotNullOrEmpty(fileName, name);
            if (!System.IO.File.Exists(fileName))
            {
                throw new FileNotFoundException(fileName);
            }
        }

        public static void NotNull(object value, string name)
        {
            if (object.ReferenceEquals(value, null))
            {
                throw new ArgumentNullException(name);
            }
        }

        public static void NotNullOrEmpty(string value, string name)
        {
            if (string.IsNullOrEmpty(value))
            {
                throw new ArgumentNullException(name);
            }
        }

        // public static void NotEmpty(Guid value, string name)
        // {
        //     if (value == Guid.Empty)
        //     {
        //         throw new ArgumentNullException(name);
        //     }
        // }

        public static void Null(object value, string name)
        {
            if (!object.ReferenceEquals(value, null))
            {
                throw new ArgumentException(message: $"{name} should be null.", paramName: name);
            }
        }
    }
}
