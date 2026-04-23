using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BasicAgent.Infrastructure;
using BasicAgent.Models;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Client;

namespace BasicAgent.Services
{
    internal static class GitHubMcpBootstrapper
    {
        public static async Task<GitHubMcpContext> InitializeAsync()
        {
            var githubTools = new List<AITool>();
            var githubToolNames = new List<string>();

            var githubToken = EnvironmentVariables.GetGitHubToken();

            if (!string.IsNullOrWhiteSpace(githubToken))
            {
                try
                {
                    Console.WriteLine("Connecting to GitHub MCP Server...");
                    var githubMcpServerPath = McpServerPathResolver.ResolveGitHubServerPath();
                    if (githubMcpServerPath == null)
                    {
                        Console.WriteLine("[Warning] GitHub MCP server script not found. Run 'npm install' in project root.");
                    }
                    else
                    {
                        var transport = new StdioClientTransport(new StdioClientTransportOptions
                        {
                            Command = "node",
                            Arguments = [githubMcpServerPath],
                            EnvironmentVariables = new Dictionary<string, string?>
                            {
                                { "GITHUB_PERSONAL_ACCESS_TOKEN", githubToken }
                            }
                        });

                        var mcpClient = await McpClient.CreateAsync(transport);
                        var mcpTools = await mcpClient.ListToolsAsync();

                        githubTools.AddRange(mcpTools.Cast<AITool>());
                        githubToolNames = mcpTools.Select(t => t.Name).OrderBy(n => n).ToList();

                        Console.WriteLine($"[Success] Loaded {githubTools.Count} GitHub MCP tools.");
                        Console.WriteLine($"[MCP Tools] {string.Join(", ", githubToolNames)}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Warning] GitHub MCP failed: {ex.Message}");
                }
            }
            else
            {
                Console.WriteLine("[Warning] GitHub MCP disabled: missing GITHUB_PERSONAL_ACCESS_TOKEN (or GITHUB_TOKEN).");
            }

            string createRepoToolName = githubToolNames
                .FirstOrDefault(n => n.Contains("create_repository", StringComparison.OrdinalIgnoreCase))
                ?? "create_repository";

            string pushFilesToolName = githubToolNames
                .FirstOrDefault(n => n.Contains("push_files", StringComparison.OrdinalIgnoreCase))
                ?? "push_files";

            return new GitHubMcpContext
            {
                Tools = githubTools,
                ToolNames = githubToolNames,
                CreateRepoToolName = createRepoToolName,
                PushFilesToolName = pushFilesToolName
            };
        }
    }
}
