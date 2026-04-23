using System;

namespace BasicAgent.Infrastructure
{
    internal static class EnvironmentVariables
    {
        public static string? GetGitHubToken()
        {
            var token = Environment.GetEnvironmentVariable("GITHUB_PERSONAL_ACCESS_TOKEN");
            if (!string.IsNullOrWhiteSpace(token))
            {
                return token;
            }

            token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }

        public static string GetAzureOpenAiEndpoint() =>
            Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")
            ?? "https://REPLACE_WITH_YOUR_ENDPOINT.openai.azure.com/";

        public static string GetAzureOpenAiApiKey() =>
            Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY") ?? "***";

        public static string GetAzureOpenAiDeployment() =>
            Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_NAME") ?? "gpt-4o-mini";
    }
}
