using System;
using Npgsql;

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

        public static string? GetSupabasePostgresConnectionString()
        {
            var value = Environment.GetEnvironmentVariable("SUPABASE_DB_CONNECTION_STRING");
            if (!string.IsNullOrWhiteSpace(value))
            {
                return NormalizePostgresConnectionString(value);
            }

            value = Environment.GetEnvironmentVariable("SUPABASE_POSTGRES_CONNECTION_STRING");
            return string.IsNullOrWhiteSpace(value) ? null : NormalizePostgresConnectionString(value);
        }

        private static string NormalizePostgresConnectionString(string raw)
        {
            if (raw.Contains('=') && raw.Contains(';'))
            {
                return raw;
            }

            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            {
                return raw;
            }

            if (!uri.Scheme.StartsWith("postgres", StringComparison.OrdinalIgnoreCase))
            {
                return raw;
            }

            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = uri.Host,
                Port = uri.Port > 0 ? uri.Port : 5432,
                Username = Uri.UnescapeDataString(uri.UserInfo.Split(':', 2)[0]),
                Password = uri.UserInfo.Contains(':') ? Uri.UnescapeDataString(uri.UserInfo.Split(':', 2)[1]) : string.Empty,
                Database = uri.AbsolutePath.TrimStart('/'),
                SslMode = SslMode.Require,
                Pooling = false
            };

            return builder.ConnectionString;
        }

        public static string? GetLiteLlmEndpoint() =>
            Environment.GetEnvironmentVariable("LITELLM_ENDPOINT");

        public static string? GetLiteLlmApiKey() =>
            Environment.GetEnvironmentVariable("LITELLM_KEY");

        public static string? GetLiteLlmModel() =>
            Environment.GetEnvironmentVariable("LITELLM_MODEL");

        public static bool IsLiteLlmConfigured()
        {
            var endpoint = GetLiteLlmEndpoint();
            var apiKey = GetLiteLlmApiKey();
            var model = GetLiteLlmModel();
            return !string.IsNullOrWhiteSpace(endpoint) && !string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(model);
        }
    }
}
