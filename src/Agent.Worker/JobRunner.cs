using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Pipelines = Microsoft.TeamFoundation.DistributedTask.Pipelines;
using Microsoft.VisualStudio.Services.Agent.Util;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using System.Text;
using System.IO.Compression;
using Microsoft.VisualStudio.Services.Agent.Worker.Build;
using System.Diagnostics;

namespace Microsoft.VisualStudio.Services.Agent.Worker
{
    [ServiceLocator(Default = typeof(JobRunner))]
    public interface IJobRunner : IAgentService
    {
        Task<TaskResult> RunAsync(Pipelines.AgentJobRequestMessage message, CancellationToken jobRequestCancellationToken);
    }

    public sealed class JobRunner : AgentService, IJobRunner
    {
        private IJobServerQueue _jobServerQueue;
        private ITempDirectoryManager _tempDirectoryManager;

        public async Task<TaskResult> RunAsync(Pipelines.AgentJobRequestMessage message, CancellationToken jobRequestCancellationToken)
        {
            // Validate parameters.
            Trace.Entering();
            ArgUtil.NotNull(message, nameof(message));
            ArgUtil.NotNull(message.Resources, nameof(message.Resources));
            ArgUtil.NotNull(message.Variables, nameof(message.Variables));
            ArgUtil.NotNull(message.Steps, nameof(message.Steps));
            Trace.Info("Job ID {0}", message.JobId);

            DateTime jobStartTimeUtc = DateTime.UtcNow;

            var tfsGit1 = new Pipelines.RepositoryResource()
            {
                Endpoint = new Pipelines.ServiceEndpointReference() { Name = WellKnownServiceEndpointNames.SystemVssConnection },
                Id = "11e2c5c2-0b38-4b8d-965d-1e1f275eb20a",
                Alias = "self",
                Type = "TfsGit",
                Url = new Uri("https://tingweu.visualstudio.com/_git/g"),
                Version = "878ca37d598e35c6a1e2ec678e5355682d51dde1",
            };
            tfsGit1.Properties.Set<bool>("clean", false);
            tfsGit1.Properties.Set<string>("branch", "master");
            tfsGit1.Properties.Set<string>("name", "g");
            // gated check-in stuff

            // var tfsGit2 = new Pipelines.RepositoryResource()
            // {
            //     Endpoint = new Pipelines.ServiceEndpointReference() { Name = WellKnownServiceEndpointNames.SystemVssConnection },
            //     Id = "ba188439-b224-4878-9c32-f6c39fcada03",
            //     Alias = "agent",
            //     Type = "TfsGit",
            //     Url = new Uri("https://tingweu.visualstudio.com/g/_git/vsts-agent"),
            //     Version = "67f37676dd69b3f96e138121d60a680f3726985a",
            // };
            // tfsGit2.Properties.Set<bool>("clean", true);
            // tfsGit2.Properties.Set<string>("branch", "master");
            // tfsGit2.Properties.Set<string>("name", "vsts-agent");

            var checkoutTask1 = new Pipelines.TaskStep()
            {
                Condition = "succeeded()",
                ContinueOnError = false,
                Name = "checkout",
                Id = Guid.NewGuid(),
                DisplayName = "GetSource",
                Enabled = true,
                Inputs = {
                    {
                        "Repository", "self"
                    },
                    {
                        "clean", "true"
                    }
                },
                Reference = new Pipelines.TaskStepDefinitionReference()
                {
                    Id = new Guid("c61807ba-5e20-4b70-bd8c-3683c9f74003"),
                    Name = "checkout",
                    Version = "1.0.0"
                }
            };

            //var checkoutTask2 = new Pipelines.TaskStep()
            // {
            //     Condition = "succeeded()",
            //     ContinueOnError = false,
            //     Name = "checkout",
            //     Id = Guid.NewGuid(),
            //     DisplayName = "GetSource",
            //     Enabled = true,
            //     Inputs = {
            //         {
            //             "Repository", "agent"
            //         },
            //         {
            //             "clean", "true"
            //         }
            //     },
            //     Reference = new Pipelines.TaskStepDefinitionReference()
            //     {
            //         Id = WellKnownAgentPluginTasks.CheckoutTaskId,
            //         Name = "checkout",
            //         Version = "1.0.0"
            //     }
            // };

            message.Resources.Repositories.Add(tfsGit1);
            //message.Resources.Repositories.Add(tfsGit2);
            message.Steps.Insert(0, checkoutTask1);
            //message.Steps.Add(checkoutTask2);
            //message.Resources.Endpoints.RemoveAt(0);

            // Agent.RunMode
            RunMode runMode;
            if (message.Variables.ContainsKey(Constants.Variables.Agent.RunMode) &&
                Enum.TryParse(message.Variables[Constants.Variables.Agent.RunMode].Value, ignoreCase: true, result: out runMode) &&
                runMode == RunMode.Local)
            {
                HostContext.RunMode = runMode;
            }

            ServiceEndpoint systemConnection = message.Resources.Endpoints.Single(x => string.Equals(x.Name, WellKnownServiceEndpointNames.SystemVssConnection, StringComparison.OrdinalIgnoreCase));

            // System.AccessToken
            if (message.Variables.ContainsKey(Constants.Variables.System.EnableAccessToken) &&
                StringUtil.ConvertToBoolean(message.Variables[Constants.Variables.System.EnableAccessToken].Value))
            {
                message.Variables[Constants.Variables.System.AccessToken] = new VariableValue(systemConnection.Authorization.Parameters["AccessToken"], false);
            }

            // back compat TfsServerUrl
            message.Variables[Constants.Variables.System.TFServerUrl] = systemConnection.Url.AbsoluteUri;

            // Make sure SystemConnection Url and Endpoint Url match Config Url base for OnPremises server
            if (!message.Variables.ContainsKey(Constants.Variables.System.ServerType))
            {
                if (!UrlUtil.IsHosted(systemConnection.Url.AbsoluteUri)) // TODO: remove this after TFS/RM move to M133
                {
                    ReplaceConfigUriBaseInJobRequestMessage(message);
                }
            }
            else if (string.Equals(message.Variables[Constants.Variables.System.ServerType]?.Value, "OnPremises", StringComparison.OrdinalIgnoreCase))
            {
                ReplaceConfigUriBaseInJobRequestMessage(message);
            }

            // Setup the job server and job server queue.
            var jobServer = HostContext.GetService<IJobServer>();
            VssCredentials jobServerCredential = ApiUtil.GetVssCredential(systemConnection);
            Uri jobServerUrl = systemConnection.Url;

            Trace.Info($"Creating job server with URL: {jobServerUrl}");
            // jobServerQueue is the throttling reporter.
            _jobServerQueue = HostContext.GetService<IJobServerQueue>();
            VssConnection jobConnection = ApiUtil.CreateConnection(jobServerUrl, jobServerCredential, new DelegatingHandler[] { new ThrottlingReportHandler(_jobServerQueue) });
            await jobServer.ConnectAsync(jobConnection);

            _jobServerQueue.Start(message);

            IExecutionContext jobContext = null;
            CancellationTokenRegistration? agentShutdownRegistration = null;
            try
            {
                // Create the job execution context.
                jobContext = HostContext.CreateService<IExecutionContext>();
                jobContext.InitializeJob(message, jobRequestCancellationToken);
                Trace.Info("Starting the job execution context.");
                jobContext.Start();
                jobContext.Section(StringUtil.Loc("StepStarting", message.JobDisplayName));

                agentShutdownRegistration = HostContext.AgentShutdownToken.Register(() =>
                {
                    // log an issue, then agent get shutdown by Ctrl-C or Ctrl-Break.
                    // the server will use Ctrl-Break to tells the agent that operating system is shutting down.
                    string errorMessage;
                    switch (HostContext.AgentShutdownReason)
                    {
                        case ShutdownReason.UserCancelled:
                            errorMessage = StringUtil.Loc("UserShutdownAgent");
                            break;
                        case ShutdownReason.OperatingSystemShutdown:
                            errorMessage = StringUtil.Loc("OperatingSystemShutdown", Environment.MachineName);
                            break;
                        default:
                            throw new ArgumentException(HostContext.AgentShutdownReason.ToString(), nameof(HostContext.AgentShutdownReason));
                    }
                    jobContext.AddIssue(new Issue() { Type = IssueType.Error, Message = errorMessage });
                });

                // Set agent version variable.
                jobContext.Variables.Set(Constants.Variables.Agent.Version, Constants.Agent.Version);
                jobContext.Output(StringUtil.Loc("AgentVersion", Constants.Agent.Version));

                // Print proxy setting information for better diagnostic experience
                var agentWebProxy = HostContext.GetService<IVstsAgentWebProxy>();
                if (!string.IsNullOrEmpty(agentWebProxy.ProxyAddress))
                {
                    jobContext.Output(StringUtil.Loc("AgentRunningBehindProxy", agentWebProxy.ProxyAddress));
                }

                // Validate directory permissions.
                string workDirectory = HostContext.GetDirectory(WellKnownDirectory.Work);
                Trace.Info($"Validating directory permissions for: '{workDirectory}'");
                try
                {
                    Directory.CreateDirectory(workDirectory);
                    IOUtil.ValidateExecutePermission(workDirectory);
                }
                catch (Exception ex)
                {
                    Trace.Error(ex);
                    jobContext.Error(ex);
                    return await CompleteJobAsync(jobServer, jobContext, message, TaskResult.Failed);
                }

                // Set agent variables.
                AgentSettings settings = HostContext.GetService<IConfigurationStore>().GetSettings();
                jobContext.Variables.Set(Constants.Variables.Agent.Id, settings.AgentId.ToString(CultureInfo.InvariantCulture));
                jobContext.Variables.Set(Constants.Variables.Agent.HomeDirectory, HostContext.GetDirectory(WellKnownDirectory.Root));
                jobContext.Variables.Set(Constants.Variables.Agent.JobName, message.JobDisplayName);
                jobContext.Variables.Set(Constants.Variables.Agent.MachineName, Environment.MachineName);
                jobContext.Variables.Set(Constants.Variables.Agent.Name, settings.AgentName);
                jobContext.Variables.Set(Constants.Variables.Agent.OS, VarUtil.OS);
                jobContext.Variables.Set(Constants.Variables.Agent.RootDirectory, IOUtil.GetWorkPath(HostContext));
#if OS_WINDOWS
                jobContext.Variables.Set(Constants.Variables.Agent.ServerOMDirectory, Path.Combine(IOUtil.GetExternalsPath(), Constants.Path.ServerOMDirectory));
#else
                jobContext.Variables.Set(Constants.Variables.Agent.AcceptTeeEula, settings.AcceptTeeEula.ToString());
#endif
                jobContext.Variables.Set(Constants.Variables.Agent.WorkFolder, IOUtil.GetWorkPath(HostContext));
                jobContext.Variables.Set(Constants.Variables.System.WorkFolder, IOUtil.GetWorkPath(HostContext));

                string toolsDirectory = Environment.GetEnvironmentVariable("AGENT_TOOLSDIRECTORY") ?? Environment.GetEnvironmentVariable(Constants.Variables.Agent.ToolsDirectory);
                if (string.IsNullOrEmpty(toolsDirectory))
                {
                    toolsDirectory = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Work), Constants.Path.ToolDirectory);
                    Directory.CreateDirectory(toolsDirectory);
                }
                else
                {
                    Trace.Info($"Set tool cache directory base on environment: '{toolsDirectory}'");
                    Directory.CreateDirectory(toolsDirectory);
                }
                jobContext.Variables.Set(Constants.Variables.Agent.ToolsDirectory, toolsDirectory);

