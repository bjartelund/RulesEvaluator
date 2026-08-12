namespace Grains;

public interface IRulesGrain : IGrainWithStringKey
{
    /// <summary>
    /// Ingests unstructured policy text, generates/validates the Prolog rules via LLM, 
    /// and stores the compiled rules and derived schema internally.
    /// </summary>
    [Alias("IngestPolicyAsync")]
    Task<bool> IngestPolicyAsync(string policyText, CancellationToken cancellationToken = default);

    /// <summary>
    /// Evaluates a candidate's CV against the internally stored rules and schema.
    /// </summary>
    [Alias("EvaluateCandidateAsync")]
    Task<EvaluationResult> EvaluateCandidateAsync(string cvText, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the currently active rule set (the Prolog program ingested for this policy),
    /// or null if no policy has been ingested yet.
    /// </summary>
    [Alias("GetRulesAsync")]
    Task<PrologProgram?> GetRulesAsync(CancellationToken cancellationToken = default);
}