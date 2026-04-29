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
                ResourceDirectories = ["references", "templates", "examples", "guides", "scripts", "resources"],
                AllowedScriptExtensions = [".py", ".ps1", ".sh", ".cmd"]
            };

            Console.WriteLine($"[Skills] Resource directories: {string.Join(", ", fileOptions.ResourceDirectories)}");
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
                Console.WriteLine("[Skills] Directory not found.");
                return;
            }

            foreach (string dir in Directory.GetDirectories(skillsPath).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine($"[Skills] Skill folder: {Path.GetFileName(dir)}");

                foreach (string file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                             .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    string relative = Path.GetRelativePath(skillsPath, file);
                    Console.WriteLine($"[Skills]   - {relative}");
                }
            }
        }
    }
}
