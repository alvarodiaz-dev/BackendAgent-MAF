using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using ModelContextProtocol;

namespace BasicAgent
{
    internal static class McpGitHubSmokeTests
    {
        public static async Task RunAsync(string[] args)
        {
            Console.WriteLine("[MCP Test] Starting GitHub MCP smoke test...");

            var token = Environment.GetEnvironmentVariable("GITHUB_PERSONAL_ACCESS_TOKEN");
            if (string.IsNullOrWhiteSpace(token))
            {
                token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            }
            if (string.IsNullOrWhiteSpace(token))
            {
                Console.WriteLine("[MCP Test] Missing env var: GITHUB_PERSONAL_ACCESS_TOKEN (or GITHUB_TOKEN)");
                return;
            }

            bool shouldCreateRepo = args.Any(a => string.Equals(a, "--create-repo", StringComparison.OrdinalIgnoreCase));
            var githubMcpServerPath = ResolveGitHubMcpServerPath();
            if (githubMcpServerPath == null)
            {
                Console.WriteLine("[MCP Test] GitHub MCP server script not found. Run 'npm install' in project root.");
                return;
            }

            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = "node",
                Arguments = [githubMcpServerPath],
                EnvironmentVariables = new Dictionary<string, string?>
                {
                    { "GITHUB_PERSONAL_ACCESS_TOKEN", token }
                }
            });

            var client = await McpClient.CreateAsync(transport);
            var tools = (await client.ListToolsAsync()).ToList();

            Console.WriteLine($"[MCP Test] Connected. Tools exposed: {tools.Count}");
            foreach (var tool in tools.OrderBy(t => t.Name))
            {
                Console.WriteLine($" - {tool.Name}");
            }

            var createRepoTool = tools.FirstOrDefault(t =>
                t.Name.Contains("create_repository", StringComparison.OrdinalIgnoreCase));

            if (createRepoTool == null)
            {
                Console.WriteLine("[MCP Test] FAIL: No create_repository tool found.");
                return;
            }

            Console.WriteLine($"[MCP Test] OK: create repo tool found: {createRepoTool.Name}");

            if (!shouldCreateRepo)
            {
                Console.WriteLine("[MCP Test] Dry run only. To actually create a repo, rerun with --create-repo");
                return;
            }

            var defaultRepoName = $"mcp-smoke-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
            var repoNameArg = args.FirstOrDefault(a => a.StartsWith("--repo-name=", StringComparison.OrdinalIgnoreCase));
            var repoName = repoNameArg?.Split('=', 2).LastOrDefault();
            repoName = string.IsNullOrWhiteSpace(repoName) ? defaultRepoName : repoName.Trim();

            var ownerArg = args.FirstOrDefault(a => a.StartsWith("--owner=", StringComparison.OrdinalIgnoreCase));
            var owner = ownerArg?.Split('=', 2).LastOrDefault();

            var payload = new Dictionary<string, object?>
            {
                ["name"] = repoName,
                ["private"] = true,
                ["description"] = "Repository created by MCP smoke test",
                ["autoInit"] = true
            };

            if (!string.IsNullOrWhiteSpace(owner))
            {
                payload["organization"] = owner;
            }

            Console.WriteLine($"[MCP Test] Creating repo '{repoName}' via tool '{createRepoTool.Name}'...");
            var response = await InvokeToolByReflectionAsync(client, createRepoTool.Name, payload);

            Console.WriteLine("[MCP Test] Create repository response:");
            Console.WriteLine(response);
            Console.WriteLine("[MCP Test] Done.");
        }

        private static async Task<string> InvokeToolByReflectionAsync(object client, string toolName, Dictionary<string, object?> payload)
        {
            var methods = client.GetType().GetMethods()
                .Where(m => string.Equals(m.Name, "CallToolAsync", StringComparison.Ordinal))
                .ToList();

            if (methods.Count == 0)
            {
                return "Could not find CallToolAsync on MCP client. Update this helper to match current SDK.";
            }

            foreach (var method in methods)
            {
                var parameters = method.GetParameters();
                if (parameters.Length < 2)
                {
                    continue;
                }

                var callArgs = new object?[parameters.Length];
                bool valid = true;

                callArgs[0] = toolName;
                callArgs[1] = BuildArgsForType(parameters[1].ParameterType, payload);

                if (callArgs[1] == null && parameters[1].ParameterType.IsValueType)
                {
                    valid = false;
                }

                for (int i = 2; i < parameters.Length; i++)
                {
                    if (parameters[i].HasDefaultValue)
                    {
                        callArgs[i] = parameters[i].DefaultValue;
                    }
                    else
                    {
                        valid = false;
                        break;
                    }
                }

                if (!valid)
                {
                    continue;
                }

                try
                {
                    var resultTaskObj = method.Invoke(client, callArgs);
                    if (resultTaskObj is not Task resultTask)
                    {
                        continue;
                    }

                    await resultTask.ConfigureAwait(false);
                    var resultProperty = resultTaskObj.GetType().GetProperty("Result");
                    var result = resultProperty?.GetValue(resultTaskObj);

                    return SerializeObject(result);
                }
                catch (Exception ex)
                {
                    return $"CallToolAsync invocation failed: {ex.Message}";
                }
            }

            return "No compatible CallToolAsync overload found for provided payload type.";
        }

        private static object? BuildArgsForType(Type targetType, Dictionary<string, object?> payload)
        {
            if (targetType.IsAssignableFrom(typeof(Dictionary<string, object?>)))
            {
                return payload;
            }

            if (targetType.IsAssignableFrom(typeof(IReadOnlyDictionary<string, object?>)))
            {
                return payload;
            }

            if (targetType == typeof(JsonObject))
            {
                return JsonSerializer.SerializeToNode(payload)?.AsObject();
            }

            if (targetType == typeof(JsonNode))
            {
                return JsonSerializer.SerializeToNode(payload);
            }

            if (targetType == typeof(JsonDocument))
            {
                return JsonDocument.Parse(JsonSerializer.Serialize(payload));
            }

            if (targetType == typeof(string))
            {
                return JsonSerializer.Serialize(payload);
            }

            if (typeof(IEnumerable).IsAssignableFrom(targetType) && targetType != typeof(string))
            {
                return payload;
            }

            return null;
        }

        private static string SerializeObject(object? value)
        {
            try
            {
                return JsonSerializer.Serialize(value, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
            }
            catch
            {
                return value?.ToString() ?? "<null>";
            }
        }

        private static string? ResolveGitHubMcpServerPath()
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
