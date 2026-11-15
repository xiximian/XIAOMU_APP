namespace Xiaomuocr.Core.Services;

/// <summary>高级检索行之间的布尔关系（相对上一行）。</summary>
public enum SearchBoolOp
{
    And = 0,
    Or = 1,
    Not = 2,
}

/// <summary>高级检索单行条件。</summary>
public class SearchClause
{
    /// <summary>与上一子句的关系；首行忽略。</summary>
    public SearchBoolOp Op { get; set; } = SearchBoolOp.And;

    public string Keyword { get; set; } = "";
}

/// <summary>高级检索：多子句左结合查询。</summary>
public class SearchQuery
{
    public List<SearchClause> Clauses { get; set; } = new();

    public string Describe()
    {
        if (Clauses.Count == 0) return "";
        var parts = new List<string>();
        for (var i = 0; i < Clauses.Count; i++)
        {
            var c = Clauses[i];
            var kw = string.IsNullOrWhiteSpace(c.Keyword) ? "?" : c.Keyword.Trim();
            if (i == 0)
                parts.Add($"「{kw}」");
            else
            {
                var op = c.Op switch
                {
                    SearchBoolOp.Or => "OR",
                    SearchBoolOp.Not => "NOT",
                    _ => "AND",
                };
                parts.Add($"{op} 「{kw}」");
            }
        }
        return string.Join(" ", parts);
    }

    /// <summary>转为 AST（左结合；NOT 行视为 AND NOT term）。</summary>
    public SearchNode ToNode()
    {
        SearchNode? node = null;
        foreach (var c in Clauses)
        {
            var kw = (c.Keyword ?? "").Trim();
            if (string.IsNullOrEmpty(kw)) continue;
            var term = new SearchTermNode(kw);
            if (node == null)
            {
                node = term;
                continue;
            }
            node = c.Op switch
            {
                SearchBoolOp.Or => new SearchOrNode(node, term),
                SearchBoolOp.Not => new SearchAndNode(node, new SearchNotNode(term)),
                _ => new SearchAndNode(node, term),
            };
        }
        return node ?? new SearchTermNode("");
    }
}

/// <summary>布尔检索 AST。</summary>
public abstract class SearchNode
{
    public abstract bool Evaluate(Func<string, bool> termMatches);
    public abstract IEnumerable<string> PositiveKeywords();
}

public sealed class SearchTermNode : SearchNode
{
    public string Keyword { get; }
    public SearchTermNode(string keyword) => Keyword = keyword ?? "";

    public override bool Evaluate(Func<string, bool> termMatches)
        => !string.IsNullOrEmpty(Keyword) && termMatches(Keyword);

    public override IEnumerable<string> PositiveKeywords()
    {
        if (!string.IsNullOrEmpty(Keyword))
            yield return Keyword;
    }
}

public sealed class SearchNotNode : SearchNode
{
    public SearchNode Child { get; }
    public SearchNotNode(SearchNode child) => Child = child;

    public override bool Evaluate(Func<string, bool> termMatches)
        => !Child.Evaluate(termMatches);

    public override IEnumerable<string> PositiveKeywords()
        => Enumerable.Empty<string>();
}

public sealed class SearchAndNode : SearchNode
{
    public SearchNode Left { get; }
    public SearchNode Right { get; }
    public SearchAndNode(SearchNode left, SearchNode right)
    {
        Left = left;
        Right = right;
    }

    public override bool Evaluate(Func<string, bool> termMatches)
        => Left.Evaluate(termMatches) && Right.Evaluate(termMatches);

    public override IEnumerable<string> PositiveKeywords()
        => Left.PositiveKeywords().Concat(Right.PositiveKeywords());
}

public sealed class SearchOrNode : SearchNode
{
    public SearchNode Left { get; }
    public SearchNode Right { get; }
    public SearchOrNode(SearchNode left, SearchNode right)
    {
        Left = left;
        Right = right;
    }

    public override bool Evaluate(Func<string, bool> termMatches)
        => Left.Evaluate(termMatches) || Right.Evaluate(termMatches);

    public override IEnumerable<string> PositiveKeywords()
        => Left.PositiveKeywords().Concat(Right.PositiveKeywords());
}
