using System.Diagnostics;
using Azure.AI.Agents.Persistent;
using Azure.AI.Projects;
using Azure.Identity;
using ContractAnalysis.Agents;
using ContractAnalysis.Tools;
using ContractAnalysis.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents.AzureAI;
using Microsoft.SemanticKernel.Agents.Orchestration;
using Microsoft.SemanticKernel.Agents.Orchestration.Sequential;
using Microsoft.SemanticKernel.Agents.Runtime;
using Microsoft.SemanticKernel.Agents.Runtime.InProcess;
using Polly;
using Polly.Fallback;
using Polly.Retry;

namespace ContractAnalysis;

public class Program
{
    public static async Task Main(string[] args)
    {
        var host = CreateHostBuilder(args).Build();

        try
        {
            await RunAgentAsync(host.Services);
        }
        catch (Exception ex)
        {
            var logger = host.Services.GetRequiredService<ILogger<Program>>();
            logger.LogError(ex, "An error occurred while running the application");
        }
    }

    private static async Task RunAgentAsync(IServiceProvider services)
    {
        var logger = services.GetRequiredService<ILogger<Program>>();

        logger.LogInformation("Starting Contract Analysis Agents");

        // Create the agent
        var analysisAgent = services.GetRequiredService<ContractAnalysisAgent>();
        ContractPolicyAgent policyAgent = services.GetRequiredService<ContractPolicyAgent>();

        // Invoke the Content Understanding tool to convert PDF contracts to markdown
        var tool = services.GetRequiredService<ContentUnderstandingTool>();

        await tool.CreateAnalyzerAsync();
        try
        {
            var analysisAgentAzureAIAgent = await analysisAgent.CreateAsync(tool);
            var policyAgentAzureAIAgent = await policyAgent.CreateAsync();

            OrchestrationMonitor monitor = new();
            SequentialOrchestration<List<string>, string> orchestration = new(analysisAgentAzureAIAgent, policyAgentAzureAIAgent)
            {
                ResponseCallback = monitor.ResponseCallback,
            };

            // Start the runtime
            InProcessRuntime runtime = new();
            await runtime.StartAsync();

            // Run the orchestration
            List<string> input = new() {
                 "'../assets/input/123LogisticsContract.md'",
                "'../assets/input/ABCContract.pdf'" };
            // Polly retry and fallback policy for orchestration throttling and transient errors
            var orchestrationRetryPolicy = Polly.Policy
                .Handle<Exception>(ex =>
                    IsThrottlingException(ex)
                )
                .WaitAndRetryAsync(5, attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)),
                    (exception, timeSpan, retryCount, context) =>
                    {
                        if (exception is Azure.RequestFailedException rfe && rfe.Status == 429 && rfe.Message.Contains("Received too many requests in a short amount of time"))
                        {
                            logger.LogDebug($"Suppressed 429 log: {rfe.Message}");
                        }
                        else
                        {
                            logger.LogWarning(exception, $"Orchestration retry {retryCount} after {timeSpan.TotalSeconds}s due to throttling or transient error.");
                        }
                    });

            OrchestrationResult<string>? result = null;
            string? text = null;
            await orchestrationRetryPolicy.ExecuteAsync(async () =>
            {
                result = await orchestration.InvokeAsync(input, runtime).AsTask();
                text = await result.GetValueAsync();
                await runtime.RunUntilIdleAsync();
            });

            if (result != null)
            {
                await StartEvaluationSamplingAsync(analysisAgentAzureAIAgent, result.Topic, runtime, services);
            }

            foreach (ChatMessageContent message in monitor.History)
            {
                if (message.AuthorName == ContractAnalysisAgent.AgentName)
                {
                    File.WriteAllText($"../assets/output/run-comparison-outcome.md", message.Content);
                }
                if (message.AuthorName == ContractPolicyAgent.AgentName)
                {
                    File.WriteAllText($"../assets/output/run-policycheck-outcome.md", message.Content);
                }
            }
            OpenBothOutputFiles();

