using Azure.AI.Agents.Persistent;
using Microsoft.Extensions.Options;

namespace ContractAnalysis.Agents;

public abstract class BaseAzureAIAgent
{
    protected readonly Settings _settings;
    protected readonly PersistentAgentsClient _client;
    protected readonly string? _agentId;
    protected readonly string? _instructionsPath;
    protected readonly string _modelDeploymentName;
    protected readonly string _agentName;
    protected readonly string _description;

    protected BaseAzureAIAgent(IOptions<Settings> settings, PersistentAgentsClient client, string? agentId, string? instructionsPath, string modelDeploymentName, string agentName, string description)
    {
        _settings = settings.Value;
        _client = client;
        _agentId = agentId;
        _instructionsPath = instructionsPath;
        _modelDeploymentName = modelDeploymentName;
        _agentName = agentName;
        _description = description;
    }

    protected async Task<PersistentAgent> GetOrCreateAgentAsync()
    {
        if (!string.IsNullOrWhiteSpace(_agentId))
        {
            // Use existing agent by ID
            return await _client.Administration.GetAgentAsync(_agentId);
        }
        else
        {
            // Create new agent using instructions
            if (string.IsNullOrWhiteSpace(_instructionsPath))
            {
                throw new ArgumentException("Instructions path cannot be null or empty.", nameof(_instructionsPath));
            }

            var instructions = await File.ReadAllTextAsync(_instructionsPath);

            if (string.IsNullOrWhiteSpace(instructions))
            {
                throw new InvalidOperationException("Instructions file is empty.");
            }

            return await _client.Administration.CreateAgentAsync(
                _modelDeploymentName,
                name: _agentName,
                description: _description,
                instructions: instructions);
        }
    }
}
