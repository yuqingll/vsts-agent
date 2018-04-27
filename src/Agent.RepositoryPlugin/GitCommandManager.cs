using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using Microsoft.VisualStudio.Services.Agent.PluginCore;

namespace Agent.RepositoryPlugin
{
    public class GitCommandManager
    {
#if OS_WINDOWS
        private static readonly Encoding s_encoding = Encoding.UTF8;
#else
        private static readonly Encoding s_encoding = null;
#endif
        private string gitHttpUserAgentEnv = null;
        private string gitPath = null;
        private Version gitVersion = null;
        private string gitLfsPath = null;
        private Version gitLfsVersion = null;

        public bool EnsureGitVersion(Version requiredVersion, bool throwOnNotMatch)
        {
            PluginUtil.NotNull(gitPath, nameof(gitPath));
            PluginUtil.NotNull(gitVersion, nameof(gitVersion));

            if (gitVersion < requiredVersion && throwOnNotMatch)
            {
                throw new NotSupportedException($"MinRequiredGitVersion {requiredVersion}, {gitPath}, {gitVersion}");
            }

            return gitVersion >= requiredVersion;
        }

        public bool EnsureGitLFSVersion(Version requiredVersion, bool throwOnNotMatch)
        {
            PluginUtil.NotNull(gitLfsPath, nameof(gitLfsPath));
            PluginUtil.NotNull(gitLfsVersion, nameof(gitLfsVersion));

            if (gitLfsVersion < requiredVersion && throwOnNotMatch)
            {
                throw new NotSupportedException($"MinRequiredGitLfsVersion {requiredVersion} {gitLfsPath} {gitLfsVersion}");
            }

            return gitLfsVersion >= requiredVersion;
        }

        public static string PrependPath(string path, string currentPath)
        {
            PluginUtil.NotNullOrEmpty(path, nameof(path));
            if (string.IsNullOrEmpty(currentPath))
            {
                // Careful not to add a trailing separator if the PATH is empty.
                // On OSX/Linux, a trailing separator indicates that "current directory"
                // is added to the PATH, which is considered a security risk.
                return path;
            }

            return path + Path.PathSeparator + currentPath;
        }

        public void PrependPath(string directory)
        {
            PluginUtil.DirectoryExists(directory, nameof(directory));

            // Build the new value.
            string currentPath = Environment.GetEnvironmentVariable("PATH");
            string path = PrependPath(directory, currentPath);

            // Update the PATH environment variable.
            Environment.SetEnvironmentVariable("PATH", path);
        }

        public async Task LoadGitExecutionInfo(AgentTaskPluginExecutionContext context, bool useBuiltInGit)
        {
            // Resolve the location of git.
            if (useBuiltInGit)
            {
#if OS_WINDOWS
                gitPath = Path.Combine(context.Variables.GetValueOrDefault("agent.homedirectory")?.Value, "externals", "git", "cmd", $"git.exe");

                // Prepend the PATH.
                context.Output($"Prepending0WithDirectoryContaining1 PATH {Path.GetFileName(gitPath)}");
                PrependPath(Path.GetDirectoryName(gitPath));
                context.Debug($"PATH: '{Environment.GetEnvironmentVariable("PATH")}'");
#else
                // There is no built-in git for OSX/Linux
                gitPath = null;
#endif
            }
            else
            {
                gitPath = PluginUtil.Which("git", require: true);
            }

            PluginUtil.FileExists(gitPath, nameof(gitPath));

            // Get the Git version.    
            gitVersion = await GitVersion(context);
            PluginUtil.NotNull(gitVersion, nameof(gitVersion));
            context.Debug($"Detect git version: {gitVersion.ToString()}.");

            // Resolve the location of git-lfs.
            // This should be best effort since checkout lfs objects is an option.
            // We will check and ensure git-lfs version later
            gitLfsPath = PluginUtil.Which("git-lfs", require: false);

            // Get the Git-LFS version if git-lfs exist in %PATH%.
            if (!string.IsNullOrEmpty(gitLfsPath))
            {
                gitLfsVersion = await GitLfsVersion(context);
                context.Debug($"Detect git-lfs version: '{gitLfsVersion?.ToString() ?? string.Empty}'.");
            }

            // required 2.0, all git operation commandline args need min git version 2.0
            Version minRequiredGitVersion = new Version(2, 0);
            EnsureGitVersion(minRequiredGitVersion, throwOnNotMatch: true);

            // suggest user upgrade to 2.9 for better git experience
            Version recommendGitVersion = new Version(2, 9);
            if (!EnsureGitVersion(recommendGitVersion, throwOnNotMatch: false))
            {
                context.Output($"UpgradeToLatestGit  {recommendGitVersion}, {gitVersion}");
            }

            // Set the user agent.
            gitHttpUserAgentEnv = $"git/{gitVersion.ToString()} (vsts-agent-git/{context.Variables.GetValueOrDefault("agent.version")?.Value ?? string.Empty})";
            context.Debug($"Set git useragent to: {gitHttpUserAgentEnv}.");
        }

