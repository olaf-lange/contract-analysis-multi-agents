using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.Orchestration;
using Microsoft.SemanticKernel.Agents.Runtime;
using Microsoft.SemanticKernel.Agents.Runtime.InProcess;
using ReflectionMagic;

public static class InProcessRuntimeExtensions
{
    public static IEnumerable<AgentThread> GetThreadsForAgent(this InProcessRuntime runtime, TopicId orchestrationResultTopic, string agentName)
    {
        var agentInstances = (Dictionary<AgentId, IHostableAgent>)runtime.AsDynamic().agentInstances;
        foreach (var agentInstance in agentInstances.Values)
        {
            if (agentInstance is AgentActor agentActor)
            {
                var agentActorDynamic = agentActor.AsDynamic();
                if (agentActorDynamic.Agent?.Name == agentName && agentActorDynamic.Context.Topic.Type == orchestrationResultTopic.Type)
                {
                    yield return (AgentThread)agentActorDynamic.Thread;
                }
            }
        }
    }
}