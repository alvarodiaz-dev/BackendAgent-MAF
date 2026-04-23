using System;
using System.IO;
using System.Linq;

namespace BasicAgent.Infrastructure
{
    internal static class McpServerPathResolver
    {
        public static string? ResolveGitHubServerPath()
        {
            const string relativeMcpPath = "node_modules\\@modelcontextprotocol\\server-github\\dist\\index.js";

            var startDirs = new[]
            {
                ProjectPaths.GetProjectRootDirectory(),
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