                // Setup TEMP directories
                _tempDirectoryManager = HostContext.GetService<ITempDirectoryManager>();
                _tempDirectoryManager.InitializeTempDirectory(jobContext);

                // todo: task server can throw. try/catch and fail job gracefully.
                // prefer task definitions url, then TFS collection url, then TFS account url
                var taskServer = HostContext.GetService<ITaskServer>();
                Uri taskServerUri = null;
                if (!string.IsNullOrEmpty(jobContext.Variables.System_TaskDefinitionsUri))
                {
                    taskServerUri = new Uri(jobContext.Variables.System_TaskDefinitionsUri);
                }
                else if (!string.IsNullOrEmpty(jobContext.Variables.System_TFCollectionUrl))
                {
                    taskServerUri = new Uri(jobContext.Variables.System_TFCollectionUrl);
                }

                var taskServerCredential = ApiUtil.GetVssCredential(systemConnection);
                if (taskServerUri != null)
                {
                    Trace.Info($"Creating task server with {taskServerUri}");
                    await taskServer.ConnectAsync(ApiUtil.CreateConnection(taskServerUri, taskServerCredential));
                }

                // for back compat TFS 2015 RTM/QU1, we may need to switch the task server url to agent config url
                if (!string.Equals(message.Variables.GetValueOrDefault(Constants.Variables.System.ServerType)?.Value, "Hosted", StringComparison.OrdinalIgnoreCase))
                {
                    if (taskServerUri == null || !await taskServer.TaskDefinitionEndpointExist())
                    {
                        Trace.Info($"Can't determine task download url from JobMessage or the endpoint doesn't exist.");
                        var configStore = HostContext.GetService<IConfigurationStore>();
                        taskServerUri = new Uri(configStore.GetSettings().ServerUrl);
                        Trace.Info($"Recreate task server with configuration server url: {taskServerUri}");
                        await taskServer.ConnectAsync(ApiUtil.CreateConnection(taskServerUri, taskServerCredential));
                    }
                }

