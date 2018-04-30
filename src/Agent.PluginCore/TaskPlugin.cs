using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.OAuth;
using Microsoft.VisualStudio.Services.WebApi;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pipelines = Microsoft.TeamFoundation.DistributedTask.Pipelines;

namespace Agent.PluginCore
{
    public interface IAgentTaskPlugin
    {
        Guid Id { get; }
        string Version { get; }
        string FriendlyName { get; }
        string Description { get; }
        string HelpMarkDown { get; }
        string Author { get; }
        TaskInputDefinition[] Inputs { get; }
        HashSet<string> Stages { get; }
        Task RunAsync(AgentTaskPluginExecutionContext executionContext, CancellationToken token);
    }

    public class AgentTaskPluginExecutionContext
    {
        public AgentTaskPluginExecutionContext()
        {
            this.Endpoints = new List<ServiceEndpoint>();
            this.Inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            this.Repositories = new List<Pipelines.RepositoryResource>();
            this.TaskVariables = new Dictionary<string, VariableValue>(StringComparer.OrdinalIgnoreCase);
            this.Variables = new Dictionary<string, VariableValue>(StringComparer.OrdinalIgnoreCase);
        }

        public AgentCertificateSettings GetCertConfiguration()
        {
            bool skipCertValidation = PluginUtil.ConvertToBoolean(this.Variables.GetValueOrDefault("Agent.SkipCertValidation")?.Value);
            string caFile = this.Variables.GetValueOrDefault("Agent.CAInfo")?.Value;
            string clientCertFile = this.Variables.GetValueOrDefault("Agent.ClientCert")?.Value;

            if (!string.IsNullOrEmpty(caFile) || !string.IsNullOrEmpty(clientCertFile) || skipCertValidation)
            {
                var certConfig = new AgentCertificateSettings();
                certConfig.SkipServerCertificateValidation = skipCertValidation;
                certConfig.CACertificateFile = caFile;

                if (!string.IsNullOrEmpty(clientCertFile))
                {
                    certConfig.ClientCertificateFile = clientCertFile;
                    string clientCertKey = this.Variables.GetValueOrDefault("Agent.ClientCertKey")?.Value;
                    string clientCertArchive = this.Variables.GetValueOrDefault("Agent.ClientCertArchive")?.Value;
                    string clientCertPassword = this.Variables.GetValueOrDefault("Agent.ClientCertPassword")?.Value;

                    certConfig.ClientCertificatePrivateKeyFile = clientCertKey;
                    certConfig.ClientCertificateArchiveFile = clientCertArchive;
                    certConfig.ClientCertificatePassword = clientCertPassword;
                }

                return certConfig;
            }
            else
            {
                return null;
            }
        }

        public AgentWebProxySettings GetProxyConfiguration()
        {
            string proxyUrl = this.Variables.GetValueOrDefault("Agent.ProxyUrl")?.Value;
            if (!string.IsNullOrEmpty(proxyUrl))
            {
                string proxyUsername = this.Variables.GetValueOrDefault("Agent.ProxyUsername")?.Value;
                string proxyPassword = this.Variables.GetValueOrDefault("Agent.ProxyPassword")?.Value;
                List<string> proxyBypassHosts = PluginUtil.ConvertFromJson<List<string>>(this.Variables.GetValueOrDefault("Agent.ProxyBypassList")?.Value ?? "[]");
                return new AgentWebProxySettings()
                {
                    ProxyAddress = proxyUrl,
                    ProxyUsername = proxyUsername,
                    ProxyPassword = proxyPassword,
                    ProxyBypassList = proxyBypassHosts,
                };
            }
            else
            {
                return null;
            }
        }

        public string Stage { get; set; }
        public List<ServiceEndpoint> Endpoints { get; set; }
        public List<Pipelines.RepositoryResource> Repositories { get; set; }
        public Dictionary<string, VariableValue> Variables { get; set; }
        public Dictionary<string, VariableValue> TaskVariables { get; set; }
        public Dictionary<string, string> Inputs { get; set; }

        public string GetInput(string name, bool required = false)
        {
            string value = null;
            if (this.Inputs.ContainsKey(name))
            {
                value = this.Inputs[name];
            }

            if (string.IsNullOrEmpty(value) && required)
            {
                throw new ArgumentNullException(name);
            }

            return value;
        }

        public void Fail(string message)
        {
            if (!string.IsNullOrEmpty(message))
            {
                Error(message);
            }

            Console.WriteLine($"##vso[task.complete result=Failed;]");
        }

        public void Debug(string message)
        {
            Console.WriteLine($"##vso[task.debug]{message}");
        }

        public void Error(string message)
        {
            Console.WriteLine($"##vso[task.logissue type=error;]{message}");
        }

        public void Error(Exception exception)
        {
            Error(exception.ToString());
        }

        public void Warning(string message)
        {
            Console.WriteLine($"##vso[task.logissue type=warning;]{message}");
        }

        public void Output(string message)
        {
            Console.WriteLine(message);
        }

        public void Progress(int progress, string operation)
        {
            Console.WriteLine($"##vso[task.setprogress value={progress}]{operation}");
        }

        public void SetSecret(string secret)
        {
            Console.WriteLine($"##vso[task.setsecret]{secret}");
        }

        public void SetVariable(string variable, string value, bool isSecret = false)
        {
            this.Variables[variable] = new VariableValue(value, isSecret);
            Console.WriteLine($"##vso[task.setvariable variable={variable};issecret={isSecret.ToString()};]{value}");
        }

        public void SetTaskVariable(string variable, string value, bool isSecret = false)
        {
            this.TaskVariables[variable] = new VariableValue(value, isSecret);
            Console.WriteLine($"##vso[task.settaskvariable variable={variable};issecret={isSecret.ToString()};]{value}");
        }

        public void Command(string command)
        {
            Console.WriteLine($"##[command]{command}");
        }
    }
}
