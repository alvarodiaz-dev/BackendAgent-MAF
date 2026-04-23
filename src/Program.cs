using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BasicAgent.Infrastructure;
using BasicAgent.Pipeline;
using BasicAgent.Services;
using BasicAgent.Tools;
using DotNetEnv;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace BasicAgent
{
    internal static class Program
    {
        private static async Task Main(string[] args)
        {
            Env.Load();

            if (args.Any(a => string.Equals(a, "--test-mcp-github", StringComparison.OrdinalIgnoreCase)))
            {
                await McpGitHubSmokeTests.RunAsync(args);
                return;
            }

            IChatClient chatClient = await ChatClientFactory.BuildAsync();

            var baseTools = BuildBaseTools();
            var skillsPath = System.IO.Path.Combine(ProjectPaths.GetProjectRootDirectory(), "skills");
            var skillsProvider = SkillsProviderFactory.Build(skillsPath);
            var githubMcp = await GitHubMcpBootstrapper.InitializeAsync();

            AIAgent docAgent = AgentFactory.Build(
                chatClient,
                "DocumentationAgent",
                AgentInstructions.DocumentationAgent,
                baseTools,
                skillsProvider);

            AIAgent msAgent = AgentFactory.Build(
                chatClient,
                "MicroservicesAgent",
                AgentInstructions.MicroservicesAgent,
                baseTools,
                skillsProvider);

            var githubAgentTools = baseTools.Concat(githubMcp.Tools).ToList();
            AIAgent githubAgent = AgentFactory.Build(
                chatClient,
                "GithubAgent",
                AgentInstructions.BuildGitHubAgent(githubMcp.CreateRepoToolName, githubMcp.PushFilesToolName),
                githubAgentTools,
                null);

            Console.WriteLine("\n========================================================");
            Console.WriteLine("Pipeline listo. Escribe tu solicitud y presiona Enter.");
            Console.WriteLine("========================================================");

            var orchestrator = new PipelineOrchestrator(docAgent, msAgent, githubAgent, githubMcp.IsReady);

            while (true)
            {
                Console.Write("\n> ");
                var input = Console.ReadLine();

                if (string.IsNullOrWhiteSpace(input))
                {
                    continue;
                }

                if (input.Equals("exit", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                await orchestrator.RunAsync(input);
            }
        }

        private static List<AITool> BuildBaseTools()
        {
            return new List<AITool>
            {
                AIFunctionFactory.Create(FileSystemTools.WriteFile),
                AIFunctionFactory.Create(FileSystemTools.CreateDirectory),
                AIFunctionFactory.Create(FileSystemTools.ReadFile),
                AIFunctionFactory.Create(ShellCommandTool.RunShellCommand),
                AIFunctionFactory.Create(UserConfirmationTool.RequestUserConfirmation),
                AIFunctionFactory.Create(UserConfirmationTool.AskUserYesNo),
                AIFunctionFactory.Create(UserConfirmationTool.NotifyUser)
            };
        }
    }
}
