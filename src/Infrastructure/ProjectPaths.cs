using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace BasicAgent.Infrastructure
{
    internal static class ProjectPaths
    {
        private static readonly AsyncLocal<string?> ActiveRunDirectory = new();

        public static string ResolvePath(string path) =>
            Path.IsPathRooted(path) ? path : Path.Combine(GetBaseWorkingDirectory(), path);

        public static string GetBaseWorkingDirectory() =>
            string.IsNullOrWhiteSpace(ActiveRunDirectory.Value)
                ? GetProjectRootDirectory()
                : ActiveRunDirectory.Value!;

        public static IDisposable UseRunDirectory(string runDirectory)
        {
            if (string.IsNullOrWhiteSpace(runDirectory))
            {
                throw new ArgumentException("Run directory cannot be null or whitespace.", nameof(runDirectory));
            }

            string? previous = ActiveRunDirectory.Value;
            ActiveRunDirectory.Value = runDirectory;
            return new RunDirectoryScope(() => ActiveRunDirectory.Value = previous);
        }

        public static string GetProjectRootDirectory()
        {
            var searchRoots = new[]
            {
                AppContext.BaseDirectory,
                Environment.CurrentDirectory,
                Directory.GetCurrentDirectory()
            }
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

            foreach (var startDir in searchRoots)
            {
                var dir = new DirectoryInfo(startDir);
                while (dir != null)
                {
                    var hasProjectFile = File.Exists(Path.Combine(dir.FullName, "BasicAgent.csproj"));
                    var hasSolutionFile = File.Exists(Path.Combine(dir.FullName, "BasicAgent.sln"));

                    if (hasProjectFile || hasSolutionFile)
                    {
                        return dir.FullName;
                    }

                    dir = dir.Parent;
                }
            }

            return AppContext.BaseDirectory;
        }

        private sealed class RunDirectoryScope : IDisposable
        {
            private readonly Action _restore;
            private bool _disposed;

            public RunDirectoryScope(Action restore)
            {
                _restore = restore;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _restore();
                _disposed = true;
            }
        }
    }
}