        // git init <LocalDir>
        public async Task<int> GitInit(AgentTaskPluginExecutionContext context, string repositoryPath)
        {
            context.Debug($"Init git repository at: {repositoryPath}.");
            string repoRootEscapeSpace = PluginUtil.Format(@"""{0}""", repositoryPath.Replace(@"""", @"\"""));
            return await ExecuteGitCommandAsync(context, repositoryPath, "init", PluginUtil.Format($"{repoRootEscapeSpace}"));
        }

        // git fetch --tags --prune --progress --no-recurse-submodules [--depth=15] origin [+refs/pull/*:refs/remote/pull/*]
        public async Task<int> GitFetch(AgentTaskPluginExecutionContext context, string repositoryPath, string remoteName, int fetchDepth, List<string> refSpec, string additionalCommandLine, CancellationToken cancellationToken)
        {
            context.Debug($"Fetch git repository at: {repositoryPath} remote: {remoteName}.");
            if (refSpec != null && refSpec.Count > 0)
            {
                refSpec = refSpec.Where(r => !string.IsNullOrEmpty(r)).ToList();
            }

            // default options for git fetch.
            string options = PluginUtil.Format($"--tags --prune --progress --no-recurse-submodules {remoteName} {string.Join(" ", refSpec)}");

            // If shallow fetch add --depth arg
            // If the local repository is shallowed but there is no fetch depth provide for this build,
            // add --unshallow to convert the shallow repository to a complete repository
            if (fetchDepth > 0)
            {
                options = PluginUtil.Format($"--tags --prune --progress --no-recurse-submodules --depth={fetchDepth} {remoteName} {string.Join(" ", refSpec)}");
            }
            else
            {
                if (File.Exists(Path.Combine(repositoryPath, ".git", "shallow")))
                {
                    options = PluginUtil.Format($"--tags --prune --progress --no-recurse-submodules --unshallow {remoteName} {string.Join(" ", refSpec)}");
                }
            }

            return await ExecuteGitCommandAsync(context, repositoryPath, "fetch", options, additionalCommandLine, cancellationToken);
        }

        // git lfs fetch origin [ref]
        public async Task<int> GitLFSFetch(AgentTaskPluginExecutionContext context, string repositoryPath, string remoteName, string refSpec, string additionalCommandLine, CancellationToken cancellationToken)
        {
            context.Debug($"Fetch LFS objects for git repository at: {repositoryPath} remote: {remoteName}.");

            // default options for git lfs fetch.
            string options = PluginUtil.Format($"fetch origin {refSpec}");
            return await ExecuteGitCommandAsync(context, repositoryPath, "lfs", options, additionalCommandLine, cancellationToken);
        }

        // git checkout -f --progress <commitId/branch>
        public async Task<int> GitCheckout(AgentTaskPluginExecutionContext context, string repositoryPath, string committishOrBranchSpec, CancellationToken cancellationToken)
        {
            context.Debug($"Checkout {committishOrBranchSpec}.");

            // Git 2.7 support report checkout progress to stderr during stdout/err redirect.
            string options;
            if (gitVersion >= new Version(2, 7))
            {
                options = PluginUtil.Format("--progress --force {0}", committishOrBranchSpec);
            }
            else
            {
                options = PluginUtil.Format("--force {0}", committishOrBranchSpec);
            }

            return await ExecuteGitCommandAsync(context, repositoryPath, "checkout", options, cancellationToken);
        }

        // git clean -fdx
        public async Task<int> GitClean(AgentTaskPluginExecutionContext context, string repositoryPath)
        {
            context.Debug($"Delete untracked files/folders for repository at {repositoryPath}.");
            return await ExecuteGitCommandAsync(context, repositoryPath, "clean", "-fdx");
        }

        // git reset --hard HEAD
        public async Task<int> GitReset(AgentTaskPluginExecutionContext context, string repositoryPath)
        {
            context.Debug($"Undo any changes to tracked files in the working tree for repository at {repositoryPath}.");
            return await ExecuteGitCommandAsync(context, repositoryPath, "reset", "--hard HEAD");
        }

        // get remote set-url <origin> <url>
        public async Task<int> GitRemoteAdd(AgentTaskPluginExecutionContext context, string repositoryPath, string remoteName, string remoteUrl)
        {
            context.Debug($"Add git remote: {remoteName} to url: {remoteUrl} for repository under: {repositoryPath}.");
            return await ExecuteGitCommandAsync(context, repositoryPath, "remote", PluginUtil.Format($"add {remoteName} {remoteUrl}"));
        }

        // get remote set-url <origin> <url>
        public async Task<int> GitRemoteSetUrl(AgentTaskPluginExecutionContext context, string repositoryPath, string remoteName, string remoteUrl)
        {
            context.Debug($"Set git fetch url to: {remoteUrl} for remote: {remoteName}.");
            return await ExecuteGitCommandAsync(context, repositoryPath, "remote", PluginUtil.Format($"set-url {remoteName} {remoteUrl}"));
        }

        // get remote set-url --push <origin> <url>
        public async Task<int> GitRemoteSetPushUrl(AgentTaskPluginExecutionContext context, string repositoryPath, string remoteName, string remoteUrl)
        {
            context.Debug($"Set git push url to: {remoteUrl} for remote: {remoteName}.");
            return await ExecuteGitCommandAsync(context, repositoryPath, "remote", PluginUtil.Format($"set-url --push {remoteName} {remoteUrl}"));
        }

        // git submodule foreach git clean -fdx
        public async Task<int> GitSubmoduleClean(AgentTaskPluginExecutionContext context, string repositoryPath)
        {
            context.Debug($"Delete untracked files/folders for submodules at {repositoryPath}.");
            return await ExecuteGitCommandAsync(context, repositoryPath, "submodule", "foreach git clean -fdx");
        }

        // git submodule foreach git reset --hard HEAD
        public async Task<int> GitSubmoduleReset(AgentTaskPluginExecutionContext context, string repositoryPath)
        {
            context.Debug($"Undo any changes to tracked files in the working tree for submodules at {repositoryPath}.");
            return await ExecuteGitCommandAsync(context, repositoryPath, "submodule", "foreach git reset --hard HEAD");
        }

        // git submodule update --init --force [--recursive]
        public async Task<int> GitSubmoduleUpdate(AgentTaskPluginExecutionContext context, string repositoryPath, string additionalCommandLine, bool recursive, CancellationToken cancellationToken)
        {
            context.Debug("Update the registered git submodules.");
            string options = "update --init --force";
            if (recursive)
            {
                options = options + " --recursive";
            }

            return await ExecuteGitCommandAsync(context, repositoryPath, "submodule", options, additionalCommandLine, cancellationToken);
        }

        // git submodule sync [--recursive]
        public async Task<int> GitSubmoduleSync(AgentTaskPluginExecutionContext context, string repositoryPath, bool recursive, CancellationToken cancellationToken)
        {
            context.Debug("Synchronizes submodules' remote URL configuration setting.");
            string options = "sync";
            if (recursive)
            {
                options = options + " --recursive";
            }

            return await ExecuteGitCommandAsync(context, repositoryPath, "submodule", options, cancellationToken);
        }

        // git config --get remote.origin.url
        public async Task<Uri> GitGetFetchUrl(AgentTaskPluginExecutionContext context, string repositoryPath)
        {
            context.Debug($"Inspect remote.origin.url for repository under {repositoryPath}");
            Uri fetchUrl = null;

            List<string> outputStrings = new List<string>();
            int exitCode = await ExecuteGitCommandAsync(context, repositoryPath, "config", "--get remote.origin.url", outputStrings);

            if (exitCode != 0)
            {
                context.Warning($"'git config --get remote.origin.url' failed with exit code: {exitCode}, output: '{string.Join(Environment.NewLine, outputStrings)}'");
            }
            else
            {
                // remove empty strings
                outputStrings = outputStrings.Where(o => !string.IsNullOrEmpty(o)).ToList();
                if (outputStrings.Count == 1 && !string.IsNullOrEmpty(outputStrings.First()))
                {
                    string remoteFetchUrl = outputStrings.First();
                    if (Uri.IsWellFormedUriString(remoteFetchUrl, UriKind.Absolute))
                    {
                        context.Debug($"Get remote origin fetch url from git config: {remoteFetchUrl}");
                        fetchUrl = new Uri(remoteFetchUrl);
                    }
                    else
                    {
                        context.Debug($"The Origin fetch url from git config: {remoteFetchUrl} is not a absolute well formed url.");
                    }
                }
                else
                {
                    context.Debug($"Unable capture git remote fetch uri from 'git config --get remote.origin.url' command's output, the command's output is not expected: {string.Join(Environment.NewLine, outputStrings)}.");
                }
            }

            return fetchUrl;
        }

        // git config <key> <value>
        public async Task<int> GitConfig(AgentTaskPluginExecutionContext context, string repositoryPath, string configKey, string configValue)
        {
            context.Debug($"Set git config {configKey} {configValue}");
            return await ExecuteGitCommandAsync(context, repositoryPath, "config", PluginUtil.Format($"{configKey} {configValue}"));
        }

        // git config --get-all <key>
        public async Task<bool> GitConfigExist(AgentTaskPluginExecutionContext context, string repositoryPath, string configKey)
        {
            // git config --get-all {configKey} will return 0 and print the value if the config exist.
            context.Debug($"Checking git config {configKey} exist or not");

            // ignore any outputs by redirect them into a string list, since the output might contains secrets.
            List<string> outputStrings = new List<string>();
            int exitcode = await ExecuteGitCommandAsync(context, repositoryPath, "config", PluginUtil.Format($"--get-all {configKey}"), outputStrings);

            return exitcode == 0;
        }

        // git config --unset-all <key>
        public async Task<int> GitConfigUnset(AgentTaskPluginExecutionContext context, string repositoryPath, string configKey)
        {
            context.Debug($"Unset git config --unset-all {configKey}");
            return await ExecuteGitCommandAsync(context, repositoryPath, "config", PluginUtil.Format($"--unset-all {configKey}"));
        }

        // git config gc.auto 0
        public async Task<int> GitDisableAutoGC(AgentTaskPluginExecutionContext context, string repositoryPath)
        {
            context.Debug("Disable git auto garbage collection.");
            return await ExecuteGitCommandAsync(context, repositoryPath, "config", "gc.auto 0");
        }

        // git repack -adfl
        public async Task<int> GitRepack(AgentTaskPluginExecutionContext context, string repositoryPath)
        {
            context.Debug("Compress .git directory.");
            return await ExecuteGitCommandAsync(context, repositoryPath, "repack", "-adfl");
        }

        // git prune
        public async Task<int> GitPrune(AgentTaskPluginExecutionContext context, string repositoryPath)
        {
            context.Debug("Delete unreachable objects under .git directory.");
            return await ExecuteGitCommandAsync(context, repositoryPath, "prune", "-v");
        }

        // git count-objects -v -H
        public async Task<int> GitCountObjects(AgentTaskPluginExecutionContext context, string repositoryPath)
        {
            context.Debug("Inspect .git directory.");
            return await ExecuteGitCommandAsync(context, repositoryPath, "count-objects", "-v -H");
        }

        // git lfs install --local
        public async Task<int> GitLFSInstall(AgentTaskPluginExecutionContext context, string repositoryPath)
        {
            context.Debug("Ensure git-lfs installed.");
            return await ExecuteGitCommandAsync(context, repositoryPath, "lfs", "install --local");
        }

        // git lfs logs last
        public async Task<int> GitLFSLogs(AgentTaskPluginExecutionContext context, string repositoryPath)
        {
            context.Debug("Get git-lfs logs.");
            return await ExecuteGitCommandAsync(context, repositoryPath, "lfs", "logs last");
        }

        // git version
        public async Task<Version> GitVersion(AgentTaskPluginExecutionContext context)
        {
            context.Debug("Get git version.");
            Version version = null;
            List<string> outputStrings = new List<string>();
            int exitCode = await ExecuteGitCommandAsync(context, context.Variables.GetValueOrDefault("agent.workdirectory")?.Value, "version", null, outputStrings);
            context.Output($"{string.Join(Environment.NewLine, outputStrings)}");
            if (exitCode == 0)
            {
                // remove any empty line.
                outputStrings = outputStrings.Where(o => !string.IsNullOrEmpty(o)).ToList();
                if (outputStrings.Count == 1 && !string.IsNullOrEmpty(outputStrings.First()))
                {
                    string verString = outputStrings.First();
                    // we interested about major.minor.patch version
                    Regex verRegex = new Regex("\\d+\\.\\d+(\\.\\d+)?", RegexOptions.IgnoreCase);
                    var matchResult = verRegex.Match(verString);
                    if (matchResult.Success && !string.IsNullOrEmpty(matchResult.Value))
                    {
                        if (!Version.TryParse(matchResult.Value, out version))
                        {
                            version = null;
                        }
                    }
                }
            }

            return version;
        }

        // git lfs version
        public async Task<Version> GitLfsVersion(AgentTaskPluginExecutionContext context)
        {
            context.Debug("Get git-lfs version.");
            Version version = null;
            List<string> outputStrings = new List<string>();
            int exitCode = await ExecuteGitCommandAsync(context, context.Variables.GetValueOrDefault("agent.workdirectory")?.Value, "lfs version", null, outputStrings);
            context.Output($"{string.Join(Environment.NewLine, outputStrings)}");
            if (exitCode == 0)
            {
                // remove any empty line.
                outputStrings = outputStrings.Where(o => !string.IsNullOrEmpty(o)).ToList();
                if (outputStrings.Count == 1 && !string.IsNullOrEmpty(outputStrings.First()))
                {
                    string verString = outputStrings.First();
                    // we interested about major.minor.patch version
                    Regex verRegex = new Regex("\\d+\\.\\d+(\\.\\d+)?", RegexOptions.IgnoreCase);
                    var matchResult = verRegex.Match(verString);
                    if (matchResult.Success && !string.IsNullOrEmpty(matchResult.Value))
                    {
                        if (!Version.TryParse(matchResult.Value, out version))
                        {
                            version = null;
                        }
                    }
                }
            }

            return version;
        }
        private async Task<int> ExecuteGitCommandAsync(AgentTaskPluginExecutionContext context, string repoRoot, string command, string options, CancellationToken cancellationToken = default(CancellationToken))
        {
            string arg = PluginUtil.Format($"{command} {options}").Trim();
            context.Command($"git {arg}");

            var processInvoker = new ProcessInvoker(context);
            processInvoker.OutputDataReceived += delegate (object sender, ProcessDataReceivedEventArgs message)
            {
                context.Output(message.Data);
            };

            processInvoker.ErrorDataReceived += delegate (object sender, ProcessDataReceivedEventArgs message)
            {
                context.Output(message.Data);
            };

            return await processInvoker.ExecuteAsync(
                workingDirectory: repoRoot,
                fileName: gitPath,
                arguments: arg,
                environment: GetGitEnvironmentVariables(context),
                requireExitCodeZero: false,
                outputEncoding: s_encoding,
                cancellationToken: cancellationToken);
        }

        private async Task<int> ExecuteGitCommandAsync(AgentTaskPluginExecutionContext context, string repoRoot, string command, string options, IList<string> output)
        {
            string arg = PluginUtil.Format($"{command} {options}").Trim();
            context.Command($"git {arg}");

            if (output == null)
            {
                output = new List<string>();
            }

            object outputLock = new object();
            var processInvoker = new ProcessInvoker(context);
            processInvoker.OutputDataReceived += delegate (object sender, ProcessDataReceivedEventArgs message)
            {
                lock (outputLock)
                {
                    output.Add(message.Data);
                }
            };

            processInvoker.ErrorDataReceived += delegate (object sender, ProcessDataReceivedEventArgs message)
            {
                lock (outputLock)
                {
                    output.Add(message.Data);
                }
            };

            return await processInvoker.ExecuteAsync(
                workingDirectory: repoRoot,
                fileName: gitPath,
                arguments: arg,
                environment: GetGitEnvironmentVariables(context),
                requireExitCodeZero: false,
                outputEncoding: s_encoding,
                cancellationToken: default(CancellationToken));
        }

        private async Task<int> ExecuteGitCommandAsync(AgentTaskPluginExecutionContext context, string repoRoot, string command, string options, string additionalCommandLine, CancellationToken cancellationToken)
        {
            string arg = PluginUtil.Format($"{additionalCommandLine} {command} {options}").Trim();
            context.Command($"git {arg}");

            var processInvoker = new ProcessInvoker(context);
            processInvoker.OutputDataReceived += delegate (object sender, ProcessDataReceivedEventArgs message)
            {
                context.Output(message.Data);
            };

            processInvoker.ErrorDataReceived += delegate (object sender, ProcessDataReceivedEventArgs message)
            {
                context.Output(message.Data);
            };

            return await processInvoker.ExecuteAsync(
                workingDirectory: repoRoot,
                fileName: gitPath,
                arguments: arg,
                environment: GetGitEnvironmentVariables(context),
                requireExitCodeZero: false,
                outputEncoding: s_encoding,
                cancellationToken: cancellationToken);
        }

        private IDictionary<string, string> GetGitEnvironmentVariables(AgentTaskPluginExecutionContext context)
        {
            Dictionary<string, string> gitEnv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "GIT_TERMINAL_PROMPT", "0" },
            };

            if (!string.IsNullOrEmpty(gitHttpUserAgentEnv))
            {
                gitEnv["GIT_HTTP_USER_AGENT"] = gitHttpUserAgentEnv;
            }

            // Add the public variables.
            foreach (var variable in context.Variables)
            {
                // Add the variable using the formatted name.
                string formattedKey = (variable.Key ?? string.Empty).Replace('.', '_').Replace(' ', '_').ToUpperInvariant();

                // Skip any GIT_TRACE variable since GIT_TRACE will affect ouput from every git command.
                // This will fail the parse logic for detect git version, remote url, etc.
                // Ex. 
                //      SET GIT_TRACE=true
                //      git version 
                //      11:39:58.295959 git.c:371               trace: built-in: git 'version'
                //      git version 2.11.1.windows.1
                if (formattedKey == "GIT_TRACE" || formattedKey.StartsWith("GIT_TRACE_"))
                {
                    continue;
                }

                gitEnv[formattedKey] = variable.Value?.Value ?? string.Empty;
            }

            return gitEnv;
        }
    }
}