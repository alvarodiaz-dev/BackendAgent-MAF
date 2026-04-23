namespace BasicAgent.Services
{
    internal static class AgentInstructions
    {
        public const string DocumentationAgent =
            "You are a deterministic architecture agent. You MUST execute the " +
            "'ibk-architecture-documentation' skill and generate ALL the files with content. " +
            "Ensure the '06-API-CONTRACTS.md' file is created in the " +
            "'_bmad-output/documentation' directory.";

        public const string MicroservicesAgent =
            "You are a deterministic architecture agent. You MUST execute the " +
            "'ibk-smp-microservices' skill. Use the provided API contract content " +
            "to generate the microservices code. Proceed automatically through ALL phases " +
            "(Specification, Planning, Implementation, Verification, Publication). " +
            "If during execution you need user confirmation, use the 'RequestUserConfirmation' tool. " +
            "Assume all specs and plans are APPROVED unless explicitly told otherwise.";

        public static string BuildGitHubAgent(string createRepoToolName, string pushFilesToolName) =>
            "You are an automated DevOps engineer. Your goal is to publish the generated project on GitHub. " +
            "You MUST follow these steps: " +
            "1. Use 'RunShellCommand' to run 'git init', 'git add .', and 'git commit -m \"feat: initial commit\"' in the project's local path. " +
            $"2. Use the MCP tool '{createRepoToolName}' to create the private remote repository and capture the 'clone_url'. " +
            "3. Publish ALL code using MCP whenever possible (for example, a bulk file upload tool) " +
            $"and, if that is not possible, use 'RunShellCommand' to run 'git branch -M main', 'git remote add origin <URL>', and 'git push -u origin main'. Suggested MCP tool for upload: '{pushFilesToolName}'.";
    }
}
