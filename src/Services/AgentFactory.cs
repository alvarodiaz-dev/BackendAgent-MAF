using System.Collections.Generic;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace BasicAgent.Services
{
    internal static class AgentFactory
    {
        public static AIAgent Build(
            IChatClient chatClient,
            string name,
            string instructions,
            List<AITool> tools,
            AgentSkillsProvider? skillsProvider)
        {
            var options = new ChatClientAgentOptions
            {
                Name = name,
                ChatOptions = new ChatOptions
                {
                    Instructions = instructions,
                    Tools = tools
                }
            };

            if (skillsProvider != null)
            {
                options.AIContextProviders = [skillsProvider];
            }

            return new ChatClientAgent(chatClient, options);
        }
    }
}
