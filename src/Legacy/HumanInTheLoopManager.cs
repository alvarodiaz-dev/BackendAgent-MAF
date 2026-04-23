using System;
using System.Threading.Tasks;

namespace BasicAgent
{
    /// <summary>
    /// Manages human-in-the-loop confirmations in the agent pipeline.
    /// Allows pausing execution to validate artifacts and get approval before proceeding.
    /// </summary>
    internal static class HumanInTheLoopManager
    {
        private static bool _enableHumanInTheLoop = false;

        /// <summary>
        /// Enables or disables human-in-the-loop mode.
        /// </summary>
        public static void SetHumanInTheLoopEnabled(bool enabled)
        {
            _enableHumanInTheLoop = enabled;
            if (enabled)
            {
                Console.WriteLine("\n[Config] ✓ Human-in-the-loop mode ENABLED. You will be asked for confirmation at checkpoints.");
            }
            else
            {
                Console.WriteLine("\n[Config] Human-in-the-loop mode disabled. Pipeline will run fully automated.");
            }
        }

        /// <summary>
        /// Pauses execution and asks for human confirmation at a checkpoint.
        /// </summary>
        public static async Task<bool> RequestApprovalAsync(string phaseName, string artifact, string description = "")
        {
            if (!_enableHumanInTheLoop)
            {
                return true; // Auto-approve if not in human-in-the-loop mode
            }

            Console.WriteLine("\n" + new string('=', 70));
            Console.WriteLine($"[Human Approval] ▶ CHECKPOINT: {phaseName}");
            Console.WriteLine(new string('=', 70));

            if (!string.IsNullOrWhiteSpace(description))
            {
                Console.WriteLine($"Description: {description}");
            }

            Console.WriteLine($"\nArtifact: {artifact}");
            Console.WriteLine("\nOptions:");
            Console.WriteLine("  [Y/y] - Continue to next phase");
            Console.WriteLine("  [N/n] - Abort pipeline");
            Console.WriteLine("  [S/s] - Skip this checkpoint (disable human approval for this session)");
            Console.WriteLine("  [D/d] - Debug: Show additional info");

            while (true)
            {
                Console.Write("\nYour choice: ");
                var input = Console.ReadLine()?.ToLower().Trim();

                switch (input)
                {
                    case "y":
                        Console.WriteLine("✓ Proceeding...\n");
                        return true;

                    case "n":
                        Console.WriteLine("✗ Pipeline aborted by user.\n");
                        return false;

                    case "s":
                        Console.WriteLine("⊘ Disabling human-in-the-loop for remaining phases.\n");
                        SetHumanInTheLoopEnabled(false);
                        return true;

                    case "d":
                        await ShowDebugInfoAsync(artifact);
                        Console.Write("\nYour choice: ");
                        continue;

                    default:
                        Console.WriteLine("Invalid option. Please enter Y, N, S, or D.");
                        continue;
                }
            }
        }

        /// <summary>
        /// Shows debug information about an artifact (file count, size, path).
        /// </summary>
        private static async Task ShowDebugInfoAsync(string artifactPath)
        {
            Console.WriteLine("\n[Debug Info]:");

            if (System.IO.File.Exists(artifactPath))
            {
                var fileInfo = new System.IO.FileInfo(artifactPath);
                Console.WriteLine($"  Type: File");
                Console.WriteLine($"  Size: {fileInfo.Length} bytes");
                Console.WriteLine($"  Last Modified: {fileInfo.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
                Console.WriteLine($"  Path: {fileInfo.FullName}");
            }
            else if (System.IO.Directory.Exists(artifactPath))
            {
                var dirInfo = new System.IO.DirectoryInfo(artifactPath);
                var files = dirInfo.GetFiles("*", System.IO.SearchOption.AllDirectories);
                var totalSize = files.Sum(f => f.Length);

                Console.WriteLine($"  Type: Directory");
                Console.WriteLine($"  Files: {files.Length}");
                Console.WriteLine($"  Total Size: {totalSize} bytes");
                Console.WriteLine($"  Path: {dirInfo.FullName}");

                if (files.Length <= 10)
                {
                    Console.WriteLine("\n  Contents:");
                    foreach (var file in files)
                    {
                        Console.WriteLine($"    - {file.Name} ({file.Length} bytes)");
                    }
                }
                else
                {
                    Console.WriteLine("\n  Sample files (first 10):");
                    foreach (var file in files.Take(10))
                    {
                        Console.WriteLine($"    - {file.Name} ({file.Length} bytes)");
                    }
                    Console.WriteLine($"    ... and {files.Length - 10} more");
                }
            }
            else
            {
                Console.WriteLine($"  ⚠ Artifact not found: {artifactPath}");
            }
        }

        /// <summary>
        /// Displays a summary of completed phase with results.
        /// </summary>
        public static void DisplayPhaseSummary(string phaseName, bool success, string? resultPath = null, string? details = "")
        {
            Console.WriteLine("\n" + new string('─', 70));
            Console.WriteLine($"[Phase Summary] {phaseName}");
            Console.WriteLine(new string('─', 70));
            Console.WriteLine($"Status: {(success ? "✓ SUCCESS" : "✗ FAILED")}");

            if (!string.IsNullOrWhiteSpace(resultPath))
            {
                Console.WriteLine($"Output: {resultPath}");
            }

            if (!string.IsNullOrWhiteSpace(details))
            {
                Console.WriteLine($"Details: {details}");
            }

            Console.WriteLine();
        }
    }
}
