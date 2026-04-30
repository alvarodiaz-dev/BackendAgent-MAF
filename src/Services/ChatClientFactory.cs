using System;
using System.ClientModel;
using System.Threading.Tasks;
using Azure;
using Azure.AI.OpenAI;
using BasicAgent.Infrastructure;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OpenAI;

using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BasicAgent.Services
{
    internal class LangfuseEnrichmentClient(IChatClient innerClient, string modelName)
        : DelegatingChatClient(innerClient)
    {
        private static readonly ActivitySource LlmActivitySource =
            new("BasicAgent.LLMGeneration");

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        // ─────────────────────────────────────────────────────────────────────
        // BuildSummarizedInput
        //
        // El "input real" de cada turno LLM es el ÚLTIMO mensaje del historial.
        // Puede ser:
        //   - role=user      → texto del usuario
        //   - role=tool      → resultado(s) de herramienta(s) ejecutadas
        //   - role=assistant → raro, pero posible
        //
        // FIX: Para role=tool, antes salía content="" porque FunctionResultContent
        // no es un TextContent. Ahora lo detectamos explícitamente y extraemos
        // los resultados reales de cada tool call.
        // ─────────────────────────────────────────────────────────────────────
        private static string BuildSummarizedInput(IList<ChatMessage> messages)
        {
            if (messages.Count == 0) return "[]";

            var lastMsg = messages[^1];
            object currentInput;

            if (lastMsg.Role == ChatRole.Tool)
            {
                // Último mensaje = resultado(s) de tool call(s)
                var toolResults = ExtractToolResults(lastMsg);
                currentInput = new
                {
                    role         = "tool",
                    tool_results = toolResults,
                    count        = toolResults.Count
                };
            }
            else
            {
                // Mensaje de usuario o assistant
                var text      = ExtractTextContent(lastMsg);
                const int Max = 1500;
                var truncated = text.Length > Max;
                currentInput  = new
                {
                    role      = lastMsg.Role.Value,
                    content   = truncated ? text[..Max] + $"\n...[truncated {text.Length} chars total]" : text,
                    truncated
                };
            }

            var summary = new
            {
                context_turns   = messages.Count - 1,
                context_summary = messages.Count > 1
                    ? string.Join(", ", messages
                        .Take(messages.Count - 1)
                        .GroupBy(m => m.Role.Value)
                        .Select(g => $"{g.Count()} {g.Key}"))
                    : "none",
                current_input = currentInput
            };

            return JsonSerializer.Serialize(summary, JsonOpts);
        }

        private static string ExtractTextContent(ChatMessage msg)
        {
            if (msg.Contents == null) return string.Empty;
            var sb = new StringBuilder();
            foreach (var c in msg.Contents)
                if (c is TextContent tc) sb.Append(tc.Text);
            return sb.ToString();
        }

        /// <summary>
        /// Extrae los resultados de FunctionResultContent de un mensaje role=tool.
        /// Devuelve lista de objetos { call_id, result } para mostrar en Langfuse.
        /// </summary>
        private static List<object> ExtractToolResults(ChatMessage msg)
        {
            var results = new List<object>();
            if (msg.Contents == null) return results;

            foreach (var c in msg.Contents)
            {
                if (c is not FunctionResultContent frc) continue;

                var raw       = frc.Result?.ToString() ?? "(null)";
                const int Max = 500;
                var truncated = raw.Length > Max;

                results.Add(new
                {
                    call_id   = frc.CallId,
                    result    = truncated ? raw[..Max] + $"...[{raw.Length} chars]" : raw,
                    truncated
                });
            }

            return results;
        }

        // ─────────────────────────────────────────────────────────────────────
        // BuildOutput
        // Captura texto + tool calls del response del LLM.
        // ─────────────────────────────────────────────────────────────────────
        private static string BuildOutput(ChatResponse response)
        {
            var parts = new List<object>();

            foreach (var msg in response.Messages)
            {
                if (msg.Contents == null) continue;
                foreach (var content in msg.Contents)
                {
                    switch (content)
                    {
                        case TextContent tc when !string.IsNullOrWhiteSpace(tc.Text):
                            parts.Add(new { type = "text", text = tc.Text });
                            break;
                        case FunctionCallContent fcc:
                            parts.Add(new
                            {
                                type      = "tool_call",
                                name      = fcc.Name,
                                call_id   = fcc.CallId,
                                arguments = fcc.Arguments
                            });
                            break;
                    }
                }
            }

            return parts.Count == 0
                ? "\"(no output)\""
                : JsonSerializer.Serialize(parts, JsonOpts);
        }

        // ─────────────────────────────────────────────────────────────────────
        // CreateToolCallSpan
        //
        // INPUT del span  = argumentos que pasó el LLM a la herramienta
        // OUTPUT del span = resultado real que devolvió la herramienta
        //
        // El resultado se busca en el historial acumulado de mensajes.
        // El historial es multi-turno acumulativo: cuando el LLM llama a
        // WriteFile en el turno N, en el turno N+1 el historial ya tiene el
        // FunctionResultContent con el resultado. Como nuestro middleware ve
        // todos los mensajes en cada turno, podemos buscarlo retrospectivamente.
        //
        // Si el callId no aparece en el historial actual (turno en curso),
        // queda como "pending" — es correcto, llegará en el próximo turno.
        // ─────────────────────────────────────────────────────────────────────
        private static void CreateToolCallSpan(
            string name,
            string? callId,
            object? arguments,
            Activity parentActivity,
            IList<ChatMessage> messageHistory)
        {
            using var toolSpan = LlmActivitySource.StartActivity(
                $"ToolCall {name}",
                ActivityKind.Internal,
                parentContext: parentActivity.Context);

            if (toolSpan == null) return;

            var argsJson     = arguments != null
                ? JsonSerializer.Serialize(arguments, JsonOpts)
                : "{}";

            var toolOutput   = FindToolResult(callId, messageHistory);

            toolSpan.SetTag("langfuse.observation.type",   "span");
            toolSpan.SetTag("langfuse.observation.name",   $"ToolCall: {name}");
            toolSpan.SetTag("langfuse.observation.input",  argsJson);
            toolSpan.SetTag("langfuse.observation.output", toolOutput);
            toolSpan.SetTag("gen_ai.tool.name",            name);
            toolSpan.SetTag("gen_ai.tool.call_id",         callId ?? "unknown");
        }

        /// <summary>
        /// Busca el resultado de un tool call específico en el historial.
        /// Los mensajes role=tool contienen FunctionResultContent con el callId
        /// correspondiente al FunctionCallContent que los originó.
        /// </summary>
        private static string FindToolResult(string? callId, IList<ChatMessage> history)
        {
            if (callId == null) return "\"(no call_id)\"";

            foreach (var msg in history)
            {
                if (msg.Role != ChatRole.Tool || msg.Contents == null) continue;

                foreach (var c in msg.Contents)
                {
                    if (c is not FunctionResultContent frc) continue;
                    if (frc.CallId != callId) continue;

                    var raw       = frc.Result?.ToString() ?? "(null)";
                    const int Max = 800;
                    var result    = raw.Length > Max
                        ? raw[..Max] + $"...[{raw.Length} chars total]"
                        : raw;

                    return JsonSerializer.Serialize(result);
                }
            }

            // El resultado llegará en el próximo turno — normal para el turno actual
            return "\"(pending — result arrives in next turn)\"";
        }

        // ─────────────────────────────────────────────────────────────────────
        // GetResponseAsync
        // ─────────────────────────────────────────────────────────────────────
        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var messageList = chatMessages.ToList();
            var inputJson   = BuildSummarizedInput(messageList);

            using var activity = LlmActivitySource.StartActivity(
                $"ChatCompletion {modelName}", ActivityKind.Client);

            if (activity != null)
            {
                activity.SetTag("langfuse.observation.type",  "generation");
                activity.SetTag("langfuse.observation.name",  $"ChatCompletion {modelName}");
                activity.SetTag("langfuse.observation.input", inputJson);
                activity.SetTag("gen_ai.system",              "openai");
                activity.SetTag("gen_ai.request.model",       modelName);
                activity.SetTag("input.value",                inputJson);
                activity.SetTag("llm.context_turns",          messageList.Count);
            }

            try
            {
                var response = await base.GetResponseAsync(messageList, options, cancellationToken);

                if (activity != null)
                {
                    var outputJson = BuildOutput(response);
                    activity.SetTag("langfuse.observation.output", outputJson);
                    activity.SetTag("output.value",                outputJson);

                    if (!string.IsNullOrWhiteSpace(response.ModelId))
                        activity.SetTag("gen_ai.response.model", response.ModelId);

                    if (response.Usage != null)
                    {
                        activity.SetTag("gen_ai.usage.input_tokens",  response.Usage.InputTokenCount);
                        activity.SetTag("gen_ai.usage.output_tokens", response.Usage.OutputTokenCount);
                        activity.SetTag("langfuse.usage.input",       response.Usage.InputTokenCount);
                        activity.SetTag("langfuse.usage.output",      response.Usage.OutputTokenCount);
                    }

                    foreach (var msg in response.Messages)
                    {
                        if (msg.Contents == null) continue;
                        foreach (var c in msg.Contents)
                            if (c is FunctionCallContent fcc)
                                CreateToolCallSpan(fcc.Name, fcc.CallId, fcc.Arguments,
                                    activity, messageList);
                    }
                }

                return response;
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                throw;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // GetStreamingResponseAsync
        // ─────────────────────────────────────────────────────────────────────
        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var messageList = chatMessages.ToList();
            var inputJson   = BuildSummarizedInput(messageList);

            using var activity = LlmActivitySource.StartActivity(
                $"ChatStreaming {modelName}", ActivityKind.Client);

            if (activity != null)
            {
                activity.SetTag("langfuse.observation.type",  "generation");
                activity.SetTag("langfuse.observation.name",  $"ChatStreaming {modelName}");
                activity.SetTag("langfuse.observation.input", inputJson);
                activity.SetTag("gen_ai.system",              "openai");
                activity.SetTag("gen_ai.request.model",       modelName);
                activity.SetTag("input.value",                inputJson);
                activity.SetTag("llm.context_turns",          messageList.Count);
            }

            var fullText  = new StringBuilder();
            var toolCalls = new List<(string Name, string? CallId, object? Arguments)>();

            await foreach (var update in base.GetStreamingResponseAsync(messageList, options, cancellationToken))
            {
                if (!string.IsNullOrEmpty(update.Text))
                    fullText.Append(update.Text);

                if (update.Contents != null)
                    foreach (var c in update.Contents)
                        if (c is FunctionCallContent fcc)
                            toolCalls.Add((fcc.Name, fcc.CallId, fcc.Arguments));

                yield return update;
            }

            if (activity != null)
            {
                var parts = new List<object>();
                if (fullText.Length > 0)
                    parts.Add(new { type = "text", text = fullText.ToString() });
                foreach (var (name, callId, args) in toolCalls)
                    parts.Add(new { type = "tool_call", name, call_id = callId, arguments = args });

                var outputJson = parts.Count > 0
                    ? JsonSerializer.Serialize(parts, JsonOpts)
                    : "\"(no output)\"";

                activity.SetTag("langfuse.observation.output", outputJson);
                activity.SetTag("output.value",                outputJson);

                foreach (var (name, callId, args) in toolCalls)
                    CreateToolCallSpan(name, callId, args, activity, messageList);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ChatClientFactory
    // ─────────────────────────────────────────────────────────────────────────
    internal static class ChatClientFactory
    {
        private static readonly System.Net.Http.HttpClient SharedHttpClient = new()
        {
            Timeout = TimeSpan.FromMinutes(30)
        };

        public static async Task<IChatClient> BuildAsync()
        {
            var preferredProvider = Environment.GetEnvironmentVariable("LLM_PROVIDER")?.ToLowerInvariant();
            Console.WriteLine($"[System] LLM_PROVIDER detectado: '{preferredProvider}'");

            if (preferredProvider == "azure")
                return await TryBuildAzureOpenAiClient() ?? BuildOllamaClient();

            if (preferredProvider == "litellm")
            {
                var client = await TryBuildLiteLlmClient();
                if (client != null) return client;
                Console.WriteLine("[Warning] No se pudo inicializar LiteLLM, cayendo a Ollama...");
                return BuildOllamaClient();
            }

            Console.WriteLine("[System] Usando proveedor por defecto (Ollama).");
            return BuildOllamaClient();
        }

        private static async Task<IChatClient?> TryBuildLiteLlmClient()
        {
            try
            {
                var endpoint  = EnvironmentVariables.GetLiteLlmEndpoint();
                var apiKey    = EnvironmentVariables.GetLiteLlmApiKey();
                var modelName = EnvironmentVariables.GetLiteLlmModel();

                if (string.IsNullOrWhiteSpace(endpoint) ||
                    string.IsNullOrWhiteSpace(apiKey)   ||
                    string.IsNullOrWhiteSpace(modelName))
                {
                    Console.WriteLine("[System] LiteLLM no esta configurado correctamente.");
                    return null;
                }

                Console.WriteLine($"[System] Inicializando LiteLLM (Endpoint: {endpoint}, Modelo: {modelName})...");

                IChatClient chatClient = new OpenAIClient(
                    new ApiKeyCredential(apiKey),
                    new OpenAIClientOptions
                    {
                        Endpoint  = new Uri(endpoint.TrimEnd('/')),
                        Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(SharedHttpClient)
                    })
                    .GetChatClient(modelName).AsIChatClient();

                return chatClient.AsBuilder()
                    .Use(inner => new LangfuseEnrichmentClient(inner, modelName))
                    .UseOpenTelemetry(configure: o => o.EnableSensitiveData = true)
                    .Build();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Warning] LiteLLM fallo: {ex.Message}");
                return null;
            }
        }

        private static async Task<IChatClient?> TryBuildAzureOpenAiClient()
        {
            try
            {
                var endpointUrl    = EnvironmentVariables.GetAzureOpenAiEndpoint();
                var apiKey         = EnvironmentVariables.GetAzureOpenAiApiKey();
                var deploymentName = EnvironmentVariables.GetAzureOpenAiDeployment();

                if (endpointUrl.Contains("REPLACE_WITH_YOUR_ENDPOINT") || apiKey == "***")
                    return null;

                Console.WriteLine($"[System] Inicializando Azure OpenAI (Deployment: {deploymentName})...");

                IChatClient chatClient = new AzureOpenAIClient(
                    new Uri(endpointUrl),
                    new AzureKeyCredential(apiKey),
                    new AzureOpenAIClientOptions
                    {
                        Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(SharedHttpClient)
                    })
                    .GetChatClient(deploymentName).AsIChatClient();

                return chatClient.AsBuilder()
                    .Use(inner => new LangfuseEnrichmentClient(inner, deploymentName))
                    .UseOpenTelemetry(configure: o => o.EnableSensitiveData = true)
                    .Build();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Warning] Azure fallo: {ex.Message}");
                return null;
            }
        }

        private static IChatClient BuildOllamaClient()
        {
            var modelName = EnvironmentVariables.GetOllamaModel();
            Console.WriteLine($"[System] Usando Ollama local (modelo: {modelName})...\n");

            if (SharedHttpClient.BaseAddress == null)
                SharedHttpClient.BaseAddress = new Uri("http://localhost:11434");

            return ((IChatClient)new OllamaApiClient(SharedHttpClient) { SelectedModel = modelName })
                .AsBuilder()
                .Use(inner => new LangfuseEnrichmentClient(inner, modelName))
                .UseOpenTelemetry(configure: o => o.EnableSensitiveData = true)
                .Build();
        }
    }
}