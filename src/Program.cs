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
using Microsoft.Extensions.Hosting;
using System.Globalization;
using OpenTelemetry;
using OpenTelemetry.Trace;
using OpenTelemetry.Resources;
using Langfuse.OpenTelemetry;
using System.Diagnostics;
using System.Threading;

namespace BasicAgent
{
    internal static class Program
    {
        private static readonly ActivitySource ActivitySource = new("BasicAgent.Pipeline");

        private static async Task Main(string[] args)
        {
            Env.Load();

            if (args.Any(a => string.Equals(a, "--test-mcp-github", StringComparison.OrdinalIgnoreCase)))
            {
                await McpGitHubSmokeTests.RunAsync(args);
                return;
            }

            var builder = WebApplication.CreateBuilder(args);

            // Silenciar logs de infraestructura de Microsoft (info: Microsoft.Hosting...)
            builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
            builder.Logging.AddFilter("System", LogLevel.Warning);

            // Configuracion de Langfuse / OpenTelemetry
            if (EnvironmentVariables.IsLangfuseConfigured())
            {
                var pubKey = EnvironmentVariables.GetLangfusePublicKey();
                var baseUrl = EnvironmentVariables.GetLangfuseBaseUrl();
                Console.WriteLine($"[System] Configurando observabilidad con Langfuse...");
                Console.WriteLine($"[System] Langfuse Host: {baseUrl}");
                Console.WriteLine($"[System] Langfuse Public Key: {pubKey?[..6]}***");

                builder.Services.AddOpenTelemetry()
                    .WithTracing(tracing =>
                    {
                        tracing
                            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("BasicAgent"))
                            .AddAspNetCoreInstrumentation()
                            .AddHttpClientInstrumentation()
                            .AddSource("Microsoft.Extensions.AI")
                            .AddSource("Microsoft.Extensions.AI.*")
                            .AddSource("BasicAgent.Pipeline")
                            .AddSource("OpenAI.*")
                            .AddLangfuseExporter(options =>
                            {
                                options.PublicKey = EnvironmentVariables.GetLangfusePublicKey();
                                options.SecretKey = EnvironmentVariables.GetLangfuseSecretKey();
                                options.BaseUrl = baseUrl;
                            });
                    });
                
                builder.Services.AddHostedService<TelemetryBootstrapService>();
            }

            IChatClient chatClient = await ChatClientFactory.BuildAsync();
            var baseTools = BuildBaseTools();
            
            var requiredSkills = new[] 
            { 
                "skills/ibk-architecture-documentation", 
                "skills/ibk-smp-microservices" 
            };
            
            var skillsPath = await SkillsSynchronizer.SyncAsync(requiredSkills);
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
                Console.WriteLine("[System] Supabase listo.");
                await persistence.EnsureSchemaAsync();
            }

            builder.Services.AddSingleton(orchestrator);
            builder.Services.AddSingleton(persistence);
            builder.Services.AddSingleton<PipelineRunStore>();

            var app = builder.Build();

            // DESPERTAR telemetría silenciosamente
            _ = app.Services.GetServices<TracerProvider>();

            // Test de telemetría sin log en consola
            using (var activity = ActivitySource.StartActivity("TelemetryPing"))
            {
                activity?.SetTag("ping.status", "ok");
            }

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
                    using var activity = ActivitySource.StartActivity("PipelineRun");
                    if (activity != null)
                    {
                        activity.SetTag("langfuse.trace.id", sessionId.ToString());
                        activity.SetTag("langfuse.trace.input", request.Prompt);
                        activity.SetTag("langfuse.observation.input", request.Prompt);
                        activity.SetTag("run.id", runContext.RunId);
                        activity.SetTag("gen_ai.system", "BasicAgent");
                    }

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
                        Console.WriteLine($"[Error] Pipeline crash: {ex}");
                        if (ex.InnerException != null)
                        {
                            Console.WriteLine($"[Error] Inner Exception: {ex.InnerException}");
                        }
                        
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

        private class TelemetryBootstrapService() : IHostedService
        {
            public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }

        private static List<AITool> BuildBaseTools()
        {
            return new List<AITool>
            {
                AIFunctionFactory.Create(FileSystemTools.WriteFile),
                AIFunctionFactory.Create(FileSystemTools.CreateDirectory),
                AIFunctionFactory.Create(FileSystemTools.ReadFile),
                AIFunctionFactory.Create(FileSystemTools.GetDirectoryStructure),
                AIFunctionFactory.Create(ShellCommandTool.RunShellCommand),
                AIFunctionFactory.Create(UserConfirmationTool.RequestUserConfirmation),
                AIFunctionFactory.Create(UserConfirmationTool.AskUserYesNo),
                AIFunctionFactory.Create(UserConfirmationTool.NotifyUser)
            };
        }
    }
}
