using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BasicAgent.Infrastructure;
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

        public async Task RunAsync(string userInput)
        {
            var runContext = RunOutputFactory.Create(userInput);
            Console.WriteLine($"\n[Pipeline] Run creado: {runContext.RunId}");
            Console.WriteLine($"[Pipeline] Output de esta ejecucion: {runContext.RunDirectory}");

            using (ProjectPaths.UseRunDirectory(runContext.RunDirectory))
            {
                Console.WriteLine("\n[Pipeline] > FASE 1 - Iniciando DocumentationAgent");
                var docSession = await _docAgent.CreateSessionAsync();
                var docPrompt = $"""
                    Please execute the 'ibk-architecture-documentation' skill to generate the architecture documentation based on this request: {userInput}

                    IMPORTANT OUTPUT RULES:
                    - Save all generated files for this run under the current execution folder.
                    - Use relative paths only.
                    - Ensure '06-API-CONTRACTS.md' is generated under '_bmad-output/documentation' for this run.
                    """;

                bool docOk = await StreamAgentAsync(_docAgent, docSession, new ChatMessage(ChatRole.User, docPrompt));
                if (!docOk)
                {
                    Console.WriteLine("[Pipeline] X FASE 1 fallo. Abortando pipeline.");
                    return;
                }

                Console.WriteLine("\n[Pipeline] Buscando archivo 06-API-CONTRACTS.md...");
                string? contractPath = await WaitForContractFileAsync(runContext.RunDirectory, maxRetries: 30, delayMs: 2000);
                if (contractPath == null)
                {
                    Console.WriteLine("[Pipeline] X Error: No se encontro el contrato. Abortando pipeline.");
                    return;
                }

                Console.WriteLine($"[Pipeline] OK Contrato detectado: {contractPath}");
                string contractContent = await File.ReadAllTextAsync(contractPath);

                Console.WriteLine("\n[Pipeline] > FASE 2 - Iniciando MicroservicesAgent");
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

                bool msOk = await StreamAgentAsync(_msAgent, msSession, new ChatMessage(ChatRole.User, msPrompt));
                if (!msOk)
                {
                    Console.WriteLine("[Pipeline] X FASE 2 fallo. Abortando pipeline.");
                    return;
                }

                Console.WriteLine("\n[Pipeline] Buscando directorio del microservicio generado...");
                string baseDir = runContext.RunDirectory;
                var projectDir = Directory.GetDirectories(baseDir, "ibkteam-smp-*-service", SearchOption.AllDirectories)
                                          .OrderByDescending(Directory.GetCreationTime)
                                          .FirstOrDefault();

                if (projectDir == null)
                {
                    Console.WriteLine("[Pipeline] X Error: No se encontro el proyecto de codigo. Abortando publicacion.");
                    return;
                }

                string projectName = Path.GetFileName(projectDir);
                Console.WriteLine($"[Pipeline] OK Proyecto detectado: {projectName} en {projectDir}");

                if (!_githubMcpReady)
                {
                    Console.WriteLine("[Pipeline] X GitHub MCP no esta disponible. Se omite FASE 3 para evitar publicacion incompleta.");
                    Console.WriteLine("[Pipeline] Finalizo hasta FASE 2. Revisa token/ruta MCP e intentalo nuevamente.");
                    return;
                }

                Console.WriteLine("\n[Pipeline] > FASE 3 - Iniciando GithubAgent");
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

                bool ghOk = await StreamAgentAsync(_githubAgent, ghSession, new ChatMessage(ChatRole.User, ghPrompt));
                if (!ghOk)
                {
                    Console.WriteLine("[Pipeline] X FASE 3 fallo. Revisa logs y vuelve a intentar.");
                    return;
                }

                Console.WriteLine("\n[Pipeline] PIPELINE COMPLETADO EXITOSAMENTE");
            }
        }

        private static async Task<bool> StreamAgentAsync(AIAgent agent, AgentSession session, ChatMessage message)
        {
            var messages = new List<ChatMessage> { message };
            int transientRetryCount = 0;

            while (true)
            {
                try
                {
                    Console.WriteLine("Procesando...\n");
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
                    string response = accumulatedResponse.ToString().ToLowerInvariant();
                    bool needsConfirmation = ContainsConfirmationRequest(response);

                    if (needsConfirmation)
                    {
                        Console.WriteLine("\n[Pipeline] La skill esta pidiendo confirmacion para continuar.");
                        Console.Write("[Pipeline] Deseas continuar? (s/n): ");

                        string? userResponse = Console.ReadLine();
                        if (string.IsNullOrWhiteSpace(userResponse))
                        {
                            userResponse = "n";
                        }

                        if (userResponse.Equals("s", StringComparison.OrdinalIgnoreCase) ||
                            userResponse.Equals("y", StringComparison.OrdinalIgnoreCase))
                        {
                            messages.Add(new ChatMessage(ChatRole.Assistant, accumulatedResponse.ToString()));
                            messages.Add(new ChatMessage(ChatRole.User, "Confirmado. Por favor continua con el siguiente paso."));
                            Console.WriteLine("[Pipeline] Continuando con la ejecucion...\n");
                        }
                        else
                        {
                            Console.WriteLine("[Pipeline] Ejecucion cancelada por el usuario.");
                            return false;
                        }
                    }
                    else
                    {
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    if (RetryPolicy.IsTransient(ex) && transientRetryCount < MaxStreamingRetries)
                    {
                        transientRetryCount++;
                        var delay = RetryPolicy.ComputeExponentialBackoff(transientRetryCount);
                        Console.WriteLine($"\n[Warning] Error transitorio detectado. Reintento {transientRetryCount}/{MaxStreamingRetries} en {delay.TotalSeconds:F1}s...");
                        await Task.Delay(delay);
                        continue;
                    }

                    Console.WriteLine($"\n[Error en Agente] {ex.Message}");
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

        private static async Task<string?> WaitForContractFileAsync(string baseDir, int maxRetries = 30, int delayMs = 2000)
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
                Console.Write($"\r[Pipeline] Esperando escritura de disco... {remaining}s restantes   ");
                await Task.Delay(delayMs);
            }

            Console.WriteLine();
            return null;
        }
    }
}
