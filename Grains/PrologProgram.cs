using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Grains;

// These types serve two purposes: they're the deserialization target for the LLM's
// structured JSON output (JsonPropertyName attributes), and they're persisted verbatim
// as Orleans grain state (GenerateSerializer/Id attributes).
[GenerateSerializer]
[Alias("Grains.PrologProgram")]
public class PrologProgram
{
    [Id(0)]
    [JsonPropertyName("clauses")]
    public List<Clause> Clauses { get; set; } = [];

    // Declares the JSON value type of every leaf fact predicate referenced in the rule
    // bodies (e.g. "has_valid_email" -> "boolean", "word_count" -> "number"). The LLM fills
    // this in at ingestion time, when it actually knows how each predicate is used (compared
    // against true/false, compared arithmetically, matched as a list, etc.) - that's the one
    // point in the pipeline with enough context to know a predicate is boolean rather than a
    // free-text string. Downstream fact extraction builds its JSON schema directly from this
    // instead of guessing the type back from the predicate's name.
    [Id(1)]
    [JsonPropertyName("factPredicates")]
    public List<FactPredicate> FactPredicates { get; set; } = [];
}

[GenerateSerializer]
[Alias("Grains.FactPredicate")]
public class FactPredicate
{
    [Id(0)]
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [Id(1)]
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty; // "boolean", "number", "string", or "array"
}

[GenerateSerializer]
[Alias("Grains.Clause")]
public class Clause
{
    [Id(0)]
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty; // "fact" or "rule"

    [Id(1)]
    [JsonPropertyName("name")]
    public string? Name { get; set; } // Human-readable name for the rule, e.g. "Minimum experience"

    [Id(2)]
    [JsonPropertyName("references")]
    public List<string> References { get; set; } = []; // Source sections/paragraphs the LLM based this clause on

    [Id(3)]
    [JsonPropertyName("head")]
    public Term Head { get; set; } = new();

    [Id(4)]
    [JsonPropertyName("body")]
    public List<Term> Body { get; set; } = [];
}

[GenerateSerializer]
[Alias("Grains.Term")]
public class Term
{
    [Id(0)]
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty; // "atom", "number", "variable", "compound"

    // Stored as text (even for numbers) so the type stays a plain string that both
    // System.Text.Json and Orleans's generated serializer know how to handle - an
    // object-typed property deserializes to a boxed JsonElement, which Orleans has
    // no copier for.
    [Id(1)]
    [JsonPropertyName("value")]
    [JsonConverter(typeof(TermValueConverter))]
    public string? Value { get; set; } // string or number literal for atoms/numbers

    [Id(2)]
    [JsonPropertyName("name")]
    public string? Name { get; set; } // Used if kind is "variable" (e.g., "X")

    [Id(3)]
    [JsonPropertyName("functor")]
    public string? Functor { get; set; } // Used if kind is "compound" (e.g., "parent")

    [Id(4)]
    [JsonPropertyName("args")]
    public List<Term> Args { get; set; } = [];
}

// The JSON schema declares "value" as ["string", "number", "null"]. Reading it as a plain
// string keeps the CLR type simple (see Term.Value), so numbers are converted to their
// invariant-culture text form on the way in.
public class TermValueConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt64(out var i)
                ? i.ToString(CultureInfo.InvariantCulture)
                : reader.GetDouble().ToString(CultureInfo.InvariantCulture),
            _ => throw new JsonException($"Unexpected token {reader.TokenType} for Term.Value")
        };
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStringValue(value);
        }
    }
}