            logger.LogInformation("Analysis completed successfully");
        }
        finally
        {
            await tool.DeleteAnalyzerAsync();
        }
    }

    private static async Task StartEvaluationSamplingAsync(AzureAIAgent agent, TopicId orchestrationResultTopic, InProcessRuntime runtime, IServiceProvider services)
    {
        var logger = services.GetRequiredService<ILogger<Program>>();
        var projectClient = services.GetRequiredService<AIProjectClient>();
        var agentsClient = services.GetRequiredService<PersistentAgentsClient>();

        var evaluators = new Dictionary<string, EvaluatorConfiguration>{
            { "Relevance", new EvaluatorConfiguration(EvaluatorIDs.Relevance) },
            { "Fluency", new EvaluatorConfiguration(EvaluatorIDs.Fluency) },
            { "Coherence", new EvaluatorConfiguration(EvaluatorIDs.Coherence) }
        };

        var evaluationsClient = projectClient.GetEvaluationsClient();
        var appInsightsConnectionString = projectClient.Telemetry.GetConnectionString();

        var samplingConfig = new AgentEvaluationSamplingConfiguration(
            name: $"{agent.Name} Sampling",
            samplingPercent: 15,
            maxRequestRate: 250
        );

        var thread = runtime.GetThreadsForAgent(orchestrationResultTopic, agent.Name!).First();
        var run = (await agentsClient.Runs.GetRunsAsync(thread.Id).ToListAsync()).First();

        logger.LogInformation("Try creating Agent Evaluation for thread {ThreadId} with run {RunId}", thread.Id, run.Id);

        // Polly retry and fallback policy for throttling and transient errors
        var retryPolicy = Polly.Policy
            .Handle<Exception>(ex =>
                IsThrottlingException(ex)
            )
            .WaitAndRetry(5, attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)),
                (exception, timeSpan, retryCount, context) =>
                {
                    if (exception is Azure.RequestFailedException rfe && rfe.Status == 429 && rfe.Message.Contains("Received too many requests in a short amount of time"))
                    {
                        // Suppress or log at Debug level
                        logger.LogDebug($"Suppressed 429 log: {rfe.Message}");
                    }
                    else
                    {
                        logger.LogWarning(exception, $"Retry {retryCount} after {timeSpan.TotalSeconds}s due to throttling or transient error.");
                    }
                });

        var fallbackPolicy = Polly.Policy
            .Handle<Exception>()
            .Fallback(() =>
            {
                logger.LogError("Agent evaluation request failed after retries due to persistent throttling or error.");
            });

        fallbackPolicy.Wrap(retryPolicy).Execute(() =>
        {
            var response = evaluationsClient.CreateAgentEvaluation(
                new AgentEvaluationRequest(run.Id, evaluators, appInsightsConnectionString)
                {
                    ThreadId = thread.Id,
                    SamplingConfiguration = samplingConfig,
                }
            );
            logger.LogInformation("Agent Evaluation created successfully");
        });
    }

    private static void OpenBothOutputFiles()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c code \"../assets/output/run-comparison-outcome.md\" \"../assets/output/run-policycheck-outcome.md\"",
            CreateNoWindow = true,
        });
    }

    private static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((context, config) =>
            {
                config.AddJsonFile("appsettings.json", optional: false)
                      .AddJsonFile($"appsettings.{context.HostingEnvironment.EnvironmentName}.json", optional: true)
                      .AddEnvironmentVariables();
            }).ConfigureServices((context, services) =>
            {
                services.AddOptions<Settings>()
                    .Bind(context.Configuration.GetSection("Settings"))
                    .ValidateDataAnnotations();

                services.AddLogging(builder => builder.AddConsole());
                services.AddSingleton<ContentUnderstandingTool>();
                services.AddSingleton<ContractAnalysisAgent>();
                services.AddSingleton<ContractPolicyAgent>();

                services.AddTransient(provider =>
                {
                    var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
                    {
                        ExcludeSharedTokenCacheCredential = true,
                        ExcludeVisualStudioCredential = true
                    });

                    var settings = provider.GetRequiredService<IOptions<Settings>>()!.Value;
                    return AzureAIAgent.CreateAgentsClient(settings.AzureAiAgentEndpoint, credential);
                });

                services.AddTransient(provider =>
                {
                    var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
                    {
                        ExcludeSharedTokenCacheCredential = true,
                        ExcludeVisualStudioCredential = true
                    });

                    var settings = provider.GetRequiredService<IOptions<Settings>>()!.Value;
                    return new AIProjectClient(new Uri(settings.AzureAiAgentEndpoint), credential);
                });
            });

    // Helper method for throttling detection
    static bool IsThrottlingException(Exception ex)
    {
        if (ex is AggregateException aggEx)
        {
            return aggEx.InnerExceptions.Any(IsThrottlingException);
        }
        if (ex is Azure.RequestFailedException rfe && (rfe.Status == 429 || rfe.Status == 503 || rfe.Status == 408))
            return true;
        if (ex is Microsoft.SemanticKernel.KernelException kernelEx &&
            (kernelEx.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || kernelEx.Message.Contains("Rate limit is exceeded", StringComparison.OrdinalIgnoreCase)
            || kernelEx.Message.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase)
            || kernelEx.Message.Contains("throttle", StringComparison.OrdinalIgnoreCase)))
            return true;
        var msg = ex.Message;
        return msg.Contains("429")
            || msg.Contains("Too Many Requests")
            || msg.Contains("throttle", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("Rate limit is exceeded", StringComparison.OrdinalIgnoreCase);
    }
}