                // Expand the endpoint data values.
                foreach (ServiceEndpoint endpoint in jobContext.Endpoints)
                {
                    jobContext.Variables.ExpandValues(target: endpoint.Data);
                    VarUtil.ExpandEnvironmentVariables(HostContext, target: endpoint.Data);
                }

                // Get the job extension.
                Trace.Info("Getting job extension.");
                var hostType = jobContext.Variables.System_HostType;
                var extensionManager = HostContext.GetService<IExtensionManager>();
                // We should always have one job extension
                IJobExtension jobExtension =
                    (extensionManager.GetExtensions<IJobExtension>() ?? new List<IJobExtension>())
                    .Where(x => x.HostType.HasFlag(hostType))
                    .FirstOrDefault();
                ArgUtil.NotNull(jobExtension, nameof(jobExtension));

                List<IStep> jobSteps = new List<IStep>();
                try
                {
                    Trace.Info("Initialize job. Getting all job steps.");
                    var initializeResult = await jobExtension.InitializeJob(jobContext, message);
                    jobSteps.AddRange(initializeResult.PreJobSteps);
                    jobSteps.AddRange(initializeResult.JobSteps);
                    jobSteps.AddRange(initializeResult.PostJobStep);
                }
                catch (OperationCanceledException ex) when (jobContext.CancellationToken.IsCancellationRequested)
                {
                    // set the job to canceled
                    // don't log error issue to job ExecutionContext, since server owns the job level issue
                    Trace.Error($"Job is canceled during initialize.");
                    Trace.Error($"Caught exception: {ex}");
                    return await CompleteJobAsync(jobServer, jobContext, message, TaskResult.Canceled);
                }
                catch (Exception ex)
                {
                    // set the job to failed.
                    // don't log error issue to job ExecutionContext, since server owns the job level issue
                    Trace.Error($"Job initialize failed.");
                    Trace.Error($"Caught exception from {nameof(jobExtension.InitializeJob)}: {ex}");
                    return await CompleteJobAsync(jobServer, jobContext, message, TaskResult.Failed);
                }

