using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using BasicAgent.Infrastructure;
using System.Text.Json;

namespace BasicAgent.Services
{
    public class LangfusePromptService
    {
        private readonly HttpClient _httpClient;
        private readonly string _publicKey;
        private readonly string _secretKey;

        public LangfusePromptService()
        {
            _publicKey = EnvironmentVariables.GetLangfusePublicKey() ?? string.Empty;
            _secretKey = EnvironmentVariables.GetLangfuseSecretKey() ?? string.Empty;
            var baseUrl = EnvironmentVariables.GetLangfuseBaseUrl();

            _httpClient = new HttpClient
            {
                BaseAddress = new Uri(baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/")
            };

            var auth = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{_publicKey}:{_secretKey}"));
            _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", auth);
        }

        public async Task<string?> GetPromptAsync(string name, int? version = null, string? label = null)
        {
            try
            {
                // Endpoint: GET /v1/prompts/{name}
                var url = $"v1/prompts/{name}";
                if (version.HasValue) url += $"?version={version}";
                else if (!string.IsNullOrEmpty(label)) url += $"?label={label}";

                var response = await _httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadFromJsonAsync<JsonElement>();
                
                // Langfuse API returns a prompt object with a 'prompt' field (which contains the template)
                if (json.TryGetProperty("prompt", out var promptField))
                {
                    return promptField.GetString();
                }

                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Error al obtener prompt de Langfuse: {ex.Message}");
                return null;
            }
        }
    }
}
