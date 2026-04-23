using Microsoft.Agents.AI;

namespace BasicAgent.Services
{
    internal static class SkillsProviderFactory
    {
        public static AgentSkillsProvider Build(string skillsPath)
        {
            var fileOptions = new AgentFileSkillsSourceOptions
            {
                AllowedResourceExtensions = [".md", ".txt", ".yaml", ".json"],
                ResourceDirectories = ["references", "templates", "examples", "guides", "scripts", "resources"],
                AllowedScriptExtensions = [".py", ".ps1", ".sh", ".cmd"]
            };

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
    }
}
