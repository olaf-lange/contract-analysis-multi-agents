using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ContractAnalysis.Utils;

public sealed class OrchestrationMonitor
{
    public ChatHistory History { get; } = [];

    public ValueTask ResponseCallback(ChatMessageContent response)
    {
        this.History.Add(response);
        return ValueTask.CompletedTask;
    }
}