                // trace out all steps
                Trace.Info($"Total job steps: {jobSteps.Count}.");
                Trace.Verbose($"Job steps: '{string.Join(", ", jobSteps.Select(x => x.DisplayName))}'");

                bool processCleanup = jobContext.Variables.GetBoolean("process.clean") ?? true;
                HashSet<string> existingProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string processLookupId = null;
                if (processCleanup)
                {
                    processLookupId = $"vsts_{Guid.NewGuid()}";

                    // Set the VSTS_PROCESS_LOOKUP_ID env variable.
                    jobContext.SetVariable(Constants.ProcessLookupId, processLookupId, false, false);

                    // Take a snapshot of current running processes
                    Dictionary<int, Process> processes = SnapshotProcesses();
                    foreach (var proc in processes)
                    {
                        // Pid_ProcessName
                        existingProcesses.Add($"{proc.Key}_{proc.Value.ProcessName}");
                    }
                }

                // Run all job steps
                Trace.Info("Run all job steps.");
                var stepsRunner = HostContext.GetService<IStepsRunner>();
                try
                {
                    await stepsRunner.RunAsync(jobContext, jobSteps);
                }
                catch (Exception ex)
                {
                    // StepRunner should never throw exception out.
                    // End up here mean there is a bug in StepRunner
                    // Log the error and fail the job.
                    Trace.Error($"Caught exception from job steps {nameof(StepsRunner)}: {ex}");
                    jobContext.Error(ex);
                    return await CompleteJobAsync(jobServer, jobContext, message, TaskResult.Failed);
                }
                finally
                {
                    if (processCleanup)
                    {
                        // Only check environment variable for any process that doesn't run before we invoke our process.
                        Dictionary<int, Process> currentProcesses = SnapshotProcesses();
                        foreach (var proc in currentProcesses)
                        {
                            if (existingProcesses.Contains($"{proc.Key}_{proc.Value.ProcessName}"))
                            {
                                Trace.Verbose($"Skip existing process. PID: {proc.Key} ({proc.Value.ProcessName})");
                            }
                            else
                            {
                                Trace.Info($"Inspecting process environment variables. PID: {proc.Key} ({proc.Value.ProcessName})");

                                Dictionary<string, string> env = new Dictionary<string, string>();
                                try
                                {
                                    env = proc.Value.GetEnvironmentVariables();
                                    foreach (var e in env)
                                    {
                                        Trace.Verbose($"PID:{proc.Key} ({e.Key}={e.Value})");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Trace.Verbose("Ignore any exception during read process environment variables.");
                                    Trace.Verbose(ex.ToString());
                                }

                                if (env.TryGetValue(Constants.ProcessLookupId, out string lookupId) &&
                                    lookupId.Equals(processLookupId, StringComparison.OrdinalIgnoreCase))
                                {
                                    Trace.Info($"Terminate orphan process: pid ({proc.Key}) ({proc.Value.ProcessName})");
                                    try
                                    {
                                        proc.Value.Kill();
                                    }
                                    catch (Exception ex)
                                    {
                                        Trace.Error("Catch exception during orphan process cleanup.");
                                        Trace.Error(ex);
                                    }
                                }
                            }
                        }
                    }
                }

                Trace.Info($"Job result after all job steps finish: {jobContext.Result ?? TaskResult.Succeeded}");

                if (jobContext.Variables.GetBoolean(Constants.Variables.Agent.Diagnostic) ?? false)
                {
                    Trace.Info("Support log upload starting.");

                    IDiagnosticLogManager diagnosticLogManager = HostContext.GetService<IDiagnosticLogManager>();

                    try
                    {
                        await diagnosticLogManager.UploadDiagnosticLogsAsync(executionContext: jobContext, message: message, jobStartTimeUtc: jobStartTimeUtc);

                        Trace.Info("Support log upload complete.");
                    }
                    catch (Exception ex)
                    {
                        // Log the error but make sure we continue gracefully.
                        Trace.Info("Error uploading support logs.");
                        Trace.Error(ex);
                    }
                }

                Trace.Info("Completing the job execution context.");
                return await CompleteJobAsync(jobServer, jobContext, message);
            }
            finally
            {
                if (agentShutdownRegistration != null)
                {
                    agentShutdownRegistration.Value.Dispose();
                    agentShutdownRegistration = null;
                }

                await ShutdownQueue(throwOnFailure: false);
            }
        }

