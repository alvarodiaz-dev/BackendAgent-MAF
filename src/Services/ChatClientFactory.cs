using System;
using System.ClientModel;
using System.Threading.Tasks;
using Azure;
using Azure.AI.OpenAI;
using BasicAgent.Infrastructure;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OpenAI;

namespace BasicAgent.Services
{
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
            Console.WriteLine("[System] Usando Ollama local (fallback)...\n");

            var http = new System.Net.Http.HttpClient
            {
                BaseAddress = new Uri("http://localhost:11434"),
                Timeout = TimeSpan.FromMinutes(30)
            };

            var ollama = new OllamaApiClient(http)
            {
                SelectedModel = "minimax-m2.7:cloud"
            };

            return ollama;
        }
    }
}
