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
using System.Threading;

namespace BasicAgent.Services
{
    // Middleware para asegurar que Langfuse vea el input/output de cada llamada a la IA
    internal class LangfuseEnrichmentClient(IChatClient innerClient) : DelegatingChatClient(innerClient)
    {
        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var activity = Activity.Current;
            if (activity != null)
            {
                // Capturamos el último mensaje del usuario como input de esta observación
                var lastUserMessage = chatMessages.LastOrDefault(m => m.Role == ChatRole.User)?.Text 
                                     ?? chatMessages.LastOrDefault()?.Text;
                
                activity.SetTag("langfuse.observation.input", lastUserMessage);
                activity.SetTag("gen_ai.prompt", lastUserMessage);
            }

            try 
            {
                var response = await base.GetResponseAsync(chatMessages, options, cancellationToken);

                if (activity != null)
                {
                    var completion = response.ToString();
                    activity.SetTag("langfuse.observation.output", completion);
                    activity.SetTag("gen_ai.completion", completion);
                }

                return response;
            }
            catch (Exception ex)
            {
                activity?.SetTag("otel.status_code", "ERROR");
                activity?.SetTag("otel.status_description", ex.Message);
                throw;
            }
        }
    }

    internal static class ChatClientFactory
    {
        public static async Task<IChatClient> BuildAsync()
        {
            // Intenta LiteLLM primero si está configurado
            if (EnvironmentVariables.IsLiteLlmConfigured())
            {
                var litellmClient = await TryBuildLiteLlmClient();
                if (litellmClient != null)
                {
                    return litellmClient;
                }
            }

            // Intenta Azure OpenAI
            var azureClient = await TryBuildAzureOpenAiClient();
            if (azureClient != null)
            {
                return azureClient;
            }

            // Fallback a Ollama
            return BuildOllamaClient();
        }

        private static async Task<IChatClient?> TryBuildLiteLlmClient()
        {
            try
            {
                var endpoint = EnvironmentVariables.GetLiteLlmEndpoint();
                var apiKey = EnvironmentVariables.GetLiteLlmApiKey();
                var modelName = EnvironmentVariables.GetLiteLlmModel();

                if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(modelName))
                {
                    return null;
                }

                Console.WriteLine("[System] Inicializando LiteLLM Gateway...");

                // LiteLLM expone un endpoint compatible con OpenAI
                var litellmUri = endpoint.EndsWith("/") ? endpoint.TrimEnd('/') : endpoint;
                var openAiClient = new OpenAIClient(new ApiKeyCredential(apiKey), new OpenAIClientOptions { Endpoint = new Uri(litellmUri) });

                IChatClient chatClient = openAiClient.GetChatClient(modelName).AsIChatClient();

                // Habilitar captura de contenido y enriquecer para Langfuse
                chatClient = chatClient.AsBuilder()
                    .UseOpenTelemetry(configure: o => o.EnableSensitiveData = true)
                    .Use(inner => new LangfuseEnrichmentClient(inner))
                    .Build();

                Console.WriteLine("[System] Probando conexion con LiteLLM...");
                await chatClient.GetResponseAsync("test");
                Console.WriteLine("[System] LiteLLM Gateway conectado.");

                return chatClient;
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
                var endpointUrl = EnvironmentVariables.GetAzureOpenAiEndpoint();
                var apiKey = EnvironmentVariables.GetAzureOpenAiApiKey();
                var deploymentName = EnvironmentVariables.GetAzureOpenAiDeployment();

                // Validar que no sean placeholders
                if (endpointUrl.Contains("REPLACE_WITH_YOUR_ENDPOINT") || apiKey == "***")
                {
                    return null;
                }

                Console.WriteLine("[System] Inicializando Azure OpenAI...");
                var azureClient = new AzureOpenAIClient(new Uri(endpointUrl), new AzureKeyCredential(apiKey));
                IChatClient chatClient = azureClient.GetChatClient(deploymentName).AsIChatClient();

                // Habilitar captura de contenido y enriquecer para Langfuse
                chatClient = chatClient.AsBuilder()
                    .UseOpenTelemetry(configure: o => o.EnableSensitiveData = true)
                    .Use(inner => new LangfuseEnrichmentClient(inner))
                    .Build();

                Console.WriteLine("[System] Probando conexion con la API...");
                await chatClient.GetResponseAsync("test");
                Console.WriteLine("[System] Azure OpenAI conectado.");

                return chatClient;
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

            var ollama = new OllamaApiClient(new Uri("http://localhost:11434"))
            {
                SelectedModel = modelName
            };

            // Lo convertimos a IChatClient, añadimos telemetría y enriquecimiento
            return ((IChatClient)ollama).AsBuilder()
                .UseOpenTelemetry(configure: o => o.EnableSensitiveData = true)
                .Use(inner => new LangfuseEnrichmentClient(inner))
                .Build();
        }
    }
}
