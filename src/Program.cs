using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BasicAgent.Api;
using BasicAgent.Infrastructure;
using BasicAgent.Models;
using BasicAgent.Pipeline;
using BasicAgent.Services;
using BasicAgent.Tools;
using DotNetEnv;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using System.Globalization;

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

            var builder = WebApplication.CreateBuilder(args);

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

            var orchestrator = new PipelineOrchestrator(docAgent, msAgent, githubAgent, githubMcp.IsReady);

            var supabaseConnection = EnvironmentVariables.GetSupabasePostgresConnectionString();
            IPipelinePersistence persistence = string.IsNullOrWhiteSpace(supabaseConnection)
                ? new NoOpPipelinePersistence()
                : new SupabasePostgresPersistence(supabaseConnection);

            if (persistence.IsEnabled)
            {
                Console.WriteLine("[System] Inicializando esquema en Supabase Postgres...");
                await persistence.EnsureSchemaAsync();
                Console.WriteLine("[System] Supabase listo.");
            }
            else
            {
                Console.WriteLine("[System] Supabase deshabilitado: no se encontro SUPABASE_DB_CONNECTION_STRING.");
            }

            builder.Services.AddSingleton(orchestrator);
            builder.Services.AddSingleton(persistence);
            builder.Services.AddSingleton<PipelineRunStore>();

            var app = builder.Build();

            app.MapGet("/", () => Results.Ok(new { service = "BasicAgent API", status = "ok" }));

            app.MapPost("/chat", async (ChatRequest request, PipelineOrchestrator pipeline, PipelineRunStore store, IPipelinePersistence persistenceService) =>
            {
                if (string.IsNullOrWhiteSpace(request.Prompt))
                {
                    return Results.BadRequest(new { error = "prompt is required" });
                }

                Guid sessionId = Guid.Empty;
                if (!string.IsNullOrWhiteSpace(request.SessionId) && !Guid.TryParse(request.SessionId, out sessionId))
                {
                    return Results.BadRequest(new { error = "sessionId must be a valid UUID" });
                }

                if (sessionId == Guid.Empty)
                {
                    sessionId = Guid.NewGuid();
                }

                string shortPrompt = request.Prompt.Length <= 120
                    ? request.Prompt
                    : request.Prompt[..120];

                await persistenceService.UpsertSessionAsync(sessionId, shortPrompt);
                await persistenceService.InsertMessageAsync(sessionId, "user", request.Prompt);

                PipelineRunContext runContext = RunOutputFactory.Create(request.Prompt);
                PipelineRunState state = store.Create(runContext, sessionId);
                await state.PersistCreatedAsync(request.Prompt, request.AutoApprove);
                state.MarkRunning("starting");
                state.AddLog("Run accepted.");
                Console.WriteLine($"[API] Run accepted: {runContext.RunId}");
                Console.WriteLine($"[API] Background pipeline starting for session {sessionId}");

                _ = Task.Run(async () =>
                {
                    try
                    {
                        Console.WriteLine($"[API] Executing pipeline for {runContext.RunId}");
                        var interaction = new ApiPipelineInteraction(state, request.AutoApprove);
                        bool ok = await pipeline.RunAsync(request.Prompt, interaction, runContext);
                        if (ok)
                        {
                            state.MarkCompleted();
                            await persistenceService.InsertMessageAsync(sessionId, "assistant", "Pipeline completed successfully.", runContext.RunId);
                        }
                        else if (state.Status == "running")
                        {
                            state.MarkFailed("Pipeline ended before completion.");
                            await persistenceService.InsertMessageAsync(sessionId, "assistant", "Pipeline ended before completion.", runContext.RunId);
                        }
                    }
                    catch (Exception ex)
                    {
                        state.AddLog($"Unhandled error: {ex.Message}");
                        state.MarkFailed(ex.Message);
                        await persistenceService.InsertMessageAsync(sessionId, "assistant", $"Pipeline failed: {ex.Message}", runContext.RunId);
                    }
                });

                string statusUrl = $"/chat/{runContext.RunId}";
                return Results.Accepted(statusUrl, new ChatStartResponse(runContext.RunId, sessionId.ToString("D", CultureInfo.InvariantCulture), statusUrl));
            });

            app.MapGet("/chat/{runId}", (string runId, PipelineRunStore store) =>
            {
                if (!store.TryGet(runId, out var state) || state == null)
                {
                    return Results.NotFound(new { error = "run not found" });
                }

                return Results.Ok(state.ToResponse());
            });

            app.MapPost("/chat/{runId}/confirm", (string runId, ConfirmRequest request, PipelineRunStore store) =>
            {
                if (!store.TryGet(runId, out var state) || state == null)
                {
                    return Results.NotFound(new { error = "run not found" });
                }

                if (string.IsNullOrWhiteSpace(request.RequestId))
                {
                    return Results.BadRequest(new { error = "requestId is required" });
                }

                bool accepted = state.TrySubmitConfirmation(request.RequestId, request.Response ?? string.Empty);
                return accepted
                    ? Results.Ok(new { status = "accepted" })
                    : Results.BadRequest(new { error = "invalid requestId or no pending confirmation" });
            });

            await app.RunAsync();
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
