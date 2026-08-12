# DotProlog suitability evaluation for RulesEvaluator

## Context

RulesEvaluator (per the phase plan) needs an **in-process Prolog engine** to:

- consult LLM-generated Prolog text (facts + rules) stored per rule-set version in `RulesRegistryGrain`,
- run it against structured application facts inside `ApplicationEvaluationGrain` (an Orleans grain,
  i.e. single-threaded per activation, but many activations run concurrently in the same silo process),
- get back solutions/bindings (success/failure, matched clauses) to hand to an LLM for a
  human-readable explanation.

Phase 4 explicitly names "an in-process engine like C#Prolog or a lightweight local evaluator" as the
candidate slot. DotProlog (the repo dropped into `dotprolog/`) is one option for that slot. Findings
below are from reading its `README.md`, `COMPATIBILITY.md`, `CHANGELOG.md`, `docs/dotnet-integration.md`,
and relevant source (`Machine.cs`, `CoreBuiltins.cs`).

## What DotProlog is

- A from-scratch, ISO-track Prolog implementation for **.NET 10 / C# 14**, published to NuGet.org
  (`DotProlog.Compiler` is "the package to reference to embed Prolog in a .NET application"), current
  version **0.5.0**.
- Embedding API is exactly the shape RulesEvaluator needs:
  ```csharp
  var engine = new PrologEngine(PrologLanguageMode.StrictIso);
  engine.ConsultText(rawPrologText);          // dynamic text, not just files — fits per-version rule sets
  foreach (var solution in engine.Query("eligible(X)").Solutions())
      Console.WriteLine(solution["X"]);
  bool ok = engine.Query("eligible(applicant1)").Prove();
  ```
  `ConsultText` + `Query(...).Solutions()`/`.Prove()` maps directly onto "load the active rule text from
  `RulesRegistryGrain`, load the structured application facts, query the decision predicate."
- Solutions are marshalled into plain `PrologValue`s eagerly per answer, so results stay valid after
  the query has moved on — convenient for handing "matched clauses / bindings" to the results-explanation
  LLM step in Phase 5.

## Fit with the Orleans/Aspire architecture

- **.NET version matches exactly.** `Silo`, `Grains`, `AppHost`, `ServiceDefaults` are all already
  `net10.0`; DotProlog also targets net10.0/C#14. No downgrade or multi-targeting needed.
