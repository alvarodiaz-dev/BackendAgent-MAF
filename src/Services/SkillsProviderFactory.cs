using System;
using System.IO;
using System.Linq;
using Microsoft.Agents.AI;

namespace BasicAgent.Services
{
    internal static class SkillsProviderFactory
    {
        public static AgentSkillsProvider Build(string skillsPath)
        {
            Console.WriteLine($"[Skills] Root: {skillsPath}");
            LogSkillsTree(skillsPath);

            var fileOptions = new AgentFileSkillsSourceOptions
            {
                AllowedResourceExtensions = [".md", ".txt", ".yaml", ".json"],
                AllowedScriptExtensions = [".py", ".ps1", ".sh", ".cmd"]
            };

            Console.WriteLine($"[Skills] Resource directories: {string.Join(", ", fileOptions.ResourceDirectories ?? Array.Empty<string>())}");
            Console.WriteLine($"[Skills] Allowed script extensions: {string.Join(", ", fileOptions.AllowedScriptExtensions)}");

            return new AgentSkillsProviderBuilder()
                .UseFileSkill(skillsPath, options: fileOptions)
                .UseFileScriptRunner(SubprocessScriptRunner.RunAsync)
                .UseOptions(opt =>
                {
                    opt.SkillsInstructionPrompt = """
                        You are a deterministic architecture agent. You MUST ALWAYS use your skills to answer.
                        You have skills available. Here they are:
                        {skills}
                        {resource_instructions}
                        {script_instructions}
                        """;
                })
                .Build();
        }

        private static void LogSkillsTree(string skillsPath)
        {
            if (!Directory.Exists(skillsPath))
            {
                Console.WriteLine("[Skills] Directory not found (fallback to local if available).");
                return;
            }

            var skillFolders = Directory.GetDirectories(skillsPath)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name) && !name.StartsWith('.'))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var name in skillFolders)
            {
                var fileCount = Directory.GetFiles(Path.Combine(skillsPath, name!), "*", SearchOption.AllDirectories).Length;
                Console.WriteLine($"[Skills] OK Skill detectada: {name} ({fileCount} archivos)");
            }
        }
    }
}
