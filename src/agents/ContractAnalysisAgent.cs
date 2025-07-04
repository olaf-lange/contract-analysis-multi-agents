using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents.AzureAI;
using Azure.AI.Agents.Persistent;
using Microsoft.Extensions.Options;
using ContractAnalysis.Tools;

namespace ContractAnalysis.Agents;

public class ContractAnalysisAgent : BaseAzureAIAgent
{
    public const string AgentName = "ContractAnalysisAgent";

    public ContractAnalysisAgent(IOptions<Settings> settings, PersistentAgentsClient client)
        : base(settings, client,
            settings.Value.ContractAnalysisAgentId,
            settings.Value.ContractAnalysisAgentInstructionsPath,
            settings.Value.AzureAiAgentModelDeploymentName,
            AgentName,
            "Agent to analyze contracts.")
    {
    }

    public async Task<AzureAIAgent> CreateAsync(ContentUnderstandingTool tool)
    {
        var definition = await GetOrCreateAgentAsync();
        KernelPlugin plugin = KernelPluginFactory.CreateFromObject(tool);
        return new(definition, _client, plugins: [plugin]);
    }
}