- **Not thread-safe per engine, by design** ("One engine runs one goal at a time and is not
  thread-safe. Use separate engine instances when independent callers need to execute concurrently").
  This is actually a *good* fit for Orleans: create one `PrologEngine` per evaluation call (or cache one
  per grain activation), never share an instance across grains/threads. Since Orleans single-threads
  each grain's turns anyway, this constraint costs nothing extra as long as the engine isn't stashed in
  a singleton/static.
- **`halt/1` is per-engine, not process-wide.** Checked in source (`Machine.RequestHalt` just sets a
  flag on that `Machine`/engine and closes its own streams) — an LLM-generated rule set that happens to
  contain a stray `halt.` directive cannot take down the whole Silo process. This matters because rule
  text in this project is LLM-authored, i.e. semi-untrusted.
- **No query timeout / cancellation, no recursion-depth guard.** There's no `CancellationToken` on
  `Query(...)`, and no built-in max-depth or step-budget setting was found. An LLM-generated rule with
  unguarded left recursion or a runaway `between/3`-style generator would hang the calling grain's
  turn indefinitely. The README's own mitigation is `Solutions().Take(n)` (lazy enumeration), which
  bounds *nondeterministic* runaway but not a single non-terminating deterministic goal. For Phase 4,
  wrap evaluation calls in an externally-imposed timeout (e.g. run the engine call on a bounded
  `Task.Run` with a cancellation-based abandon, since the engine itself won't cooperate) rather than
  relying on DotProlog to self-limit.
- **No sandboxing of the standard library.** Stream/file I/O predicates (`open/3,4`, byte/char/text
  streams) are implemented and reachable from consulted text. Since rule text here is LLM-generated
  and stored/executed server-side, this is a real (if narrow) risk: a rule set could in principle try
  to `open` a file on the Silo's disk. There's no allow-list or restricted-builtins mode surfaced in the
  docs. Mitigate by running evaluation in a locked-down working directory/identity, or by prompt-
  constraining the LLM to a fact/rule subset — DotProlog itself won't enforce this.

## Maturity / risk

- **README says "Status: early, but usable."** The compiler front-end (`plc`) and IL generation for
  predicate bodies are explicitly *not done yet* — consulted Prolog runs on a bytecode VM instead,
  which is fine functionally but means this is pre-1.0 software, current release 0.5.0.
- Conformance work looks serious (768/802 ISO Part 1 declarations passing, Logtalk conformance suite,
  1,400+ tests) — the *language semantics* are trustworthy for what's implemented, which de-risks
  "will `eligible(X) :- age(X, A), A >= 18.` behave correctly" type concerns.
- `CHANGELOG.md` shows active, fast iteration (0.4.0 → 0.5.0 in a day in the sample history) with
  behavior-changing fixes to core ISO error handling as recently as the latest release — expect API/
  semantics churn if you pin to a pre-1.0 version and upgrade later.
- No published throughput/latency benchmarks against realistic ruleset sizes were found (a
  `benchmarks/DotProlog.Benchmarks` project exists in the repo but wasn't run as part of this review) —
  worth a quick spike before committing, given Phase 4 puts this call in the request path per
  application evaluation.

## Alternatives considered (per the phase text)

The phase text explicitly floats "C#Prolog" (a much older, more battle-tested but effectively
unmaintained pure-C# ISO Prolog, no NuGet-first embedding story, older .NET conventions) as the other
named option. DotProlog is the more modern, actively developed, and better-documented embedding
target, and it uniquely already matches this project's net10.0/C#14 baseline — C#Prolog would likely
need adaptation work to fit the same toolchain.

## Recommendation

**Suitable, with two guardrails to add around it, not inside it:**

1. One `PrologEngine` instance per evaluation (never shared/cached across concurrent grain
   activations) — this is required by DotProlog's own concurrency contract and maps naturally onto
   Orleans grain semantics.
2. Wrap `engine.Query(...)` execution in your own timeout/cancellation and, if the threat model
   warrants it, restrict what the LLM is prompted to generate (facts/rules only, no directives) since
   DotProlog does not provide a sandboxed/restricted-builtins mode itself.

Given it's pre-1.0 (0.5.0), pin the exact NuGet version and re-check `CHANGELOG.md` before bumping,
rather than floating to latest.

## Implementation snippets

Reference material for later work, based on the actual `DotProlog.Compiler`/`DotProlog.Runtime` API
(`PrologEngine`, `PrologQuery`, `PrologSolution`, `PrologValue` and its subtypes `PrologAtom`,
`PrologInteger`, `PrologFloat`, `PrologCompound`, `PrologVariable`) read from source, not from docs
prose alone — so these should compile as-is against 0.5.0, modulo namespaces.

### Package reference

```xml
<!-- Grains/Grains.csproj -->
<ItemGroup>
  <PackageReference Include="DotProlog.Compiler" Version="0.5.0" />
</ItemGroup>
```

### RulesRegistryGrain — storing and validating a rule set (Phase 2 / Phase 3)

Validate LLM-generated Prolog at ingestion time by consulting it once in a throwaway engine, so a
malformed rule set never gets stored as "active":

```csharp
using DotProlog.Compiler;
using DotProlog.Runtime;

public class RulesRegistryGrain(
    [PersistentState("rules", "Default")] IPersistentState<RuleSetState> state)
    : Grain, IRulesRegistryGrain
{
    public async Task<RuleSetVersion> PublishRuleSetAsync(string prologText, string schemaJson)
    {
        // Fail fast on bad Prolog before it becomes the active rule set for anyone.
        try
        {
            var probe = new PrologEngine(PrologLanguageMode.StrictIso);
            probe.ConsultText(prologText);
        }
        catch (PrologException ex)
        {
            throw new RuleSetValidationException($"Rule set failed to consult: {ex.Message}", ex);
        }

        var version = new RuleSetVersion(Guid.NewGuid(), ComputeHash(prologText));

        state.State.PrologText = prologText;
        state.State.SchemaJson = schemaJson;
        state.State.Version = version;
        await state.WriteStateAsync();

        return version;
    }

    public Task<(string PrologText, string SchemaJson, RuleSetVersion Version)> GetActiveRuleSetAsync() =>
        Task.FromResult((state.State.PrologText, state.State.SchemaJson, state.State.Version));

    private static string ComputeHash(string text) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
```

### ApplicationEvaluationGrain — running the engine against structured facts (Phase 4)

`ConsultText` takes plain text, so structured application data (already reduced to JSON by the LLM
step) is rendered into Prolog facts and consulted alongside the active rules. Solutions marshal
into plain records, safe to keep after the query moves on — convenient for handing straight to the
Phase 5 result-explanation step.

```csharp
using DotProlog.Compiler;
using DotProlog.Runtime;

public class ApplicationEvaluationGrain(
    [PersistentState("application", "Default")] IPersistentState<ApplicationState> state,
    IGrainFactory grainFactory)
    : Grain, IApplicationEvaluationGrain
{
    public async Task<EvaluationResult> EvaluateAsync(ApplicationFacts facts)
    {
        var registry = grainFactory.GetGrain<IRulesRegistryGrain>("active");
        var (rulesText, _, version) = await registry.GetActiveRuleSetAsync();

        // Structured facts -> Prolog facts. Keep this narrow and generated, not string-interpolated
        // from raw user text, so application data can't smuggle in extra clauses or directives.
        string factsText = RenderFacts(facts);

        EvaluationOutcome outcome = await RunWithTimeoutAsync(
            () => Evaluate(rulesText, factsText),
            TimeSpan.FromSeconds(5));

        state.State.LastOutcome = outcome;
        state.State.RuleSetVersion = version;
        await state.WriteStateAsync();

        return new EvaluationResult(outcome, version);
    }

    private static EvaluationOutcome Evaluate(string rulesText, string factsText)
    {
        // One engine per call: PrologEngine is not thread-safe and must not be shared or cached
        // across concurrent grain activations.
        var engine = new PrologEngine(PrologLanguageMode.StrictIso);
        engine.ConsultText(rulesText);
        engine.ConsultText(factsText);

        PrologQuery query = engine.Query("eligible(applicant, Reason)");

        var matches = new List<string>();
        foreach (PrologSolution solution in query.Solutions())
        {
            if (solution.TryGetValue("Reason", out PrologValue reason))
            {
                matches.Add(Describe(reason));
            }
        }

        return matches.Count > 0
            ? EvaluationOutcome.Approved(matches)
            : EvaluationOutcome.Denied();
    }

    // PrologValue is a closed hierarchy: PrologAtom, PrologInteger, PrologFloat, PrologCompound,
    // PrologVariable. Pattern-match it the same way you'd pattern-match a JSON DOM.
    private static string Describe(PrologValue value) => value switch
    {
        PrologAtom atom => atom.Name,
        PrologInteger integer => integer.Value.ToString(),
        PrologFloat @float => @float.Value.ToString("G"),
        PrologCompound compound => $"{compound.Name}({string.Join(", ", compound.Arguments.Select(Describe))})",
        PrologVariable variable => variable.Name,
        _ => value.ToString() ?? string.Empty,
    };

    private static string RenderFacts(ApplicationFacts facts) =>
        string.Join(
            Environment.NewLine,
            facts.Age is { } age ? $"age(applicant, {age})." : null,
            facts.Municipality is { } m ? $"municipality(applicant, '{EscapeAtom(m)}')." : null,
            facts.Income is { } income ? $"income(applicant, {income.ToString(CultureInfo.InvariantCulture)})." : null);

    private static string EscapeAtom(string value) => value.Replace("'", "\\'");

    // DotProlog does not expose a CancellationToken on Query/Solutions, so an unbounded or
    // accidentally-recursive LLM-authored rule has to be bounded from the host side.
    private static async Task<T> RunWithTimeoutAsync<T>(Func<T> work, TimeSpan timeout)
    {
        var task = Task.Run(work);
        var completed = await Task.WhenAny(task, Task.Delay(timeout));
        if (completed != task)
        {
            throw new TimeoutException("Rule evaluation exceeded the allotted time budget.");
        }

        return await task; // rethrows PrologException from inside `work`, if any
    }
}
```

Notes on that snippet:

- `RunWithTimeoutAsync` is a *host-side* abandon — the background `Task.Run` is not actually
  cancelled (DotProlog gives no cooperative cancellation hook), so a truly runaway goal keeps a
  thread pool thread busy until it terminates on its own. Treat the timeout as "stop waiting for an
  answer," not "stop the engine." If this becomes a real problem, isolating evaluation in its own
  process (killable) is the next escalation.
- `RenderFacts` builds Prolog text from already-schema-validated structured data (the Phase 3 JSON
  schema), not from raw applicant free text — keeps a malicious applicant submission from injecting
  extra clauses/directives into the consulted program.

### Semantic Kernel / `Microsoft.Extensions.AI` structured-output hook (Phase 3)

The LLM step just needs to emit Prolog text and a JSON schema as two string outputs; nothing
DotProlog-specific is required here beyond consulting the result once for validation (shown above in
`PublishRuleSetAsync`). A minimal shape for the ingestion call:

```csharp
public sealed record IngestedRuleSet(string JsonSchema, string PrologSource);

// via IChatClient (Microsoft.Extensions.AI) with structured output:
ChatResponse<IngestedRuleSet> response = await chatClient.GetResponseAsync<IngestedRuleSet>(
    $"""
    Convert the following municipal rulebook into:
    1. a JSON schema for the attributes an application must provide,
    2. valid ISO Prolog facts/rules (no directives, no I/O predicates) implementing the rules.

    Rulebook:
    {rawRuleText}
    """);

await rulesRegistry.PublishRuleSetAsync(response.Result.PrologSource, response.Result.JsonSchema);
```

The prompt constraint ("no directives, no I/O predicates") is doing real work here: DotProlog will
happily execute `:- initialization(...)` directives and stream/file builtins if they show up in
consulted text, and it has no built-in allow-list to fall back on — see the sandboxing caveat above.

## Yes — a typed exchange is possible, not just JSON strings + Prolog text

Everything above talks to Prolog by building goal strings (`engine.Query("eligible(applicant, Reason)")`)
and reading answers back as `PrologValue`. DotProlog has a second, lower-level surface —
`PrologHost` / `PrologPredicate` / `PrologInput` — that skips goal strings entirely: it binds a
predicate by name/arity once and calls it with typed .NET arguments, reading typed outputs back.
This is literally "the surface a generated `.dplproj` facade sits on" (per the source doc-comment),
so it's the same mechanism that gives `.dplproj` consumers a plain method call instead of a string
query — just usable directly, against text consulted at runtime, without needing a build-time
contract.

### Why not just use the `.dplproj` facade generator itself?

That generator (`clr_export/3` + the MSBuild SDK) produces the richest possible experience — a real
typed C# interface, no `PrologValue` at all on the call site — but it runs at **build time** against
a `.pl`/`.dpli` file that ships with the project. Phase 3's rule sets are generated by an LLM per
municipality/version and land in Azure Table Storage at runtime, so there is no fixed source file to
point the SDK generator at without recompiling and redeploying per rule-set version. That's out of
scope here (and would reopen the "running generated code" concern the NativeAOT design deliberately
avoids). `PrologHost` gets most of the same ergonomics at runtime instead.

### `PrologHost` — typed calls without goal strings

```csharp
using DotProlog.Compiler;
using DotProlog.Runtime;

var engine = new PrologEngine(PrologLanguageMode.StrictIso);
engine.ConsultText(rulesText);   // still the LLM-generated rules, consulted as text

var host = new PrologHost(engine.Machine);
PrologPredicate eligible = host.Bind("eligible", 2);   // resolved once, by name/arity — no string goal

// CallOnce: one deterministic answer, outputs come back as PrologValue[] (or null if it failed)
PrologValue[]? result = host.CallOnce(
    eligible,
    PrologInput.Atom("applicant"),
    PrologInput.Output);

// CallAll: every nondeterministic answer, streamed lazily
foreach (PrologValue[] answer in host.CallAll(eligible, PrologInput.Atom("applicant"), PrologInput.Output))
{
    Console.WriteLine(answer[0]);   // the bound Reason for this answer
}
```

`PrologInput` already covers atoms, integers, floats, lists, and compounds, and
`PrologInput.FromValue(PrologValue)` round-trips a value you got back from an earlier call into an
argument for the next one — useful for chaining calls without dropping back to text in between.

### A small typed mapper between `ApplicationFacts`/`EvaluationOutcome` and Prolog terms

`PrologInput`/`PrologValue` are still term-shaped, not your domain records — but wrapping them once
gives every grain a genuinely typed call, instead of hand-building fact text and pattern-matching
`PrologValue` inline each time:

```csharp
public static class PrologFactMapper
{
    // ApplicationFacts -> a single compound term, e.g. applicant(30, 'Bergen', 450000.0)
    public static PrologInput ToTerm(this ApplicationFacts facts) => PrologInput.Compound(
        "applicant",
        facts.Age is { } age ? PrologInput.Integer(age) : PrologInput.Output,
        facts.Municipality is { } m ? PrologInput.Atom(m) : PrologInput.Output,
        facts.Income is { } income ? PrologInput.Float(income) : PrologInput.Output);

    // PrologValue answer -> a typed outcome, no manual switch at each call site
    public static EvaluationOutcome ToOutcome(this PrologValue[] outputs) => outputs switch
    {
        [PrologAtom { Name: "approved" }, PrologCompound reason] =>
            EvaluationOutcome.Approved(reason.Arguments.Select(a => a.ToString() ?? "").ToArray()),
        [PrologAtom { Name: "denied" }, ..] => EvaluationOutcome.Denied(),
        _ => throw new PrologException($"Unexpected evaluation shape: {string.Join(", ", outputs)}"),
    };
}
```

```csharp
// ApplicationEvaluationGrain, using the mapper instead of ad-hoc string/PrologValue handling
var host = new PrologHost(engine.Machine);
PrologPredicate decide = host.Bind("decide", 2);

PrologValue[]? outputs = host.CallOnce(decide, facts.ToTerm(), PrologInput.Output);
EvaluationOutcome outcome = outputs is null
    ? EvaluationOutcome.Denied()
    : PrologFactMapper.ToOutcome(outputs);
```

The rule text itself would then define `decide(applicant(Age, Municipality, Income), approved(Reason))`
clauses (or `decide(_, denied)`) — the LLM ingestion prompt just needs to be told the predicate
contract (name, arity, argument order) it must target, the same way a `.dpli` contract states one for
build-time code.

### What this buys over plain JSON + text, and what it doesn't

- **Buys**: no hand-built goal strings anywhere in grain code (so no string-interpolation-into-Prolog
  injection surface for *facts*), a resolved-once predicate handle instead of re-parsing a goal every
  call, and one central mapping layer per predicate contract instead of scattered `PrologValue`
  pattern-matching.
- **Doesn't buy**: compile-time type checking of the contract itself — `host.Bind("decide", 2)` still
  fails at runtime (a `PrologException`) if the LLM-generated rule set doesn't define that predicate,
  same as a wrong string goal would. The safety net is `RulesRegistryGrain`'s ingestion-time
  `ConsultText` probe (Phase 3) plus asserting the expected predicate exists (`host.Bind` throwing
  when `unknown` is `error`) — worth doing at publish time as well as at evaluation time, so a rule
  set missing the expected contract predicate is rejected before it ever becomes "active," not
  discovered mid-evaluation.
