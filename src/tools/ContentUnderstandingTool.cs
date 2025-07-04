using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

namespace ContractAnalysis.Tools;

public class ContentUnderstandingTool
{
    private readonly AzureContentUnderstandingClient _client;
    private readonly string _schemaFilePath;
    private readonly ILogger<ContentUnderstandingTool> _logger;
    private string _currentAnalyzerId = "";

    public ContentUnderstandingTool(IOptions<Settings> settings, ILogger<ContentUnderstandingTool> logger)
    {
        _schemaFilePath = settings.Value.AzureAiCuSchemaFilePath;

        _client = new AzureContentUnderstandingClient(settings.Value.AzureAiCuEndpoint, settings.Value.AzureAiCuApiVersion, settings.Value.AzureAiCuSubscription);
        _logger = logger;
    }

    public async Task CreateAnalyzerAsync()
    {
        var analyzerId = $"contract-analysis-{Guid.NewGuid()}";

        // Create analyzer
        var response = await _client.BeginCreateAnalyzerAsync(analyzerId, analyzerTemplatePath: _schemaFilePath);
        var result = await _client.PollResultAsync(response);

        _currentAnalyzerId = analyzerId;
    }

    public async Task DeleteAnalyzerAsync()
    {
        await _client.DeleteAnalyzerAsync(_currentAnalyzerId);
    }

    [KernelFunction("analyze_document")]
    [Description("Analyzes a document and transforms it into markdown format.")]
    public async Task<string> AnalyzeAsync(string filepath)
    {
        _logger.LogInformation($"Starting for: {filepath}");

        // Analyze file
        var response = await _client.BeginAnalyzeAsync(_currentAnalyzerId, filepath);
        var result = await _client.PollResultAsync(response);

        var resultJson = JsonSerializer.Deserialize<JsonElement>(result);
        var contractInMarkdown = resultJson
            .GetProperty("result")
            .GetProperty("contents")[0]
            .GetProperty("markdown")
            .GetString() ?? string.Empty;

        _logger.LogInformation($"Completed for: {filepath}");
        return contractInMarkdown;
    }

    [KernelFunction("read_document")]
    [Description("Opens the file and returns the file content.")]
    public async Task<string> ReadAsync(string filepath)
    {
        var currentDir = Directory.GetCurrentDirectory();
        Console.WriteLine($"Current directory: {currentDir}");
        Console.WriteLine($"Reading file: {filepath}");
        return await File.ReadAllTextAsync(filepath);
    }
}

public class AzureContentUnderstandingClient
{
    private readonly HttpClient _httpClient;
    private readonly string _endpoint;
    private readonly string _apiVersion;
    private readonly ILogger<AzureContentUnderstandingClient> _logger;

    public AzureContentUnderstandingClient(string endpoint, string apiVersion, string subscriptionKey)
    {
        _endpoint = endpoint.TrimEnd('/');
        _apiVersion = apiVersion;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", subscriptionKey);
        _httpClient.DefaultRequestHeaders.Add("x-ms-useragent", "cu-sample-code");

        using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        _logger = loggerFactory.CreateLogger<AzureContentUnderstandingClient>();
    }

    private string GetAnalyzerUrl(string analyzerId)
    {
        return $"{_endpoint}/contentunderstanding/analyzers/{analyzerId}?api-version={_apiVersion}";
    }

    private string GetAnalyzeUrl(string analyzerId)
    {
        return $"{_endpoint}/contentunderstanding/analyzers/{analyzerId}:analyze?api-version={_apiVersion}";
    }
    public async Task<HttpResponseMessage> BeginCreateAnalyzerAsync(
        string analyzerId,
        JsonElement? analyzerTemplate = null,
        string? analyzerTemplatePath = null)
    {
        string templateJson;

        if (!string.IsNullOrEmpty(analyzerTemplatePath) && File.Exists(analyzerTemplatePath))
        {
            templateJson = await File.ReadAllTextAsync(analyzerTemplatePath);
        }
        else if (analyzerTemplate.HasValue)
        {
            templateJson = analyzerTemplate.Value.ToString();
        }
        else
        {
            throw new ArgumentException("Analyzer schema must be provided.");
        }

        var request = new HttpRequestMessage(HttpMethod.Put, GetAnalyzerUrl(analyzerId))
        {
            Content = new StringContent(templateJson, Encoding.UTF8, "application/json")
        };

        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        _logger.LogInformation($"Analyzer {analyzerId} create request accepted.");
        return response;
    }

    public async Task<HttpResponseMessage> DeleteAnalyzerAsync(string analyzerId)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, GetAnalyzerUrl(analyzerId));
        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        _logger.LogInformation($"Analyzer {analyzerId} deleted.");
        return response;
    }

    public async Task<HttpResponseMessage> BeginAnalyzeAsync(string analyzerId, string fileLocation)
    {
        HttpContent content;

        if (File.Exists(fileLocation))
        {
            var fileBytes = await File.ReadAllBytesAsync(fileLocation);
            content = new ByteArrayContent(fileBytes);
            content.Headers.Add("Content-Type", "application/octet-stream");
        }
        else if (fileLocation.StartsWith("http://") || fileLocation.StartsWith("https://"))
        {
            var urlData = new { url = fileLocation };
            var jsonString = JsonSerializer.Serialize(urlData);
            content = new StringContent(jsonString, Encoding.UTF8, "application/json");
        }
        else
        {
            throw new ArgumentException("File location must be a valid path or URL.");
        }

        var request = new HttpRequestMessage(HttpMethod.Post, GetAnalyzeUrl(analyzerId))
        {
            Content = content
        };

        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        _logger.LogInformation($"Analyzing file {fileLocation} with analyzer: {analyzerId}");
        return response;
    }

    public async Task<string> PollResultAsync(
        HttpResponseMessage response,
        int timeoutSeconds = 120,
        int pollingIntervalSeconds = 2)
    {
        if (!response.Headers.TryGetValues("operation-location", out var operationLocations))
        {
            throw new InvalidOperationException("Operation location not found in response headers.");
        }

        var operationLocation = operationLocations.First();
        var startTime = DateTime.UtcNow;

        while (true)
        {
            var elapsedTime = DateTime.UtcNow - startTime;
            if (elapsedTime.TotalSeconds > timeoutSeconds)
            {
                throw new TimeoutException($"Operation timed out after {elapsedTime.TotalSeconds:F2} seconds.");
            }

            var pollRequest = new HttpRequestMessage(HttpMethod.Get, operationLocation);
            var pollResponse = await _httpClient.SendAsync(pollRequest);
            pollResponse.EnsureSuccessStatusCode();

            var responseContent = await pollResponse.Content.ReadAsStringAsync();
            var statusResult = JsonSerializer.Deserialize<JsonElement>(responseContent);
            var status = statusResult.GetProperty("status").GetString()?.ToLowerInvariant();

            if (status == "succeeded")
            {
                _logger.LogInformation($"Request result is ready after {elapsedTime.TotalSeconds:F2} seconds.");
                return responseContent;
            }
            else if (status == "failed")
            {
                _logger.LogError($"Request failed. Reason: {responseContent}");
                throw new InvalidOperationException("Request failed.");
            }
            else
            {
                var operationId = operationLocation.Split('/').Last().Split('?')[0];
                _logger.LogDebug($"Request {operationId} in progress ...");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollingIntervalSeconds));
        }
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }
}