        private async Task<TaskResult> CompleteJobAsync(IJobServer jobServer, IExecutionContext jobContext, Pipelines.AgentJobRequestMessage message, TaskResult? taskResult = null)
        {
            jobContext.Section(StringUtil.Loc("StepFinishing", message.JobDisplayName));
            TaskResult result = jobContext.Complete(taskResult);

            try
            {
                await ShutdownQueue(throwOnFailure: true);
            }
            catch (Exception ex)
            {
                Trace.Error($"Caught exception from {nameof(JobServerQueue)}.{nameof(_jobServerQueue.ShutdownAsync)}");
                Trace.Error("This indicate a failure during publish output variables. Fail the job to prevent unexpected job outputs.");
                Trace.Error(ex);
                result = TaskResultUtil.MergeTaskResults(result, TaskResult.Failed);
            }

            // Clean TEMP after finish process jobserverqueue, since there might be a pending fileupload still use the TEMP dir.
            _tempDirectoryManager?.CleanupTempDirectory();

            if (!jobContext.Features.HasFlag(PlanFeatures.JobCompletedPlanEvent))
            {
                Trace.Info($"Skip raise job completed event call from worker because Plan version is {message.Plan.Version}");
                return result;
            }

            Trace.Info("Raising job completed event.");
            var jobCompletedEvent = new JobCompletedEvent(message.RequestId, message.JobId, result);

            var completeJobRetryLimit = 5;
            var exceptions = new List<Exception>();
            while (completeJobRetryLimit-- > 0)
            {
                try
                {
                    await jobServer.RaisePlanEventAsync(message.Plan.ScopeIdentifier, message.Plan.PlanType, message.Plan.PlanId, jobCompletedEvent, default(CancellationToken));
                    return result;
                }
                catch (TaskOrchestrationPlanNotFoundException ex)
                {
                    Trace.Error($"TaskOrchestrationPlanNotFoundException received, while attempting to raise JobCompletedEvent for job {message.JobId}.");
                    Trace.Error(ex);
                    return TaskResult.Failed;
                }
                catch (TaskOrchestrationPlanSecurityException ex)
                {
                    Trace.Error($"TaskOrchestrationPlanSecurityException received, while attempting to raise JobCompletedEvent for job {message.JobId}.");
                    Trace.Error(ex);
                    return TaskResult.Failed;
                }
                catch (Exception ex)
                {
                    Trace.Error($"Catch exception while attempting to raise JobCompletedEvent for job {message.JobId}, job request {message.RequestId}.");
                    Trace.Error(ex);
                    exceptions.Add(ex);
                }

                // delay 5 seconds before next retry.
                await Task.Delay(TimeSpan.FromSeconds(5));
            }

            // rethrow exceptions from all attempts.
            throw new AggregateException(exceptions);
        }

