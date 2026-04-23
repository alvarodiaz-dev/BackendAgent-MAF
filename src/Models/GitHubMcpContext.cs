using System.Collections.Generic;
using Microsoft.Extensions.AI;

namespace BasicAgent.Models
{
    internal sealed class GitHubMcpContext
    {
        public required List<AITool> Tools { get; init; }

        public required List<string> ToolNames { get; init; }

        public required string CreateRepoToolName { get; init; }

        public required string PushFilesToolName { get; init; }

        public bool IsReady => Tools.Count > 0;
    }
}
