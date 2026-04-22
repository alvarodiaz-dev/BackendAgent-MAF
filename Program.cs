using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.ComponentModel;
using System.Collections.Generic;
using System.Text;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OllamaSharp;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using DotNetEnv;

namespace BasicAgent
{
    class Program
    {
        // ── Tools ────────────────────────────────────────────────────────────
        [Description("Write content to a file.")]
        static void WriteFile(
            [Description("The path to the file to write (relative or absolute).")] string path,
            [Description("The content to write into the file.")] string content = "")
        {
            string fullPath = ResolvePath(path);
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(fullPath, content);
            Console.WriteLine($"[Tool Executed] WriteFile → {fullPath}");
        }

        [Description("Create a directory.")]
        static void CreateDirectory(
            [Description("The path of the directory to create.")] string path)
        {
            string fullPath = ResolvePath(path);
            Directory.CreateDirectory(fullPath);
            Console.WriteLine($"[Tool Executed] CreateDirectory → {fullPath}");
        }

        [Description("Read content from a file.")]
        static string ReadFile(
            [Description("The path to the file to read.")] string path)
        {
            string fullPath = ResolvePath(path);
            if (!File.Exists(fullPath))
            {
                Console.WriteLine($"[Tool Warning] ReadFile — not found: {fullPath}");
                return "Error: File not found.";
            }
            Console.WriteLine($"[Tool Executed] ReadFile → {fullPath}");
            return File.ReadAllText(fullPath);
        }

