using System.ComponentModel.DataAnnotations;

public sealed class Settings : IValidatableObject
{
    public string? ContractPolicyAgentInstructionsPath { get; set; }
    public string? ContractPolicyAgentId { get; set; }
    public string? ContractAnalysisAgentInstructionsPath { get; set; }
    public string? ContractAnalysisAgentId { get; set; }
    public required string AzureAiAgentEndpoint { get; set; }
    public required string AzureAiAgentModelDeploymentName { get; set; }
    public required string AzureAiCuEndpoint { get; set; }
    public required string AzureAiCuApiVersion { get; set; }
    public required string AzureAiCuSubscription { get; set; }
    public required string AzureAiCuSchemaFilePath { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(ContractAnalysisAgentInstructionsPath) && string.IsNullOrWhiteSpace(ContractAnalysisAgentId))
        {
            yield return new ValidationResult(
                "Either ContractAnalysisAgentInstructionsPath or ContractAnalysisAgentId must be set.",
                new[] { nameof(ContractAnalysisAgentInstructionsPath), nameof(ContractAnalysisAgentId) }
            );
        }
        if (string.IsNullOrWhiteSpace(ContractPolicyAgentInstructionsPath) && string.IsNullOrWhiteSpace(ContractPolicyAgentId))
        {
            yield return new ValidationResult(
                "Either ContractPolicyAgentInstructionsPath or ContractPolicyAgentId must be set.",
                new[] { nameof(ContractPolicyAgentInstructionsPath), nameof(ContractPolicyAgentId) }
            );
        }
    }
}
