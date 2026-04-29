using System;
using System.ComponentModel;
using System.IO;
using BasicAgent.Infrastructure;

namespace BasicAgent.Tools
{
    internal static class FileSystemTools
    {
        [Description("Write content to a file.")]
        public static void WriteFile(
            [Description("The path to the file to write (relative or absolute).")]
            string path,
            [Description("The content to write into the file.")]
            string content)
        {
            string fullPath = ProjectPaths.ResolvePath(path);
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(fullPath, content);
            Console.WriteLine($"[Tool Executed] WriteFile -> {fullPath}");
        }

        [Description("Create a directory.")]
        public static void CreateDirectory(
            [Description("The path of the directory to create.")]
            string path)
        {
            string fullPath = ProjectPaths.ResolvePath(path);
            Directory.CreateDirectory(fullPath);
            Console.WriteLine($"[Tool Executed] CreateDirectory -> {fullPath}");
        }

        [Description("Read content from a file.")]
        public static string ReadFile(
            [Description("The path to the file to read.")]
            string path)
        {
            string fullPath = ProjectPaths.ResolvePath(path);
            if (!File.Exists(fullPath))
            {
                Console.WriteLine($"[Tool Warning] ReadFile - not found: {fullPath}");
                return "Error: File not found.";
            }

            Console.WriteLine($"[Tool Executed] ReadFile -> {fullPath}");
            return File.ReadAllText(fullPath);
        }

        [Description("List all files in a directory recursively.")]
        public static string GetDirectoryStructure(
            [Description("The path to the directory to explore.")]
            string path)
        {
            string fullPath = ProjectPaths.ResolvePath(path);
            if (!Directory.Exists(fullPath))
            {
                return "Error: Directory not found.";
            }

            var files = Directory.GetFiles(fullPath, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(fullPath, f))
                .ToList();

            Console.WriteLine($"[Tool Executed] GetDirectoryStructure -> {fullPath} ({files.Count} files)");
            return string.Join("\n", files);
        }
    }
}