        private async Task ShutdownQueue(bool throwOnFailure)
        {
            if (_jobServerQueue != null)
            {
                try
                {
                    Trace.Info("Shutting down the job server queue.");
                    await _jobServerQueue.ShutdownAsync();
                }
                catch (Exception ex) when (!throwOnFailure)
                {
                    Trace.Error($"Caught exception from {nameof(JobServerQueue)}.{nameof(_jobServerQueue.ShutdownAsync)}");
                    Trace.Error(ex);
                }
                finally
                {
                    _jobServerQueue = null; // Prevent multiple attempts.
                }
            }
        }

        // the scheme://hostname:port (how the agent knows the server) is external to our server
        // in other words, an agent may have it's own way (DNS, hostname) of refering
        // to the server.  it owns that.  That's the scheme://hostname:port we will use.
        // Example: Server's notification url is http://tfsserver:8080/tfs 
        //          Agent config url is https://tfsserver.mycompany.com:9090/tfs 
        private Uri ReplaceWithConfigUriBase(Uri messageUri)
        {
            AgentSettings settings = HostContext.GetService<IConfigurationStore>().GetSettings();
            try
            {
                Uri result = null;
                Uri configUri = new Uri(settings.ServerUrl);
                if (Uri.TryCreate(new Uri(configUri.GetComponents(UriComponents.SchemeAndServer, UriFormat.Unescaped)), messageUri.PathAndQuery, out result))
                {
                    //replace the schema and host portion of messageUri with the host from the
                    //server URI (which was set at config time)
                    return result;
                }
            }
            catch (InvalidOperationException ex)
            {
                //cannot parse the Uri - not a fatal error
                Trace.Error(ex);
            }
            catch (UriFormatException ex)
            {
                //cannot parse the Uri - not a fatal error
                Trace.Error(ex);
            }

            return messageUri;
        }

