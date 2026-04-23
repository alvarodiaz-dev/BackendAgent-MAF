using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using BasicAgent.Infrastructure;

namespace BasicAgent.Tools
{
    internal static class ShellCommandTool
    {
        [Description("Execute a shell command locally.")]
        public static async Task<string> RunShellCommand(
            [Description("The command to execute.")] string command,
            [Description("The working directory for the command.")] string workingDirectory)
        {
            try
            {
                Console.WriteLine($"[Tool Executed] Running command: {command} in {workingDirectory}");

                bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
                string fileName = isWindows ? "cmd.exe" : "/bin/bash";
                string arguments = isWindows ? $"/c {command}" : $"-c \"{command}\"";
                string resolvedWorkingDirectory = ProjectPaths.ResolvePath(workingDirectory);

                var processInfo = new ProcessStartInfo(fileName, arguments)
                {
                    WorkingDirectory = resolvedWorkingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(processInfo);
                if (process == null)
                {
                    return "Error: Could not start process.";
                }

                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try
                {
                    await process.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    process.Kill(entireProcessTree: true);
                    return $"Error: Command timed out after 30 seconds. Command was probably waiting for user input: {command}";
                }

                string output = await outputTask;
                string error = await errorTask;
                var result = string.IsNullOrEmpty(error) ? output : $"Output: {output}\nError: {error}";

                return string.IsNullOrWhiteSpace(result)
                    ? "Command executed successfully (no output)."
                    : result;
            }
            catch (Exception ex)
            {
                return $"Error executing command: {ex.Message}";
            }
        }
    }
}
