using System.Text;
using System.Text.RegularExpressions;

namespace Grains;

public static class PrologTranslator
{
    // ISO unquoted atoms are either: a lowercase letter followed by alphanumerics/underscores,
    // or purely symbolic characters (e.g. ":-", "-->"), or one of a small set of solo atoms.
    // Anything else (spaces, uppercase-leading text, punctuation like "&", digits-first, etc.)
    // must be single-quoted or it breaks the whole consult - see the "Microsoft Word" /
    // "internet browsing" incident where an unquoted multi-word atom aborted an entire
    // ConsultText call and silently undefined every rule predicate in that text.
    private static readonly Regex UnquotedAlnumAtom = new(@"^[a-z][a-zA-Z0-9_]*$", RegexOptions.Compiled);
    private static readonly Regex UnquotedSymbolicAtom = new(@"^[+\-*/\\^<>=~:.?@#&$]+$", RegexOptions.Compiled);
    private static readonly HashSet<string> UnquotedSoloAtoms = new() { "[]", "{}", "!", ";", "," };

    public static string ToPrologCode(this PrologProgram program)
    {
        var sb = new StringBuilder();

        foreach (var clause in program.Clauses)
        {
            sb.AppendLine(FormatClause(clause));
        }

        return sb.ToString();
    }

    public static string ToPrologCode(this Clause clause) => FormatClause(clause);

    private static string FormatClause(Clause clause)
    {
        var headStr = FormatTerm(clause.Head);

        if (clause.Type == "fact" || clause.Body.Count == 0)
        {
            return $"{headStr}.";
        }

        var bodyStrs = string.Join(", ", clause.Body.Select(FormatTerm));
        return $"{headStr} :- {bodyStrs}.";
    }

    private static string FormatTerm(Term term)
    {
        return term.Kind switch
        {
            "atom" => FormatAtom(term.Value ?? string.Empty),
            "number" => term.Value ?? "0",
            "variable" => term.Name ?? "_",
            "compound" => FormatCompound(term),
            _ => throw new NotSupportedException($"Unknown term kind: {term.Kind}")
        };
    }

    private static string FormatAtom(string value)
    {
        if (value.Length == 0)
        {
            return "''";
        }

        if (UnquotedSoloAtoms.Contains(value) || UnquotedAlnumAtom.IsMatch(value) || UnquotedSymbolicAtom.IsMatch(value))
        {
            return value;
        }

        return $"'{EscapeQuotedAtom(value)}'";
    }

    private static string EscapeQuotedAtom(string value) =>
        value.Replace("\\", "\\\\").Replace("'", "\\'");

    private static string FormatCompound(Term term)
    {
        // A functor is itself an atom, so it's subject to the same quoting rules
        // (guards against LLM-emitted functors like "has degree" or "Eligible").
        var functor = FormatAtom(term.Functor ?? string.Empty);

        if (term.Args.Count == 0)
        {
            return functor;
        }

        var argsStr = string.Join(", ", term.Args.Select(FormatTerm));
        return $"{functor}({argsStr})";
    }
}