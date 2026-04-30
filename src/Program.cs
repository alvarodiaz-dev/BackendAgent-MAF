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
using OpenTelemetry.Exporter;
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

            // Forzar exportación rápida de OpenTelemetry (tiempo real)
            Environment.SetEnvironmentVariable("OTEL_BSP_SCHEDULE_DELAY", "500");
            Environment.SetEnvironmentVariable("OTEL_BSP_MAX_EXPORT_BATCH_SIZE", "512");

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
                var secretKey = EnvironmentVariables.GetLangfuseSecretKey();
                var baseUrl = EnvironmentVariables.GetLangfuseBaseUrl();
                
                Console.WriteLine($"[System] Configurando observabilidad con Langfuse (vVia OTLP)...");
                Console.WriteLine($"[System] Langfuse Host: {baseUrl}");

                // Construir Header de Autorización Basic (Base64)
                string auth = "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{pubKey}:{secretKey}"));

                builder.Services.AddOpenTelemetry()
                    .WithTracing(tracing =>
                    {
                        tracing
                            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("BasicAgent"))
                            .AddAspNetCoreInstrumentation(options =>
                            {
                                options.EnrichWithHttpRequest = (activity, httpRequest) =>
                                {
                                    // Estos atributos de trace-level los leerá Langfuse desde el span raíz
                                    activity.SetTag("langfuse.trace.name", $"Pipeline {httpRequest.Path}");
                                };
                                options.EnrichWithHttpResponse = (activity, httpResponse) =>
                                {
                                    activity.SetTag("http.response.status", httpResponse.StatusCode);
                                };
                                options.Filter = (httpContext) =>
                                {
                                    var path = httpContext.Request.Path.Value ?? "";
                                    return !path.StartsWith("/health") &&
                                        !path.StartsWith("/_") &&
                                        path != "/";
                                };
                            })
                            .AddHttpClientInstrumentation()
                            // Sources del SDK de Microsoft
                            .AddSource("Microsoft.Extensions.AI")
                            .AddSource("Microsoft.SemanticKernel")
                            .AddSource("Microsoft.SemanticKernel.*")
                            // Source del pipeline propio
                            .AddSource("BasicAgent.Pipeline")
                            // ── CRÍTICO: registrar el source de las generaciones LLM ──────────
                            // Sin esto, los spans de LangfuseEnrichmentClient nunca se exportan
                            .AddSource("BasicAgent.LLMGeneration")
                            // Source de OpenAI SDK (opcional, para spans adicionales)
                            .AddSource("OpenAI.*")
                            .AddOtlpExporter(options =>
                            {
                                var endpoint = baseUrl.TrimEnd('/');
                                if (!endpoint.EndsWith("/api/public/otel"))
                                    endpoint += "/api/public/otel";

                                options.Endpoint = new Uri($"{endpoint}/v1/traces");
                                options.Protocol = OtlpExportProtocol.HttpProtobuf;
                                options.Headers  = $"Authorization={auth},x-langfuse-ingestion-version=4";
                                //                                        ^── sin espacio después de la coma
                            });
                    });
            }

            IChatClient chatClient = await ChatClientFactory.BuildAsync();
            var promptService = new LangfusePromptService();
            var baseTools = BuildBaseTools();
            
            builder.Services.AddSingleton(chatClient);
            builder.Services.AddSingleton(promptService);
            
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

                var httpSpan = Activity.Current;
                if (httpSpan != null)
                {
                    httpSpan.SetTag("langfuse.trace.name", $"Pipeline Run");
                    httpSpan.SetTag("langfuse.trace.input", request.Prompt);
                    httpSpan.SetTag("langfuse.session.id", sessionId.ToString());
                    httpSpan.SetTag("langfuse.user.id", sessionId.ToString());
                    httpSpan.SetTag("gen_ai.system", "BasicAgent");
                }

                var parentContext = Activity.Current?.Context ?? default;

                _ = Task.Run(async () =>
                {
                    using var activity = ActivitySource.StartActivity("PipelineRun", ActivityKind.Internal, parentContext: parentContext);
                    if (activity != null)
                    {
                        activity.SetTag("langfuse.observation.name", "PipelineRun");
                        activity.SetTag("langfuse.observation.input", request.Prompt);
                        activity.SetTag("run.id", runContext.RunId);
                    }

                    try
                    {
                        Console.WriteLine($"[API] Executing pipeline for {runContext.RunId}");
                        var interaction = new ApiPipelineInteraction(state, request.AutoApprove);
                        bool ok = await pipeline.RunAsync(request.Prompt, interaction, runContext);
                        if (ok)
                        {
                            state.MarkCompleted();
                            var successMsg = "Pipeline completed successfully.";
                            await persistenceService.InsertMessageAsync(sessionId, "assistant", successMsg, runContext.RunId);
                            activity?.SetTag("langfuse.observation.output", successMsg);
                        }
                        else if (state.Status == "running")
                        {
                            state.MarkFailed("Pipeline ended before completion.");
                            activity?.SetTag("langfuse.trace.output", "Pipeline ended before completion.");
                            activity?.SetStatus(ActivityStatusCode.Error, "Pipeline ended before completion.");
                        }
                    }
                    catch (Exception ex)
                    {
                        activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                        activity?.AddException(ex);
                        activity?.SetTag("langfuse.observation.output", $"Error: {ex.Message}");
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
            var userConfirmationTool = AIFunctionFactory.Create(UserConfirmationTool.RequestUserConfirmation);
            // Envolvemos la herramienta en ApprovalRequiredAIFunction para activar el HITL nativo
            var approvalRequiredConfirmation = new ApprovalRequiredAIFunction(userConfirmationTool);

            return new List<AITool>
            {
                AIFunctionFactory.Create(FileSystemTools.WriteFile),
                AIFunctionFactory.Create(FileSystemTools.CreateDirectory),
                AIFunctionFactory.Create(FileSystemTools.ReadFile),
                AIFunctionFactory.Create(FileSystemTools.GetDirectoryStructure),
                AIFunctionFactory.Create(ShellCommandTool.RunShellCommand),
                approvalRequiredConfirmation,
                AIFunctionFactory.Create(UserConfirmationTool.AskUserYesNo),
                AIFunctionFactory.Create(UserConfirmationTool.NotifyUser),
                AIFunctionFactory.Create(() => "completed", "MarkTaskAsCompleted", "Mark the assigned task or phase as fully completed.")
            };
        }
    }
}