        [Description("Execute a shell command locally.")]
        static async Task<string> RunShellCommand(
        [Description("The command to execute.")] string command,
        [Description("The working directory for the command.")] string workingDirectory)
        {
            try
            {
                Console.WriteLine($"[Tool Executed] Running command: {command} in {workingDirectory}");

                bool isWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
                string fileName = isWindows ? "cmd.exe" : "/bin/bash";

                // Corrección de comillas para cmd.exe en Windows para evitar que se rompan comandos encadenados con &&
                string arguments = isWindows ? $"/c {command}" : $"-c \"{command}\"";

                var processInfo = new System.Diagnostics.ProcessStartInfo(fileName, arguments)
                {
                    WorkingDirectory = workingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = System.Diagnostics.Process.Start(processInfo);
                if (process == null) return "Error: Could not start process.";

                // Leemos de forma ASÍNCRONA y concurrente para evitar el Deadlock de los buffers de OS
                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();

                // Agregamos un Timeout de 30 segundos. Si git se queda esperando input, lo matamos.
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
                try
                {
                    await process.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    process.Kill(entireProcessTree: true);
                    return $"Error: Command timed out after 30 seconds. Command was probably waiting for user input: {command}";
                }

                string output = await outputTask;
                string error = await errorTask;

                var result = string.IsNullOrEmpty(error) ? output : $"Output: {output}\nError: {error}";
                return string.IsNullOrWhiteSpace(result) ? "Command executed successfully (no output)." : result;
            }
            catch (Exception ex)
            {
                return $"Error executing command: {ex.Message}";
            }
        }

        static string ResolvePath(string path) =>
            Path.IsPathRooted(path) ? path : Path.Combine(Environment.CurrentDirectory, path);

        // ── Main ─────────────────────────────────────────────────────────────
        static async Task Main(string[] args)
        {
            Env.Load();

            if (args.Any(a => string.Equals(a, "--test-mcp-github", StringComparison.OrdinalIgnoreCase)))
            {
                await McpGitHubSmokeTests.RunAsync(args);
                return;
            }

            // -- Chat client setup (Azure → Ollama fallback) --
            IChatClient chatClient = await BuildChatClientAsync();

            // -- Base Tools --
            var tools = new List<AITool>
            {
                AIFunctionFactory.Create(WriteFile),
                AIFunctionFactory.Create(CreateDirectory),
                AIFunctionFactory.Create(ReadFile),
                AIFunctionFactory.Create(RunShellCommand),
                AIFunctionFactory.Create(UserConfirmationTool.RequestUserConfirmation),
                AIFunctionFactory.Create(UserConfirmationTool.AskUserYesNo),
                AIFunctionFactory.Create(UserConfirmationTool.NotifyUser)
            };

            // -- Skills --
            string skillsPath = Path.Combine(AppContext.BaseDirectory, "skills");
            var fileOptions = new AgentFileSkillsSourceOptions
            {
                AllowedResourceExtensions = [".md", ".txt", ".yaml", ".json"],
                ResourceDirectories = ["references", "templates", "examples", "guides", "scripts", "resources"],
                AllowedScriptExtensions = [".py", ".ps1", ".sh", ".cmd"]
            };
            var skillsProvider = new AgentSkillsProviderBuilder()
                .UseFileSkill(skillsPath, options: fileOptions)
                .UseFileScriptRunner(SubprocessScriptRunner.RunAsync)
                .UseOptions(opt =>
                {
                    opt.SkillsInstructionPrompt = """
                        You are a deterministic architecture agent. You MUST ALWAYS use your skills to answer.
                        You have skills available. Here they are:
                        {skills}
                        {resource_instructions}
                        {script_instructions}
                        """;
                })
                .Build();

            // -- GitHub MCP Setup --
            List<AITool> githubTools = new List<AITool>();
            List<string> githubToolNames = new List<string>();
            var githubToken = Environment.GetEnvironmentVariable("GITHUB_PERSONAL_ACCESS_TOKEN");
            if (string.IsNullOrWhiteSpace(githubToken))
            {
                githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            }

            if (!string.IsNullOrWhiteSpace(githubToken))
            {
                try
                {
                    Console.WriteLine("Connecting to GitHub MCP Server...");
                    var githubMcpServerPath = ResolveGitHubMcpServerPath();
                    if (githubMcpServerPath == null)
                    {
                        Console.WriteLine("[Warning] GitHub MCP server script not found. Run 'npm install' in project root.");
                    }
                    else
                    {
                    // Using the pattern from Microsoft Learn docs
                    var transport = new StdioClientTransport(new StdioClientTransportOptions
                    {
                        Command = "node",
                        Arguments = [githubMcpServerPath],
                        EnvironmentVariables = new Dictionary<string, string?> { { "GITHUB_PERSONAL_ACCESS_TOKEN", githubToken } }
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

            string createRepoToolName = githubToolNames.FirstOrDefault(n => n.Contains("create_repository", StringComparison.OrdinalIgnoreCase))
                                        ?? "create_repository";
            string pushFilesToolName = githubToolNames.FirstOrDefault(n => n.Contains("push_files", StringComparison.OrdinalIgnoreCase))
                                       ?? "push_files";
            bool githubMcpReady = githubTools.Count > 0;

            // -- Agents Creation --
            AIAgent docAgent = BuildAgent(chatClient, "DocumentationAgent",
                "You are a deterministic architecture agent. You MUST execute the " +
                "'ibk-architecture-documentation' skill and generate ALL necesary files. " +
                "Ensure the '06-API-CONTRACTS.md' file is created in the " +
                "'_bmad-output/documentation' directory.",
                tools, skillsProvider);

            AIAgent msAgent = BuildAgent(chatClient, "MicroservicesAgent",
                "You are a deterministic architecture agent. You MUST execute the " +
                "'ibk-smp-microservices' skill. Use the provided API contract content " +
                "to generate the microservices code. Proceed automatically through ALL phases " +
                "(Specification, Planning, Implementation, Verification, Publication). " +
                "If during execution you need user confirmation, use the 'RequestUserConfirmation' tool. " +
                "Assume all specs and plans are APPROVED unless explicitly told otherwise.",
                tools, skillsProvider);

            // Combina las herramientas base (incluyendo RunShellCommand) con las del MCP
            var githubAgentTools = tools.Concat(githubTools).ToList();
            AIAgent githubAgent = BuildAgent(chatClient, "GithubAgent",
                "Eres un ingeniero DevOps automatizado. Tu objetivo es publicar el proyecto generado en GitHub. " +
                "DEBES seguir estos pasos: " +
                "1. Usa 'RunShellCommand' para hacer 'git init', 'git add .', y 'git commit -m \"feat: initial commit\"' en la ruta local del proyecto. " +
                $"2. Usa la herramienta MCP '{createRepoToolName}' para crear el repo remoto y captura el 'clone_url'. " +
                "3. Publica TODO el código usando MCP cuando sea posible (por ejemplo, herramienta de carga masiva de archivos) " +
                $"y, si no fuera posible, usa 'RunShellCommand' para 'git branch -M main', 'git remote add origin <URL>' y 'git push -u origin main'. Tool MCP sugerida para carga: '{pushFilesToolName}'.",
                githubAgentTools, null); // githubAgent no necesita skillsProvider

            Console.WriteLine("\n========================================================");
            Console.WriteLine("Pipeline listo. Escribe tu solicitud y presiona Enter.");
            Console.WriteLine("========================================================");

            while (true)
            {
                Console.Write("\n> ");
                var input = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(input)) continue;
                if (input.Equals("exit", StringComparison.OrdinalIgnoreCase)) break;

                await RunPipelineAsync(docAgent, msAgent, githubAgent, input, githubMcpReady);
            }
        }

        // ── Orquestador Manual Secuencial ────────────────────────────────────────────
        static async Task RunPipelineAsync(AIAgent docAgent, AIAgent msAgent, AIAgent githubAgent, string userInput, bool githubMcpReady)
        {
            // ── FASE 1: DocumentationAgent ──
            Console.WriteLine("\n[Pipeline] ▶ FASE 1 — Iniciando DocumentationAgent");
            var docSession = await docAgent.CreateSessionAsync();
            var docPrompt = $"Please execute the 'ibk-architecture-documentation' skill to " +
                            $"generate the architecture documentation based on this request: {userInput}";

            bool docOk = await StreamAgentAsync(docAgent, docSession, new ChatMessage(ChatRole.User, docPrompt));
            if (!docOk)
            {
                Console.WriteLine("[Pipeline] ✗ FASE 1 falló. Abortando pipeline.");
                return;
            }

            // ── Esperar el archivo de contrato ──
            Console.WriteLine("\n[Pipeline] Buscando archivo 06-API-CONTRACTS.md...");
            string? contractPath = await WaitForContractFileAsync(maxRetries: 30, delayMs: 2000);

            if (contractPath == null)
            {
                Console.WriteLine("[Pipeline] ✗ Error: No se encontró el contrato. Abortando pipeline.");
                return;
            }

            Console.WriteLine($"[Pipeline] ✓ Contrato detectado: {contractPath}");
            string contractContent = await File.ReadAllTextAsync(contractPath);

            // ── FASE 2: MicroservicesAgent ──
            Console.WriteLine("\n[Pipeline] ▶ FASE 2 — Iniciando MicroservicesAgent");
            var msSession = await msAgent.CreateSessionAsync();
            var msPrompt = $"""
                FASE 1 COMPLETADA. INICIANDO FASE 2 AUTOMÁTICAMENTE.

                Ejecuta la skill 'ibk-smp-microservices' usando el siguiente contrato como input.
                IMPORTANTE: Completa TODAS las fases (Specification, Planning, Implementation,
                Verification, Publication) de forma AUTOMÁTICA.

                CONTENIDO DEL CONTRATO:
                {contractContent}
                """;

            bool msOk = await StreamAgentAsync(msAgent, msSession, new ChatMessage(ChatRole.User, msPrompt));
            if (!msOk)
            {
                Console.WriteLine("[Pipeline] ✗ FASE 2 falló. Abortando pipeline.");
                return;
            }

            // ── Buscar la carpeta generada del microservicio ──
            Console.WriteLine("\n[Pipeline] Buscando directorio del microservicio generado...");
            string baseDir = Environment.CurrentDirectory;
            var projectDir = Directory.GetDirectories(baseDir, "ibkteam-smp-*-service", SearchOption.AllDirectories)
                                      .OrderByDescending(d => Directory.GetCreationTime(d))
                                      .FirstOrDefault();

            if (projectDir == null)
            {
                Console.WriteLine("[Pipeline] ✗ Error: No se encontró el proyecto de código. Abortando publicación.");
                return;
            }

            string projectName = Path.GetFileName(projectDir);
            Console.WriteLine($"[Pipeline] ✓ Proyecto detectado: {projectName} en {projectDir}");

            if (!githubMcpReady)
            {
                Console.WriteLine("[Pipeline] ✗ GitHub MCP no está disponible. Se omite FASE 3 para evitar publicación incompleta.");
                Console.WriteLine("[Pipeline] Finalizó hasta FASE 2. Revisa token/ruta MCP e inténtalo nuevamente.");
                return;
            }

            // ── FASE 3: GithubAgent ──
            Console.WriteLine("\n[Pipeline] ▶ FASE 3 — Iniciando GithubAgent");
            var ghSession = await githubAgent.CreateSessionAsync();
            var ghPrompt = $"""
                FASE 2 COMPLETADA. INICIANDO PUBLICACIÓN A GITHUB.
                
                Nombre del proyecto: {projectName}
                Ruta local absoluta: {projectDir}
                
                Instrucciones exactas:
                1. Inicializa git localmente en la ruta provista y haz tu primer commit.
                2. Crea el repositorio remoto en GitHub usando el nombre del proyecto.
                3. Conecta el remoto y haz push del código.
                """;

            bool ghOk = await StreamAgentAsync(githubAgent, ghSession, new ChatMessage(ChatRole.User, ghPrompt));
            if (!ghOk)
            {
                Console.WriteLine("[Pipeline] ✗ FASE 3 falló. Revisa logs y vuelve a intentar.");
                return;
            }

            Console.WriteLine("\n[Pipeline] ✓✨ PIPELINE COMPLETADO EXITOSAMENTE ✨✓");
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        static async Task<bool> StreamAgentAsync(AIAgent agent, AgentSession session, ChatMessage message)
        {
            var messages = new List<ChatMessage> { message };
            
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
                    
                    // Detectar si la respuesta solicita confirmación
                    string response = accumulatedResponse.ToString().ToLower();
                    bool needsConfirmation = ContainsConfirmationRequest(response);

                    if (needsConfirmation)
                    {
                        // Pedir confirmación al usuario
                        Console.WriteLine("\n[Pipeline] ⚠  La skill está pidiendo confirmación para continuar.");
                        Console.Write("[Pipeline] ¿Deseas continuar? (s/n): ");
                        
                        string? userResponse = Console.ReadLine();
                        if (string.IsNullOrWhiteSpace(userResponse))
                            userResponse = "n";

                        if (userResponse.Equals("s", StringComparison.OrdinalIgnoreCase) || 
                            userResponse.Equals("y", StringComparison.OrdinalIgnoreCase))
                        {
                            // Usuario aprobó, agregar su confirmación a los mensajes y continuar
                            messages.Add(new ChatMessage(ChatRole.Assistant, accumulatedResponse.ToString()));
                            messages.Add(new ChatMessage(ChatRole.User, 
                                "✓ Confirmado. Por favor continúa con el siguiente paso."));
                            Console.WriteLine("[Pipeline] ✓ Continuando con la ejecución...\n");
                        }
                        else
                        {
                            // Usuario rechazó
                            Console.WriteLine("[Pipeline] ✗ Ejecución cancelada por el usuario.");
                            return false;
                        }
                    }
                    else
                    {
                        // No hay confirmación solicitada, terminar exitosamente
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"\n[Error en Agente] {ex.Message}");
                    return false;
                }
            }
        }

        static bool ContainsConfirmationRequest(string response)
        {
            // Palabras clave que indican que la skill está pidiendo confirmación
            var confirmationKeywords = new[]
            {
                "confirm",
                "approval",
                "approve",
                "approve the",
                "request user confirmation",
                "please confirm",
                "confirm the",
                "aprobación",
                "confirma",
                "aprueba",
                "solicito",
                "solicita confirmación",
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
                "requiere confirmación",
                "approve before"
            };

            foreach (var keyword in confirmationKeywords)
            {
                if (response.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        static async Task<string?> WaitForContractFileAsync(int maxRetries = 30, int delayMs = 2000)
        {
            string baseDir = Environment.CurrentDirectory;
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

        static AIAgent BuildAgent(
            IChatClient chatClient,
            string name,
            string instructions,
            List<AITool> tools,
            AgentSkillsProvider? skillsProvider)
        {
            var options = new ChatClientAgentOptions
            {
                Name = name,
                ChatOptions = new ChatOptions
                {
                    Instructions = instructions,
                    Tools = tools
                }
            };

            if (skillsProvider != null)
            {
                options.AIContextProviders = [skillsProvider];
            }

            return new ChatClientAgent(chatClient, options);
        }

        static async Task<IChatClient> BuildChatClientAsync()
        {
            var endpointUrl = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")
                              ?? "https://REPLACE_WITH_YOUR_ENDPOINT.openai.azure.com/";
            var apiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY") ?? "***";
            var deploymentName = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_NAME")
                                 ?? "gpt-4o-mini";

            Console.WriteLine("[System] Inicializando Azure OpenAI...");
            var azureClient = new AzureOpenAIClient(new Uri(endpointUrl), new AzureKeyCredential(apiKey));
            IChatClient chatClient = azureClient.GetChatClient(deploymentName).AsIChatClient();

            try
            {
                Console.WriteLine("[System] Probando conexión con la API...");
                await chatClient.GetResponseAsync("test");
                Console.WriteLine("[System] ✓ Azure OpenAI conectado.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n[Warning] Azure falló: {ex.Message}");
                Console.WriteLine("[System] Usando Ollama local (fallback)...\n");
                var http = new System.Net.Http.HttpClient
                {
                    BaseAddress = new Uri("http://localhost:11434"),
                    Timeout = TimeSpan.FromMinutes(30)
                };
                var ollama = new OllamaApiClient(http);
                ollama.SelectedModel = "minimax-m2.7:cloud";
                chatClient = ollama;
            }

            return chatClient;
        }

        static string? ResolveGitHubMcpServerPath()
        {
            const string relativeMcpPath = "node_modules\\@modelcontextprotocol\\server-github\\dist\\index.js";

            var startDirs = new[]
            {
                Environment.CurrentDirectory,
                Directory.GetCurrentDirectory(),
                AppContext.BaseDirectory
            }
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

            foreach (var startDir in startDirs)
            {
                var dir = new DirectoryInfo(startDir);
                while (dir != null)
                {
                    string candidate = Path.Combine(dir.FullName, relativeMcpPath);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }

                    dir = dir.Parent;
                }
            }

            return null;
        }
    }
}