        private void ReplaceConfigUriBaseInJobRequestMessage(Pipelines.AgentJobRequestMessage message)
        {
            ServiceEndpoint systemConnection = message.Resources.Endpoints.Single(x => string.Equals(x.Name, WellKnownServiceEndpointNames.SystemVssConnection, StringComparison.OrdinalIgnoreCase));
            Uri systemConnectionUrl = systemConnection.Url;

            // fixup any endpoint Url that match SystemConnect server.
            foreach (var endpoint in message.Resources.Endpoints)
            {
                if (Uri.Compare(endpoint.Url, systemConnectionUrl, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    endpoint.Url = ReplaceWithConfigUriBase(endpoint.Url);
                    Trace.Info($"Ensure endpoint url match config url base. {endpoint.Url}");
                }
            }

            // fixup well known variables. (taskDefinitionsUrl, tfsServerUrl, tfsCollectionUrl)
            if (message.Variables.ContainsKey(WellKnownDistributedTaskVariables.TaskDefinitionsUrl))
            {
                string taskDefinitionsUrl = message.Variables[WellKnownDistributedTaskVariables.TaskDefinitionsUrl].Value;
                message.Variables[WellKnownDistributedTaskVariables.TaskDefinitionsUrl] = ReplaceWithConfigUriBase(new Uri(taskDefinitionsUrl)).AbsoluteUri;
                Trace.Info($"Ensure System.TaskDefinitionsUrl match config url base. {message.Variables[WellKnownDistributedTaskVariables.TaskDefinitionsUrl].Value}");
            }

            if (message.Variables.ContainsKey(WellKnownDistributedTaskVariables.TFCollectionUrl))
            {
                string tfsCollectionUrl = message.Variables[WellKnownDistributedTaskVariables.TFCollectionUrl].Value;
                message.Variables[WellKnownDistributedTaskVariables.TFCollectionUrl] = ReplaceWithConfigUriBase(new Uri(tfsCollectionUrl)).AbsoluteUri;
                Trace.Info($"Ensure System.TFCollectionUrl match config url base. {message.Variables[WellKnownDistributedTaskVariables.TFCollectionUrl].Value}");
            }

            if (message.Variables.ContainsKey(Constants.Variables.System.TFServerUrl))
            {
                string tfsServerUrl = message.Variables[Constants.Variables.System.TFServerUrl].Value;
                message.Variables[Constants.Variables.System.TFServerUrl] = ReplaceWithConfigUriBase(new Uri(tfsServerUrl)).AbsoluteUri;
                Trace.Info($"Ensure System.TFServerUrl match config url base. {message.Variables[Constants.Variables.System.TFServerUrl].Value}");
            }
        }

        private Dictionary<int, Process> SnapshotProcesses()
        {
            Dictionary<int, Process> snapshot = new Dictionary<int, Process>();
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    // On Windows, this will throw exception on error.
                    // On Linux, this will be NULL on error.
                    if (!string.IsNullOrEmpty(proc.ProcessName))
                    {
                        snapshot[proc.Id] = proc;
                    }
                }
                catch (Exception ex)
                {
                    Trace.Verbose($"Ignore any exception during taking process snapshot of process pid={proc.Id}: '{ex.Message}'.");
                }
            }

            Trace.Info($"Total accessible running process: {snapshot.Count}.");
            return snapshot;
        }
    }
}
