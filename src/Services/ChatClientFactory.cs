using System;
using System.Threading.Tasks;
using Azure;
using Azure.AI.OpenAI;
using BasicAgent.Infrastructure;
using Microsoft.Extensions.AI;
using OllamaSharp;

namespace BasicAgent.Services
{
    internal static class ChatClientFactory
    {
        public static async Task<IChatClient> BuildAsync()
        {
            var endpointUrl = EnvironmentVariables.GetAzureOpenAiEndpoint();
            var apiKey = EnvironmentVariables.GetAzureOpenAiApiKey();
            var deploymentName = EnvironmentVariables.GetAzureOpenAiDeployment();

            Console.WriteLine("[System] Inicializando Azure OpenAI...");
            var azureClient = new AzureOpenAIClient(new Uri(endpointUrl), new AzureKeyCredential(apiKey));
            IChatClient chatClient = azureClient.GetChatClient(deploymentName).AsIChatClient();

            try
            {
                Console.WriteLine("[System] Probando conexion con la API...");
                await chatClient.GetResponseAsync("test");
                Console.WriteLine("[System] Azure OpenAI conectado.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n[Warning] Azure fallo: {ex.Message}");
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

                chatClient = ollama;
            }

            return chatClient;
        }
    }
}
