namespace Grains;

[GenerateSerializer]
[Alias("Grains.EvaluationResult")]
public record EvaluationResult(bool Eligible, IReadOnlyList<RuleEvaluation> RuleResults)
{
    // Convenience constructor for early-exit/failure paths where no rules could be evaluated.
    public EvaluationResult(bool eligible) : this(eligible, [])
    {
    }
}

[GenerateSerializer]
[Alias("Grains.RuleEvaluation")]
public record RuleEvaluation(string Name, bool Satisfied, IReadOnlyList<string> References, string RuleText);
