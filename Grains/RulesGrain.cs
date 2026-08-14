using System.Text.Json;
using System.Text.Json.Nodes;
using DotProlog.Compiler;
using DotProlog.Runtime;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Grains;

public class RulesGrain(
  [FromKeyedServices("chat")] IChatClient chatClient,
  [PersistentState("rules", "Default")] IPersistentState<RulesState> state,
  ILogger<RulesGrain> logger) : Grain, IRulesGrain
{
  public async Task<bool> IngestPolicyAsync(string policyText, CancellationToken cancellationToken = default)
  {
    var systemPromptText =
            "You are an expert assistant to a caseworker. You will reduce an unstructured set of requirements to a set of prolog rules structured following the schema. " +
            "For every rule clause, give it a short human-readable name and list the sections/paragraphs of the source policy text that support it in 'references' " +
            "(e.g. 'Section 3.2', 'Paragraph 4'). Use an empty array for 'references' and null for 'name' only for clauses that don't map to a distinct citable requirement. " +
            "Atom values (kind 'atom') must be plain lowercase snake_case identifiers, e.g. 'microsoft_word' or 'internet_browsing' - never multi-word phrases with spaces or " +
            "capitalized text such as 'Microsoft Word'. A single malformed atom aborts the entire rule set at consult time, so this matters.\n\n" +
            "Variable and predicate-shape rules (violating these makes every rule silently fail at evaluation time, since candidate facts are " +
            "always asserted as pred(candidate, Value) - exactly two arguments):\n" +
            "1. Never use the anonymous variable for the subject of a clause - not as the literal character '_', and not as a " +
            "variable term whose 'name' field is empty or missing either. Every occurrence of the subject within one clause (head " +
            "and all body goals) must give 'name' the exact same non-empty identifier (e.g. 'Subject'), so bindings actually " +
            "thread through. An anonymous variable is a fresh, unrelated variable every time it appears - whether written as '_' " +
            "or as a variable term with no name - so a clause meaning 'cv_compliant(_) :- has_valid_email(_).' never actually " +
            "connects the two, and neither does one where every variable term has name: \"\".\n" +
            "2. Every predicate that checks a leaf/extractable candidate attribute (something that maps to one fact) must be called with exactly " +
            "two arguments: (Subject, Value) - e.g. 'has_valid_email(Subject, true)', matching how the fact will be asserted as " +
            "'has_valid_email(candidate, true)'. Never call a leaf predicate with just one argument like 'has_valid_email(Subject)'.\n" +
            "3. A top-level named rule clause (the one given a 'name' and 'references', representing one distinct policy requirement) should " +
            "have exactly one argument in its head - the subject variable - e.g. 'compliant_cv(Subject) :- has_valid_email(Subject, true), ...'.\n" +
            "4. A leaf predicate is asserted as exactly ONE fact with exactly ONE value - so a range/threshold check (e.g. 'word count between " +
            "300 and 900') must NOT be written as a single multi-argument predicate like 'word_count_between(Subject, 300, 900)', because no " +
            "fact of that arity will ever exist. Instead, extract the single number as a leaf fact (e.g. 'word_count(Subject, N)') and compare " +
            "it with standard arithmetic operators in the rule body (e.g. 'N >= 300, N =< 900').\n" +
            "5. Every predicate call in a rule body must actually be called - i.e. written as a compound term with its arguments, " +
            "never as a bare atom with no parentheses (e.g. 'has_standard_sections' by itself is invalid; it must be " +
            "'has_standard_sections(Subject, true)'). This also applies to comparison operators: '>=' and '=<' must appear with " +
            "their two operands (e.g. 'N >= 300'), never as bare atoms on their own.\n" +
            "   Term structure (always include 'kind' and 'args', populate only the fields matching that kind):\n" +
            "   - atom (e.g. 'true', 'false', 'my_atom'): {kind: 'atom', value: 'atom_name', name: null, functor: null, args: []}\n" +
            "   - number (e.g. 123, 45.6): {kind: 'number', value: 123, name: null, functor: null, args: []}\n" +
            "   - variable (e.g. Subject, X, N): {kind: 'variable', value: null, name: 'Subject', functor: null, args: []}\n" +
            "   - compound (e.g. has_email(Subject, true)): {kind: 'compound', value: null, name: null, functor: 'has_email', args: [{...Term...}, {...Term...}]}\n" +
            "   NEVER include fields that don't apply to the term's kind, and NEVER duplicate any JSON keys (especially 'functor' or 'name').\n\n" +
            "6. Always emit exactly one additional top-level clause with head 'eligible(Subject)', whose body conjoins every " +
            "other named requirement predicate for that same Subject. This is the overall pass/fail verdict for the candidate " +
            "and must be present even if it does nothing but AND together all the other named rules - evaluation always queries " +
            "'eligible(X)' to determine the final outcome.\n" +
            "7. Example of a correctly-shaped rule set:\n" +
            "   eligible(Subject) :- has_valid_email(Subject, true), length_appropriate(Subject).\n" +
            "   length_appropriate(Subject) :- word_count(Subject, N), N >= 300, N =< 900.\n" +
            "   (where 'has_valid_email' and 'word_count' are leaf predicates that will be asserted as facts on the candidate).\n\n" +
            "8. In the top-level 'factPredicates' array, declare exactly one entry for every leaf predicate you " +
            "used in a rule body (every predicate that isn't itself the head of another clause and isn't a " +
            "comparison/control operator) - each entry gives that predicate's 'name' and the JSON value 'type' " +
            "it should be extracted as: 'boolean' for a predicate whose value is compared against the atoms " +
            "true/false (e.g. 'has_valid_email'), 'number' for one whose value feeds arithmetic comparison " +
            "(e.g. 'word_count' used with '>=' or '=<'), 'array' for one whose value is a list matched with " +
            "list operations (e.g. 'skills'), and 'string' for anything else (e.g. a category or free-text " +
            "atom). Every leaf predicate referenced anywhere in the rules must have a matching entry here - a " +
            "predicate with no entry, or the wrong type, means the fact extraction step downstream will request " +
            "the wrong kind of value and the rule can never be satisfied.\n\n" +
            "CRITICAL JSON VALIDATION REQUIREMENTS:\n" +
            "- Your entire response must be valid JSON matching the provided schema exactly.\n" +
            "- NEVER duplicate any JSON object keys (e.g., 'functor' appearing twice in the same object).\n" +
            "- Each Term object must have 'kind' and 'args' fields. Include 'value' ONLY for atoms/numbers, " +
            "  'name' ONLY for variables, 'functor' ONLY for compound terms.\n" +
            "- The schema validation is strict: a single misformatted term aborts the entire ingestion, so precision is critical.";

    var chatOptions = new ChatOptions
    {
      // ChatResponseFormatJson maps 1:1 onto OpenAI's structured-outputs JSON schema format -
      // the underlying OpenAI adapter always requests strict mode when a schema is supplied.
      ResponseFormat = new ChatResponseFormatJson(
        JsonDocument.Parse(RulesSchema).RootElement, "prolog_program")
    };

    List<ChatMessage> input =
    [
      new ChatMessage(ChatRole.System, systemPromptText),
      new ChatMessage(ChatRole.User, policyText)
    ];
    var response = await chatClient.GetResponseAsync(input, chatOptions, cancellationToken);
    var program = JsonSerializer.Deserialize<PrologProgram>(response.Text);

    if (program is null || program.Clauses.Count == 0)
    {
      return false;
    }

    // The LLM is instructed on the required clause shapes (see the system prompt above), but
    // nothing enforces that it actually followed them. Persisting an unvalidated program means
    // a malformed rule set silently sits in state until every single candidate evaluation fails
    // against it. Validate up front instead, and reject the ingestion outright so the caller
    // knows to retry rather than getting a policy that can never evaluate anyone.
    var validationErrors = ValidateProgram(program);
    if (validationErrors.Count > 0)
    {
      logger.LogError(
        "IngestPolicyAsync: generated Prolog program for policy '{PolicyId}' failed validation and was rejected:\n{Errors}",
        this.GetPrimaryKeyString(), string.Join("\n", validationErrors));
      return false;
    }

    // The shape checks above catch the mistakes the prompt calls out explicitly, but they're
    // not a substitute for the real parser/compiler - quoting edge cases, operator precedence,
    // duplicate definitions, etc. can still produce something DotProlog itself rejects. Have the
    // actual engine consult the generated rules before we ever accept them as this grain's
    // ruleset, so a bad ingestion fails loudly here instead of surfacing as existence_error on
    // every future candidate evaluation.
    var rulesText = program.ToPrologCode();
    var consultError = TryConsultRules(rulesText, logger);
    if (consultError is not null)
    {
      logger.LogError(
        "IngestPolicyAsync: generated Prolog program for policy '{PolicyId}' failed to consult and was rejected: {Error}\n--- Rules ---\n{RulesText}",
        this.GetPrimaryKeyString(), consultError, rulesText);
      return false;
    }

    state.State.Program = program;
    await state.WriteStateAsync();
    return true;
  }

  // Consults the rules text with a throwaway engine (no facts asserted) and confirms
  // 'eligible/1' actually exists once loaded, using current_predicate/1 rather than calling
  // eligible(X) directly - calling it here would immediately hit existence_error on whichever
  // leaf fact predicate it reaches first, since no candidate facts exist at ingestion time.
  // Returns null on success, or a description of what went wrong.
  private static string? TryConsultRules(string rulesText, ILogger logger)
  {
    try
    {
      var engine = new PrologEngine(PrologLanguageMode.StrictIso);
      var loaded = engine.ConsultText(rulesText);
      if (!loaded.Success)
      {
        return $"rules did not compile: {string.Join("; ", loaded.Diagnostics)}";
      }

      var query = engine.Query("current_predicate(eligible/1)");
      if (!query.Solutions().Take(1).Any())
      {
        return "rules compiled, but no 'eligible/1' predicate is defined.";
      }

      return null;
    }
    catch (PrologException ex)
    {
      logger.LogDebug(ex, "TryConsultRules: consult threw unexpectedly.");
      return $"consult threw: {ex.Message}";
    }
  }

  private static readonly HashSet<string> ComparisonOperators = new()
    { ">", "<", ">=", "=<", "=:=", "=\\=", "==", "\\==", "is" };

  private static readonly HashSet<string> ControlAtoms = new() { "true", "fail", "false", "!" };

  private static readonly HashSet<string> ControlFunctors = new() { ",", ";", "->", "\\+", "not" };

  private static readonly HashSet<string> FactPredicateTypes = new() { "boolean", "number", "string", "array" };

  // Checks the shape rules spelled out to the LLM in the ingestion prompt: named rule clauses
  // take exactly one subject argument, that same named variable threads through every body
  // goal, leaf predicate calls are 2-arg pred(Subject, Value), calls to OTHER rule clauses are
  // 1-arg pred(Subject) (a rule calling another named requirement, e.g. 'eligible' conjoining
  // all the others), comparison operators always carry both operands, and an aggregate
  // eligible/1 clause exists.
  private static List<string> ValidateProgram(PrologProgram program)
  {
    var errors = new List<string>();
    var hasEligible = false;

    // A goal calling one of these functors is a rule-to-rule call (1-arg pred(Subject)), not a
    // leaf fact lookup (2-arg pred(Subject, Value)) - collected up front since a clause can
    // reference a rule defined anywhere else in the program, including later in the list.
    var ruleFunctors = program.Clauses
      .Where(c => c.Type != "fact" && c.Head.Kind == "compound" && c.Head.Args.Count == 1)
      .Select(c => c.Head.Functor!)
      .Where(f => !string.IsNullOrEmpty(f))
      .ToHashSet();

    foreach (var clause in program.Clauses)
    {
      // Facts don't carry the (Subject) rule shape - nothing to validate structurally.
      if (clause.Type == "fact")
        continue;

      var head = clause.Head;
      if (head.Kind != "compound" || string.IsNullOrEmpty(head.Functor))
      {
        errors.Add($"Clause '{clause.Name ?? "(unnamed)"}' has a non-compound or unnamed head.");
        continue;
      }

      if (head.Functor == "eligible")
        hasEligible = true;

      if (head.Args.Count != 1)
      {
        errors.Add($"Rule '{head.Functor}': head must have exactly one argument (the subject), found {head.Args.Count}.");
        continue;
      }

      var subject = head.Args[0];
      if (subject.Kind != "variable" || string.IsNullOrEmpty(subject.Name) || subject.Name == "_")
      {
        errors.Add(
          $"Rule '{head.Functor}': head's subject argument must be a single named variable (never '_'), " +
          $"found kind='{subject.Kind}' name='{subject.Name}'.");
        continue;
      }

      foreach (var goal in clause.Body)
      {
        ValidateGoal(goal, subject.Name!, head.Functor!, ruleFunctors, errors);
      }
    }

    if (!hasEligible)
    {
      errors.Add("No top-level 'eligible/1' clause was generated - overall eligibility cannot be computed.");
    }

    // The facts schema handed to the LLM at evaluation time is built directly from
    // factPredicates (see BuildFactsSchemaFromProgram) rather than guessed from predicate
    // names, so every leaf predicate actually used in the rules must be declared here with a
    // valid type, or fact extraction has no idea what shape to ask the LLM for.
    var declaredTypes = new Dictionary<string, string>();
    foreach (var factPredicate in program.FactPredicates)
    {
      if (string.IsNullOrWhiteSpace(factPredicate.Name))
      {
        errors.Add("A 'factPredicates' entry has an empty 'name'.");
        continue;
      }

      if (!FactPredicateTypes.Contains(factPredicate.Type))
      {
        errors.Add(
          $"factPredicates entry '{factPredicate.Name}': type '{factPredicate.Type}' is not one of " +
          $"{string.Join("/", FactPredicateTypes)}.");
      }

      if (!declaredTypes.TryAdd(factPredicate.Name, factPredicate.Type))
      {
        errors.Add($"factPredicates entry '{factPredicate.Name}' is declared more than once.");
      }
    }

    foreach (var leaf in CollectLeafPredicateNames(program, ruleFunctors))
    {
      if (!declaredTypes.ContainsKey(leaf))
      {
        errors.Add(
          $"Leaf predicate '{leaf}' is called in a rule body but has no corresponding 'factPredicates' " +
          "entry declaring its value type.");
      }
    }

    return errors;
  }

  // Walks every rule body collecting the functors of leaf predicate calls - the same
  // classification ValidateGoal uses (excluding rule-to-rule calls, comparisons, and control
  // constructs) - so their declared factPredicates entries can be checked for completeness.
  private static HashSet<string> CollectLeafPredicateNames(PrologProgram program, HashSet<string> ruleFunctors)
  {
    var names = new HashSet<string>();
    foreach (var clause in program.Clauses)
    {
      if (clause.Type == "fact")
        continue;

      foreach (var goal in clause.Body)
      {
        CollectLeafPredicateNames(goal, ruleFunctors, names);
      }
    }

    return names;
  }

  private static void CollectLeafPredicateNames(Term goal, HashSet<string> ruleFunctors, HashSet<string> names)
  {
    if (goal.Kind != "compound" || string.IsNullOrEmpty(goal.Functor))
      return;

    if (ruleFunctors.Contains(goal.Functor) || ComparisonOperators.Contains(goal.Functor))
      return;

    if (ControlFunctors.Contains(goal.Functor))
    {
      foreach (var arg in goal.Args)
      {
        CollectLeafPredicateNames(arg, ruleFunctors, names);
      }

      return;
    }

    names.Add(goal.Functor);
  }

  private static void ValidateGoal(Term goal, string subjectName, string ruleFunctor, HashSet<string> ruleFunctors, List<string> errors)
  {
    if (goal.Kind == "atom")
    {
      if (!ControlAtoms.Contains(goal.Value ?? string.Empty))
      {
        errors.Add(
          $"Rule '{ruleFunctor}': body goal '{goal.Value}' is a bare atom with no arguments - " +
          "leaf predicates must be called as pred(Subject, Value) and comparisons must carry both operands.");
      }

      return;
    }

    if (goal.Kind != "compound" || string.IsNullOrEmpty(goal.Functor))
    {
      errors.Add($"Rule '{ruleFunctor}': body goal has kind '{goal.Kind}', expected a compound predicate call.");
      return;
    }

    if (ruleFunctors.Contains(goal.Functor))
    {
      // A call to another named requirement clause - pred(Subject), same shape as the head.
      if (goal.Args.Count != 1)
      {
        errors.Add($"Rule '{ruleFunctor}': call to rule '{goal.Functor}' must have exactly 1 argument (the subject), found {goal.Args.Count}.");
        return;
      }

      var arg = goal.Args[0];
      if (arg.Kind != "variable" || arg.Name != subjectName)
      {
        errors.Add(
          $"Rule '{ruleFunctor}': call to rule '{goal.Functor}' must pass the clause's subject variable '{subjectName}', " +
          $"found kind='{arg.Kind}' name='{arg.Name}'.");
      }

      return;
    }

    if (ComparisonOperators.Contains(goal.Functor))
    {
      if (goal.Args.Count != 2)
      {
        errors.Add($"Rule '{ruleFunctor}': comparison '{goal.Functor}' must have exactly 2 operands, found {goal.Args.Count}.");
      }

      return;
    }

    if (ControlFunctors.Contains(goal.Functor))
    {
      foreach (var arg in goal.Args)
      {
        ValidateGoal(arg, subjectName, ruleFunctor, ruleFunctors, errors);
      }

      return;
    }

    // Everything else is a leaf predicate call - must be pred(Subject, Value), with the first
    // argument the same named subject variable that appears in the clause head.
    if (goal.Args.Count != 2)
    {
      errors.Add($"Rule '{ruleFunctor}': predicate '{goal.Functor}' must be called with exactly 2 arguments (Subject, Value), found {goal.Args.Count}.");
      return;
    }

    var firstArg = goal.Args[0];
    if (firstArg.Kind != "variable" || firstArg.Name != subjectName)
    {
      errors.Add(
        $"Rule '{ruleFunctor}': predicate '{goal.Functor}' first argument must be the clause's subject variable '{subjectName}', " +
        $"found kind='{firstArg.Kind}' name='{firstArg.Name}'.");
    }
  }

  public Task<PrologProgram?> GetRulesAsync(CancellationToken cancellationToken = default)
  {
    return Task.FromResult(state.State.Program);
  }

  public async Task<EvaluationResult> EvaluateCandidateAsync(string applicationText, CancellationToken cancellationToken = default)
  {
    // Fail fast if no rules have been ingested
    var program = state.State.Program;
    if (program is null || program.Clauses.Count == 0)
    {
      logger.LogWarning("EvaluateCandidateAsync: no rules ingested for policy '{PolicyId}'.", this.GetPrimaryKeyString());
      return new EvaluationResult(false);
    }

    var rulesText = program.ToPrologCode();
    logger.LogInformation("EvaluateCandidateAsync: consulting rules for policy '{PolicyId}':\n{RulesText}", this.GetPrimaryKeyString(), rulesText);

    // Build the facts schema from the LLM-declared factPredicates (not persisted separately -
    // derived on demand from the ingested program).
    var factsSchema = BuildFactsSchemaFromProgram(program);

    // Extract structured candidate facts from application text using the LLM
    // Use the schema that was generated when the rules were ingested
    var systemPromptText =
      "You are an expert assistant. Extract structured candidate facts from the CV text into JSON format. " +
      "Follow the provided schema exactly, extracting only the properties defined there. " +
      "Set properties to null if the information is not available in the CV.";
    var userPromptText = $"Extract candidate facts from this CV:\n\n{applicationText}";

    var chatOptions = new ChatOptions
    {
      ResponseFormat = new ChatResponseFormatJson(
        JsonDocument.Parse(factsSchema).RootElement, "candidate_facts")
    };

    List<ChatMessage> messages =
    [
      new ChatMessage(ChatRole.System, systemPromptText),
      new ChatMessage(ChatRole.User, userPromptText)
    ];
    var response = await chatClient.GetResponseAsync(messages, chatOptions, cancellationToken);
    var factJson = JsonSerializer.Deserialize<JsonElement>(response.Text);

    if (factJson.ValueKind == JsonValueKind.Null)
    {
      logger.LogWarning("EvaluateCandidateAsync: LLM fact extraction returned null for policy '{PolicyId}'. Raw completion: {Raw}",
        this.GetPrimaryKeyString(), response.Text);
      return new EvaluationResult(false);
    }

    // Convert structured facts (dynamic JSON) to Prolog facts text
    string factsText = RenderCandidateFactsFromJson(factJson);
    logger.LogInformation("EvaluateCandidateAsync: extracted candidate facts for policy '{PolicyId}':\n{FactsText}", this.GetPrimaryKeyString(), factsText);

    // Run evaluation with timeout (per dotProlog.md guidance on preventing runaway queries)
    try
    {
      var result = await RunWithTimeoutAsync(
        () => EvaluateWithProlog(rulesText, factsText, program, logger),
        TimeSpan.FromSeconds(5),
        cancellationToken);

      logger.LogInformation("EvaluateCandidateAsync: evaluation for policy '{PolicyId}' completed. Eligible={Eligible}, Rules={Rules}",
        this.GetPrimaryKeyString(), result.Eligible,
        string.Join(", ", result.RuleResults.Select(r => $"{r.Name}={r.Satisfied}")));

      return result;
    }
    catch (TimeoutException ex)
    {
      logger.LogError(ex, "EvaluateCandidateAsync: evaluation timed out for policy '{PolicyId}'.", this.GetPrimaryKeyString());
      return new EvaluationResult(false);
    }
  }

  private static EvaluationResult EvaluateWithProlog(string rulesText, string factsText, PrologProgram program, ILogger logger)
  {
    try
    {
      // One engine per call: PrologEngine is not thread-safe and must not be shared
      // across concurrent grain activations (per dotProlog.md)
      var engine = new PrologEngine(PrologLanguageMode.StrictIso);

      try
      {
        engine.ConsultText(rulesText);
      }
      catch (PrologException ex)
      {
        // A single malformed clause (e.g. an unquoted multi-word atom like "Microsoft Word")
        // can abort the whole ConsultText call, silently leaving every rule predicate
        // undefined. Surface that distinctly from a rule simply not holding.
        logger.LogError(ex, "EvaluateWithProlog: rules text failed to consult - no rule predicates were loaded.\n--- Rules ---\n{RulesText}", rulesText);
        throw;
      }

      try
      {
        engine.ConsultText(factsText);
      }
      catch (PrologException ex)
      {
        logger.LogError(ex, "EvaluateWithProlog: facts text failed to consult.\n--- Facts ---\n{FactsText}", factsText);
        throw;
      }

      var ruleResults = new List<RuleEvaluation>();
      foreach (var clause in program.Clauses)
      {
        // Only named rules (not raw facts) are reported as individually evaluated criteria
        if (clause.Type != "rule" || clause.Head.Kind != "compound" || string.IsNullOrEmpty(clause.Head.Functor))
        {
          logger.LogDebug("EvaluateWithProlog: skipping clause (type={Type}, headKind={HeadKind}, functor={Functor}) - not an evaluable named rule.",
            clause.Type, clause.Head.Kind, clause.Head.Functor);
          continue;
        }

        var satisfied = TryQueryClause(engine, clause.Head, logger);
        var name = string.IsNullOrWhiteSpace(clause.Name) ? clause.Head.Functor! : clause.Name!;
        logger.LogInformation("EvaluateWithProlog: rule '{Name}' ({Functor}) satisfied={Satisfied}", name, clause.Head.Functor, satisfied);
        ruleResults.Add(new RuleEvaluation(name, satisfied, clause.References, clause.ToPrologCode()));
      }

      // Overall eligibility - try the conventional eligible(X) predicate
      PrologQuery query = engine.Query("eligible(X)");
      var eligible = query.Solutions().Take(1).Any();
      logger.LogInformation("EvaluateWithProlog: eligible(X) query resolved to {Eligible}", eligible);

      return new EvaluationResult(eligible, ruleResults);
    }
    catch (PrologException ex)
    {
      // If there's a Prolog error, default to not eligible
      logger.LogError(ex, "EvaluateWithProlog: Prolog error while consulting rules/facts.\n--- Rules ---\n{RulesText}\n--- Facts ---\n{FactsText}",
        rulesText, factsText);
      return new EvaluationResult(false);
    }
  }

  private static bool TryQueryClause(PrologEngine engine, Term head, ILogger logger)
  {
    // Build a query from the rule's head, binding the first (subject) argument to the
    // 'candidate' atom - matching the convention used when rendering candidate facts
    // (predicate(candidate, Value)) - and leaving any remaining arguments anonymous.
    string queryText = "(not built)";
    try
    {
      if (head.Args.Count == 0)
      {
        queryText = head.Functor!;
      }
      else
      {
        var args = head.Args.Select((_, i) => i == 0 ? "candidate" : "_");
        queryText = $"{head.Functor}({string.Join(", ", args)})";
      }

      var query = engine.Query(queryText);
      var satisfied = query.Solutions().Take(1).Any();
      logger.LogDebug("TryQueryClause: query '{QueryText}' => {Satisfied}", queryText, satisfied);
      return satisfied;
    }
    catch (PrologException ex)
    {
      logger.LogWarning(ex, "TryQueryClause: query '{QueryText}' threw a Prolog error - treating rule as not satisfied.", queryText);
      return false;
    }
  }

  private static string RenderCandidateFactsFromJson(JsonElement factJson)
  {
    var lines = new List<string>();

    if (factJson.ValueKind != JsonValueKind.Object)
      return string.Empty;

    foreach (var property in factJson.EnumerateObject())
    {
      var propName = property.Name;
      var propValue = property.Value;

      // Skip null values
      if (propValue.ValueKind == JsonValueKind.Null)
        continue;

      // Handle different value types
      string? factLine = propValue.ValueKind switch
      {
        JsonValueKind.String =>
          $"{propName}(candidate, '{EscapeAtom(propValue.GetString() ?? "")}').",

        JsonValueKind.Number =>
          $"{propName}(candidate, {propValue.GetRawText()}).",

        JsonValueKind.True =>
          $"{propName}(candidate, true).",

        JsonValueKind.False =>
          $"{propName}(candidate, false).",

        JsonValueKind.Array =>
          RenderArrayAsPrologList(propName, propValue),

        JsonValueKind.Object =>
          RenderObjectAsCompound(propName, propValue),

        _ => null
      };

      if (!string.IsNullOrEmpty(factLine))
        lines.Add(factLine);
    }

    return string.Join(Environment.NewLine, lines);
  }

  private static string? RenderArrayAsPrologList(string propName, JsonElement arrayJson)
  {
    var elements = new List<string>();

    foreach (var elem in arrayJson.EnumerateArray())
    {
      string? elemStr = elem.ValueKind switch
      {
        JsonValueKind.String => $"'{EscapeAtom(elem.GetString() ?? "")}'",
        JsonValueKind.Number => elem.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null
      };

      if (elemStr is not null)
        elements.Add(elemStr);
    }

    if (elements.Count == 0)
      return null;

    var listStr = string.Join(", ", elements);
    return $"{propName}(candidate, [{listStr}]).";
  }

  private static string? RenderObjectAsCompound(string propName, JsonElement objJson)
  {
    // For nested objects, we could render them as compound terms or skip them
    // For now, skip nested objects - they're not typically used in facts
    _ = propName;
    _ = objJson;
    return null;
  }

  private static string EscapeAtom(string value) => value.Replace("'", "\\'");

  private static async Task<T> RunWithTimeoutAsync<T>(
    Func<T> work,
    TimeSpan timeout,
    CancellationToken cancellationToken = default)
  {
    // Run on a background thread with timeout protection
    // (DotProlog gives no cooperative cancellation, so this is host-side abandon per dotProlog.md)
    var task = Task.Run(work, cancellationToken);
    var completed = await Task.WhenAny(task, Task.Delay(timeout, cancellationToken));

    if (completed != task)
    {
      throw new TimeoutException("Candidate evaluation exceeded the allotted time budget.");
    }

    return await task; // Re-throws PrologException if any
  }

  private static string BuildFactsSchemaFromProgram(PrologProgram program)
  {
    // The facts schema is built straight from the LLM-declared factPredicates (validated by
    // ValidateProgram at ingestion time) rather than guessed back from predicate names - a
    // heuristic like "contains 'is_' -> boolean" is fragile and was observed producing
    // fields that were clearly meant as booleans but got extracted as strings, which then
    // silently failed every rule comparing them against true/false.
    //
    // Note: Anthropic's structured outputs don't allow oneOf/anyOf/allOf, and a "type"
    // array with more than one non-null option (e.g. ["string","number","boolean","null"])
    // gets treated as an implicit oneOf and rejected. So each property must resolve to a
    // single concrete type, optionally paired with "null" for a nullable [type, "null"] form.
    var properties = new Dictionary<string, object>();
    foreach (var factPredicate in program.FactPredicates.OrderBy(p => p.Name))
    {
      // A "type" that includes "array" must carry an "items" schema - Anthropic rejects
      // array-typed properties that don't declare what they're an array of.
      properties[factPredicate.Name] = factPredicate.Type == "array"
        ? new { type = new[] { "array", "null" }, items = new { type = "string" } }
        : new { type = new[] { factPredicate.Type, "null" } };
    }

    // No fallback: if the ingested program declares no factPredicates, ingestion itself
    // produced something unusable (ValidateProgram should have already rejected this at
    // ingest time). Silently substituting an unrelated hardcoded schema would let
    // evaluation proceed against facts that have nothing to do with the actual rules,
    // hiding a real ingestion failure. Fail hard instead.
    if (properties.Count == 0)
    {
      throw new InvalidOperationException(
        "The ingested Prolog program declares no factPredicates - no facts schema can be built. " +
        "Policy ingestion likely produced unusable rules.");
    }

    var schemaJson = new JsonObject
    {
      ["$schema"] = "http://json-schema.org/draft-07/schema#",
      ["title"] = "CandidateFacts",
      ["type"] = "object",
      ["properties"] = JsonNode.Parse(JsonSerializer.Serialize(properties)),
      // Strict structured-output schemas require additionalProperties: false, and every
      // declared property must be listed in "required" (nullable properties satisfy this
      // via their [type, "null"] union - the model can still emit null for them).
      ["additionalProperties"] = false,
      ["required"] = new JsonArray(properties.Keys.Select(k => (JsonNode)k).ToArray())
    };

    return schemaJson.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
  }

  private const string RulesSchema = """
                                     {
                                       "$schema": "http://json-schema.org/draft-07/schema#",
                                       "title": "PrologProgram",
                                       "type": "object",
                                       "properties": {
                                         "clauses": {
                                           "type": "array",
                                           "items": {
                                             "$ref": "#/$defs/Clause"
                                           }
                                         },
                                         "factPredicates": {
                                           "type": "array",
                                           "description": "One entry per leaf fact predicate referenced anywhere in the rule bodies, declaring the JSON type it should be extracted as.",
                                           "items": {
                                             "$ref": "#/$defs/FactPredicate"
                                           }
                                         }
                                       },
                                       "required": ["clauses", "factPredicates"],
                                       "additionalProperties": false,
                                       "$defs": {
                                         "FactPredicate": {
                                           "type": "object",
                                           "properties": {
                                             "name": {
                                               "type": "string",
                                               "description": "The leaf predicate's functor name, e.g. 'has_valid_email' or 'word_count'."
                                             },
                                             "type": {
                                               "type": "string",
                                               "enum": ["boolean", "number", "string", "array"],
                                               "description": "'boolean' if compared against true/false; 'number' if used in arithmetic comparisons; 'array' if matched as a list; 'string' otherwise."
                                             }
                                           },
                                           "required": ["name", "type"],
                                           "additionalProperties": false
                                         },
                                         "Clause": {
                                           "type": "object",
                                           "properties": {
                                             "type": {
                                               "type": "string",
                                               "enum": ["fact", "rule"]
                                             },
                                             "name": {
                                               "type": ["string", "null"],
                                               "description": "Short human-readable name for this rule, e.g. 'Minimum years of experience'"
                                             },
                                             "references": {
                                               "type": "array",
                                               "description": "Sections/paragraphs of the source policy text that support this rule, e.g. 'Section 3.2' or 'Paragraph 4'",
                                               "items": {
                                                 "type": "string"
                                               }
                                             },
                                             "head": {
                                               "$ref": "#/$defs/Term"
                                             },
                                             "body": {
                                               "type": "array",
                                               "items": {
                                                 "$ref": "#/$defs/Term"
                                               }
                                             }
                                           },
                                           "required": ["type", "name", "references", "head", "body"],
                                           "additionalProperties": false
                                         },
                                         "Term": {
                                           "type": "object",
                                           "properties": {
                                             "kind": {
                                               "type": "string",
                                               "enum": ["atom", "number", "variable", "compound"],
                                               "description": "Type of term: 'atom' (constants like 'true' or 'my_atom'), 'number' (integer or float), 'variable' (uppercase identifier like 'X' or 'Subject'), or 'compound' (functor with arguments)"
                                             },
                                             "value": {
                                               "type": ["string", "number", "null"],
                                               "description": "For atoms/numbers: the literal value. For variables/compounds: null."
                                             },
                                             "name": {
                                               "type": ["string", "null"],
                                               "description": "For variables: the variable name (uppercase, never empty or '_'). For others: null."
                                             },
                                             "functor": {
                                               "type": ["string", "null"],
                                               "description": "For compound terms: the predicate name. For atoms/variables/numbers: null."
                                             },
                                             "args": {
                                               "type": "array",
                                               "description": "For compound terms: the arguments (each a Term). For atoms/variables/numbers: empty array [].",
                                               "items": {
                                                 "$ref": "#/$defs/Term"
                                               }
                                             }
                                           },
                                           "required": ["kind", "args"],
                                           "additionalProperties": false
                                         }
                                       }
                                     }
                                     """;
}

[GenerateSerializer]
[Alias("Grains.RulesState")]
public class RulesState
{
    [Id(0)]
    public PrologProgram? Program { get; set; }
}