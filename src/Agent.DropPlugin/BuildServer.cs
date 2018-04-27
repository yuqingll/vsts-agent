using Microsoft.TeamFoundation.Core.WebApi;
using Microsoft.VisualStudio.Services.Agent.PluginCore;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Build2 = Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.VisualStudio.Services.WebApi;

namespace Agent.DropPlugin
{
    public class BuildServer
    {
        private readonly Build2.BuildHttpClient _buildHttpClient;

        public BuildServer(VssConnection connection)
        {
            PluginUtil.NotNull(connection, nameof(connection));
            _buildHttpClient = connection.GetClient<Build2.BuildHttpClient>();
        }

        public async Task<Build2.BuildArtifact> AssociateArtifact(
            Guid projectId,
            int buildId,
            string name,
            string type,
            string data,
            Dictionary<string, string> propertiesDictionary,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            Build2.BuildArtifact artifact = new Build2.BuildArtifact()
            {
                Name = name,
                Resource = new Build2.ArtifactResource()
                {
                    Data = data,
                    Type = type,
                    Properties = propertiesDictionary
                }
            };

            return await _buildHttpClient.CreateArtifactAsync(artifact, projectId, buildId, cancellationToken: cancellationToken);
        }
    }
}
