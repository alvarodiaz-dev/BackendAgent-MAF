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
        private const int MaxStreamingRetries = 4;

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
                // FASE 1: Documentation
                interaction.UpdatePhase("fase-1-documentation");
                interaction.Log("[Pipeline] > FASE 1 - Iniciando DocumentationAgent");
                
                var docSession = await _docAgent.CreateSessionAsync();
                var docPrompt = $"""
                    Please execute the 'ibk-architecture-documentation' skill to generate the architecture documentation based on this request: {userInput}

                    IMPORTANT OUTPUT RULES:
                    - Save all generated files for this run under the current execution folder.
                    - Use relative paths only.
                    - Ensure '06-API-CONTRACTS.md' is generated under '_bmad-output/documentation' for this run.
                    - Do NOT stop until ALL artifacts (01 to 07) are generated.
                    """;

                string? contractPath = null;
                int docRetries = 0;
                var docMessages = new List<ChatMessage> { new ChatMessage(ChatRole.User, docPrompt) };

                while (docRetries < 3)
                {
                    bool docOk = await StreamAgentAsync(_docAgent, docSession, docMessages, interaction);
                    if (!docOk)
                    {
                        interaction.Log("[Pipeline] X FASE 1 fallo durante la ejecucion del agente.");
                        return false;
                    }

                    interaction.Log("[Pipeline] Verificando integridad de la documentacion...");
                    contractPath = await WaitForContractFileAsync(runContext.RunDirectory, interaction, maxRetries: 5, delayMs: 2000);
                    
                    if (contractPath != null) 
                    {
                        // Validar que el archivo no esté vacío o sea demasiado corto (ej: solo un encabezado)
                        var info = new FileInfo(contractPath);
                        if (info.Length > 500) // Un contrato OpenAPI minimo suele tener mas de 500 bytes
                        {
                            break; 
                        }
                        interaction.Log("[Pipeline] ! El contrato parece estar incompleto (archivo muy pequeño).");
                    }

                    docRetries++;
                    interaction.Log($"[Pipeline] [Reintento {docRetries}/3] Documentacion incompleta. Solicitando continuacion tecnica...");
                    docMessages.Add(new ChatMessage(ChatRole.User, "STILL MISSING: '06-API-CONTRACTS.md'. Continue generating the remaining artifacts now. DO NOT repeat your introduction, language detection, or plan. Just generate the missing files."));
                }

                if (contractPath == null)
                {
                    interaction.Log("[Pipeline] X Error: No se encontro el contrato despues de varios intentos. Abortando pipeline.");
                    return false;
                }

                interaction.Log($"[Pipeline] OK Contrato detectado: {contractPath}");
                string contractContent = await File.ReadAllTextAsync(contractPath);

                // FASE 2: Microservices
                interaction.UpdatePhase("fase-2-microservices");
                interaction.Log("[Pipeline] > FASE 2 - Iniciando MicroservicesAgent");
                
                var msSession = await _msAgent.CreateSessionAsync();
                var msPrompt = $"""
                    FASE 1 COMPLETADA. INICIANDO FASE 2 AUTOMATICAMENTE.

                    Ejecuta la skill 'ibk-smp-microservices' usando el siguiente contrato como input.
                    IMPORTANTE: Completa TODAS las fases (Specification, Planning, Implementation,
                    Verification, Publication) de forma AUTOMATICA.

                    OUTPUT RULES FOR THIS ITERATION:
                    - Save all files under this run folder: {runContext.RunDirectory}
                    - Use relative paths only.

                    CONTENIDO DEL CONTRATO:
                    {contractContent}
                    """;

                var msMessages = new List<ChatMessage> { new ChatMessage(ChatRole.User, msPrompt) };
                bool msOk = await StreamAgentAsync(_msAgent, msSession, msMessages, interaction);
                if (!msOk)
                {
                    interaction.Log("[Pipeline] X FASE 2 fallo. Abortando pipeline.");
                    return false;
                }

                interaction.Log("[Pipeline] Buscando directorio del microservicio generado...");
                string baseDir = runContext.RunDirectory;
                string? projectDir = null;
                
                // Intentar encontrar el directorio por patron
                for (int i = 0; i < 5; i++)
                {
                    projectDir = Directory.GetDirectories(baseDir, "ibkteam-smp-*-service", SearchOption.AllDirectories)
                                          .OrderByDescending(Directory.GetCreationTime)
                                          .FirstOrDefault();
                    if (projectDir != null) break;
                    await Task.Delay(2000);
                }

                if (projectDir == null)
                {
                    interaction.Log("[Pipeline] X Error: No se encontro el proyecto de codigo. Revisa si la skill genero el microservicio.");
                    return false;
                }

                string projectName = Path.GetFileName(projectDir);
                interaction.Log($"[Pipeline] OK Proyecto detectado: {projectName} en {projectDir}");

                // FASE 3: GitHub (Si esta listo)
                if (!_githubMcpReady)
                {
                    interaction.Log("[Pipeline] ! GitHub MCP no esta disponible. Finalizando ejecucion sin publicacion remota.");
                    interaction.Log("[Pipeline] Los archivos locales estan disponibles en: " + runContext.RunDirectory);
                    return true;
                }

                interaction.UpdatePhase("fase-3-github");
                interaction.Log("[Pipeline] > FASE 3 - Iniciando GithubAgent");
                var ghSession = await _githubAgent.CreateSessionAsync();
                var ghPrompt = $"""
                    FASE 2 COMPLETADA. INICIANDO PUBLICACION A GITHUB.

                    Nombre del proyecto: {projectName}
                    Ruta local absoluta: {projectDir}

                    Instrucciones exactas:
                    1. Inicializa git localmente en la ruta provista y haz tu primer commit.
                    2. Crea el repositorio remoto en GitHub usando el nombre del proyecto.
                    3. Conecta el remoto y haz push del codigo.
                    """;

                var ghMessages = new List<ChatMessage> { new ChatMessage(ChatRole.User, ghPrompt) };
                bool ghOk = await StreamAgentAsync(_githubAgent, ghSession, ghMessages, interaction);
                if (!ghOk)
                {
                    interaction.Log("[Pipeline] X FASE 3 fallo. Revisa logs y vuelve a intentar.");
                    return false;
                }

                interaction.UpdatePhase("done");
                interaction.Log("[Pipeline] PIPELINE COMPLETADO EXITOSAMENTE");

                // Persistencia en Supabase Storage (ZIP)
                try
                {
                    interaction.Log("[Pipeline] Generando backup de la sesion (.zip)...");
                    string zipPath = Path.Combine(Path.GetTempPath(), $"{runContext.RunId}.zip");
                    if (File.Exists(zipPath)) File.Delete(zipPath);

                    System.IO.Compression.ZipFile.CreateFromDirectory(runContext.RunDirectory, zipPath);
                    
                    interaction.Log("[Pipeline] Subiendo backup a Supabase Storage...");
                    string? storageUrl = await SupabaseStorageService.UploadZipAsync(zipPath, runContext.RunId);
                    
                    if (storageUrl != null)
                    {
                        interaction.Log($"[Pipeline] OK Backup disponible en: {storageUrl}");
                    }
                    
                    // Limpieza del zip temporal local
                    if (File.Exists(zipPath)) File.Delete(zipPath);
                }
                catch (Exception ex)
                {
                    interaction.Log($"[Warning] No se pudo crear o subir el backup: {ex.Message}");
                }

                return true;
            }
        }

        private static async Task<bool> StreamAgentAsync(AIAgent agent, AgentSession session, List<ChatMessage> messages, IPipelineInteraction interaction)
        {
            int transientRetryCount = 0;

            while (true)
            {
                try
                {
                    interaction.Log("Procesando...");
                    var accumulatedResponse = new StringBuilder();

                    await foreach (var update in agent.RunStreamingAsync(messages, session))
                    {
                        if (!string.IsNullOrEmpty(update.Text))
                        {
                            Console.Write(update.Text);
                            accumulatedResponse.Append(update.Text);
                        }
                    }

                    Console.WriteLine();
                    string fullResponse = accumulatedResponse.ToString();
                    
                    // No llamamos a interaction.Log(fullResponse) aquí para evitar duplicar en consola,
                    // ya que el texto fue impreso carácter a carácter por Console.Write(update.Text).
                    // Sin embargo, para que el estado de la API tenga el log completo, 
                    // simplemente mantenemos el mensaje en el historial del agente.

                    string responseLower = fullResponse.ToLowerInvariant();
                    bool needsConfirmation = ContainsConfirmationRequest(responseLower);

                    if (needsConfirmation)
                    {
                        interaction.Log("[Pipeline] La skill esta pidiendo confirmacion para continuar.");
                        string? userResponse = await interaction.RequestUserInputAsync(
                            "La skill solicita confirmacion. Responde y/s para continuar, n para cancelar, u otro texto para enviar correcciones.");
                        
                        if (string.IsNullOrWhiteSpace(userResponse))
                        {
                            userResponse = "n";
                        }

                        string normalizedResponse = userResponse.Trim().ToLowerInvariant();

                        if (normalizedResponse == "s" || normalizedResponse == "y" || normalizedResponse == "si" || normalizedResponse == "yes")
                        {
                            messages.Add(new ChatMessage(ChatRole.Assistant, fullResponse));
                            messages.Add(new ChatMessage(ChatRole.User, "Confirmado. Por favor continua con el siguiente paso hasta completar la tarea."));
                            interaction.Log("[Pipeline] Continuando con la ejecucion...");
                            continue; // Seguir en el loop while(true) de este agente
                        }
                        else if (normalizedResponse == "n" || normalizedResponse == "no" || normalizedResponse == "cancelar")
                        {
                            interaction.Log("[Pipeline] Ejecucion cancelada por el usuario.");
                            return false;
                        }
                        else
                        {
                            messages.Add(new ChatMessage(ChatRole.Assistant, fullResponse));
                            messages.Add(new ChatMessage(ChatRole.User, userResponse));
                            interaction.Log("[Pipeline] Enviando correcciones al agente...");
                            continue; // Seguir en el loop while(true)
                        }
                    }
                    else
                    {
                        // Agregar la respuesta del asistente al historial para mantener contexto en futuros reintentos/fases si fuera necesario
                        messages.Add(new ChatMessage(ChatRole.Assistant, fullResponse));
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    if (RetryPolicy.IsTransient(ex) && transientRetryCount < MaxStreamingRetries)
                    {
                        transientRetryCount++;
                        var delay = RetryPolicy.ComputeExponentialBackoff(transientRetryCount);
                        interaction.Log($"[Warning] Error transitorio detectado. Reintento {transientRetryCount}/{MaxStreamingRetries} en {delay.TotalSeconds:F1}s...");
                        await Task.Delay(delay);
                        continue;
                    }

                    interaction.Log($"[Error en Agente] {ex.Message}");
                    return false;
                }
            }
        }

        private static bool ContainsConfirmationRequest(string response)
        {
            var confirmationKeywords = new[]
            {
                "confirm",
                "approval",
                "approve",
                "approve the",
                "request user confirmation",
                "please confirm",
                "confirm the",
                "aprobacion",
                "confirma",
                "aprueba",
                "solicito",
                "solicita confirmacion",
                "debe confirmar",
                "must confirm",
                "request confirmation",
                "await",
                "wait for",
                "waiting for",
                "esperando",
                "approval required",
                "requires approval",
                "requires confirmation",
                "requiere confirmacion",
                "approve before"
            };

            foreach (var keyword in confirmationKeywords)
            {
                if (response.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static async Task<string?> WaitForContractFileAsync(string baseDir, IPipelineInteraction interaction, int maxRetries = 30, int delayMs = 2000)
        {
            for (int i = 0; i < maxRetries; i++)
            {
                var found = Directory
                    .GetFiles(baseDir, "*06-API-CONTRACTS.md", SearchOption.AllDirectories)
                    .Where(f => f.Contains("_bmad-output"))
                    .OrderByDescending(f => new FileInfo(f).LastWriteTime)
                    .FirstOrDefault();

                if (found is not null)
                {
                    await Task.Delay(500);
                    return found;
                }

                int remaining = (maxRetries - i - 1) * delayMs / 1000;
                interaction.Log($"[Pipeline] Esperando escritura de disco... {remaining}s restantes");
                await Task.Delay(delayMs);
            }

            return null;
        }
    }
}
