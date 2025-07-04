using Microsoft.SemanticKernel.Agents.AzureAI;
using Azure.AI.Agents.Persistent;
using Microsoft.Extensions.Options;

namespace ContractAnalysis.Agents;

public class ContractPolicyAgent : BaseAzureAIAgent
{
    public const string AgentName = "ContractPolicyAgent";

    public ContractPolicyAgent(IOptions<Settings> settings, PersistentAgentsClient client)
        : base(settings, client,
            settings.Value.ContractPolicyAgentId,
            settings.Value.ContractPolicyAgentInstructionsPath,
            settings.Value.AzureAiAgentModelDeploymentName,
            AgentName,
            "Agent to analyze contracts against company standards.")
    {
    }

    public async Task<AzureAIAgent> CreateAsync()
    {
        var definition = await GetOrCreateAgentAsync();
        return new(definition, _client);
    }
}
