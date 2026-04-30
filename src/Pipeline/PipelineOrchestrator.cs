using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BasicAgent.Infrastructure;
using BasicAgent.Models;
using BasicAgent.Services;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace BasicAgent.Pipeline
{
    internal sealed class PipelineOrchestrator
    {
        private readonly AIAgent _docAgent;
        private readonly AIAgent _msAgent;
        private readonly AIAgent _githubAgent;
        private readonly bool _githubMcpReady;
        private const int MaxStreamingRetries = 6;

        public PipelineOrchestrator(AIAgent docAgent, AIAgent msAgent, AIAgent githubAgent, bool githubMcpReady)
        {
            _docAgent = docAgent;
            _msAgent = msAgent;
            _githubAgent = githubAgent;
            _githubMcpReady = githubMcpReady;
        }

        public async Task<bool> RunAsync(string userInput, IPipelineInteraction interaction, PipelineRunContext? existingRunContext = null)
        {
            var runContext = existingRunContext ?? RunOutputFactory.Create(userInput);
            interaction.Log($"[Pipeline] Run creado: {runContext.RunId}");
            interaction.Log($"[Pipeline] Output de esta ejecucion: {runContext.RunDirectory}");

            using (ProjectPaths.UseRunDirectory(runContext.RunDirectory))
            using (PipelineInteractionContext.Use(interaction))
            {
                // FASE 1: Documentation (Sub-agente)
                interaction.UpdatePhase("fase-1-documentation");
                interaction.Log("[Pipeline] > FASE 1 - Iniciando Documentation Sub-agent");
                
                var docSession = await _docAgent.CreateSessionAsync();
                var docPrompt = $"""
                    Request: {userInput}
                    Execute the 'ibk-architecture-documentation' skill now.
                    """;

                var docMessages = new List<ChatMessage> { new ChatMessage(ChatRole.User, docPrompt) };

                bool docOk = await StreamAgentAsync(_docAgent, docSession, docMessages, interaction);
                if (!docOk)
                {
                    interaction.Log("[Pipeline] X FASE 1 falló. Abortando pipeline.");
                    return false;
                }

                // Localización del contrato
                interaction.Log("[Pipeline] Localizando contrato para la siguiente fase...");
                var contractPath = Directory
                    .GetFiles(runContext.RunDirectory, "*06-API-CONTRACTS.md", SearchOption.AllDirectories)
                    .Where(f => f.Contains("_bmad-output"))
                    .OrderByDescending(f => new FileInfo(f).LastWriteTime)
                    .FirstOrDefault();

                if (contractPath == null || !File.Exists(contractPath))
                {
                    interaction.Log("[Pipeline] X Error: No se encontró '06-API-CONTRACTS.md' tras la ejecución del sub-agente.");
                    return false;
                }

                string contractContent = await File.ReadAllTextAsync(contractPath);
                interaction.Log($"[Pipeline] OK Contrato detectado ({contractContent.Length} bytes)");

                // FASE 2: Microservices (Sub-agente)
                interaction.UpdatePhase("fase-2-microservices");
                interaction.Log("[Pipeline] > FASE 2 - Iniciando Microservices Sub-agent");
                
                var msSession = await _msAgent.CreateSessionAsync();
                var msPrompt = $"""
                    API Contract Content:
                    {contractContent}

                    Execute the 'ibk-smp-microservices' skill now.
                    """;

                var msMessages = new List<ChatMessage> { new ChatMessage(ChatRole.User, msPrompt) };
                bool msOk = await StreamAgentAsync(_msAgent, msSession, msMessages, interaction);
                if (!msOk)
                {
                    interaction.Log("[Pipeline] X FASE 2 falló. Abortando pipeline.");
                    return false;
                }

                // Buscar el proyecto generado
                string? projectDir = Directory.GetDirectories(runContext.RunDirectory, "ibkteam-smp-*-service", SearchOption.AllDirectories)
                                              .OrderByDescending(Directory.GetCreationTime)
                                              .FirstOrDefault();

                if (projectDir == null)
                {
                    interaction.Log("[Pipeline] X Error: No se encontró el proyecto de código generado.");
                    return false;
                }

                string projectName = Path.GetFileName(projectDir);
                interaction.Log($"[Pipeline] OK Proyecto detectado: {projectName}");

                // FASE 3: GitHub (Sub-agente)
                if (!_githubMcpReady)
                {
                    interaction.Log("[Pipeline] ! GitHub MCP no disponible. Finalizando localmente.");
                    return true;
                }

                interaction.UpdatePhase("fase-3-github");
                interaction.Log("[Pipeline] > FASE 3 - Iniciando GitHub Sub-agent");
                var ghSession = await _githubAgent.CreateSessionAsync();
                var ghPrompt = $"""
                    Project Name: {projectName}
                    Local Path: {projectDir}
                    
                    Publish this project to GitHub now.
                    """;

                var ghMessages = new List<ChatMessage> { new ChatMessage(ChatRole.User, ghPrompt) };
                bool ghOk = await StreamAgentAsync(_githubAgent, ghSession, ghMessages, interaction);
                if (!ghOk)
                {
                    interaction.Log("[Pipeline] X FASE 3 falló.");
                    return false;
                }

                interaction.UpdatePhase("done");
                interaction.Log("[Pipeline] PIPELINE COMPLETADO EXITOSAMENTE");

                // Backup ZIP
                try
                {
                    string zipPath = Path.Combine(Path.GetTempPath(), $"{runContext.RunId}.zip");
                    System.IO.Compression.ZipFile.CreateFromDirectory(runContext.RunDirectory, zipPath);
                    await SupabaseStorageService.UploadZipAsync(zipPath, runContext.RunId);
                    if (File.Exists(zipPath)) File.Delete(zipPath);
                }
                catch { /* Ignorar errores de backup */ }

                return true;
            }
        }

        private static async Task<bool> StreamAgentAsync(AIAgent agent, AgentSession session, List<ChatMessage> messages, IPipelineInteraction interaction, int maxTurns = 20)
        {
            int turn = 0;
            int transientRetryCount = 0;

            while (turn < maxTurns)
            {
                turn++;
                try
                {
                    interaction.Log($"[Sub-agent] Turno {turn}...");
                    var accumulatedText = new StringBuilder();
                    bool toolCalled = false;
                    ToolApprovalRequestContent? pendingApproval = null;

                    await foreach (var update in agent.RunStreamingAsync(messages, session))
                    {
                        if (!string.IsNullOrEmpty(update.Text))
                        {
                            Console.Write(update.Text);
                            accumulatedText.Append(update.Text);
                        }

                        if (update.Contents != null)
                        {
                            if (update.Contents.Any(c => c is FunctionCallContent))
                            {
                                toolCalled = true;
                            }
                            
                            // Detección del patrón nativo de HITL (ApprovalRequiredAIFunction)
                            var approvalReq = update.Contents.FirstOrDefault(c => c is ToolApprovalRequestContent) as ToolApprovalRequestContent;
                            if (approvalReq != null)
                            {
                                pendingApproval = approvalReq;
                            }
                        }
                    }

                    Console.WriteLine();
                    string response = accumulatedText.ToString();
                    
                    if (!string.IsNullOrEmpty(response) || toolCalled)
                    {
                        messages.Add(new ChatMessage(ChatRole.Assistant, response));
                    }

                    // Manejo nativo de Human-in-the-loop (HITL)
                    if (pendingApproval != null)
                    {
                        var toolName = (pendingApproval.ToolCall as FunctionCallContent)?.Name ?? "unknown tool";
                        interaction.Log($"[Sub-agent] El framework requiere aprobacion para la herramienta: {toolName}");
                        string? userIn = await interaction.RequestUserInputAsync($"Se requiere aprobacion para '{toolName}'. Confirma (y/n):");
                        if (string.IsNullOrWhiteSpace(userIn)) userIn = "n";
                        
                        string normalized = userIn.Trim().ToLowerInvariant();
                        bool isApproved = normalized == "y" || normalized == "yes" || normalized == "s" || normalized == "si";
                        
                        messages.Add(new ChatMessage(ChatRole.User, new List<AIContent> 
                        { 
                            new ToolApprovalResponseContent(pendingApproval.RequestId, isApproved, pendingApproval.ToolCall!) 
                        }));
                        continue;
                    }

                    // Check completion marker
                    if (response.Contains("[TASK_COMPLETED]", StringComparison.OrdinalIgnoreCase))
                    {
                        interaction.Log("[Sub-agent] Tarea marcada como completada.");
                        return true;
                    }

                    // Si no hubo herramientas ni marcador, el agente se detuvo. Forzamos continuación.
                    if (!toolCalled)
                    {
                        interaction.Log("[Sub-agent] El agente se detuvo sin terminar. Solicitando continuación...");
                        messages.Add(new ChatMessage(ChatRole.User, "Please continue until the task is fully finished. Remember to output [TASK_COMPLETED] at the very end."));
                    }
                }
                catch (Exception ex)
                {
                    interaction.Log($"[Error] {ex.Message}");

                    if (transientRetryCount < MaxStreamingRetries)
                    {
                        transientRetryCount++;
                        
                        if (RetryPolicy.IsTransient(ex))
                        {
                            interaction.Log("[Pipeline] Reintentando por error transitorio...");
                        }
                        else
                        {
                            messages.Add(new ChatMessage(ChatRole.User, $"Hubo un error en la ejecución: {ex.Message}. Por favor, revisa tus herramientas e intenta de nuevo."));
                        }

                        await Task.Delay(RetryPolicy.ComputeExponentialBackoff(transientRetryCount));
                        continue;
                    }

                    return false;
                }
            }

            interaction.Log("[Error] Límite de turnos alcanzado.");
            return false;
        }
    }
}