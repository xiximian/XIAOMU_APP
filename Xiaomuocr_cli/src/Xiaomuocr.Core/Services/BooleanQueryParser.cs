namespace Xiaomuocr.Core.Services;

/// <summary>
/// 检索式解析：支持 "关键词" AND/OR/NOT、中文与/或/非、() / （）。
/// 优先级：NOT &gt; AND &gt; OR；A NOT B 视为 A AND NOT B。
/// </summary>
public static class BooleanQueryParser
{
    private enum TokKind
    {
        Term,
        And,
        Or,
        Not,
        LParen,
        RParen,
        End,
    }

    private readonly struct Tok
    {
        public TokKind Kind { get; init; }
        public string Text { get; init; }
    }

    public static bool TryParse(string? expression, out SearchNode? root, out string? error)
    {
        root = null;
        error = null;
        if (string.IsNullOrWhiteSpace(expression))
        {
            error = "请输入检索式";
            return false;
        }

        List<Tok> tokens;
        try
        {
            tokens = Tokenize(expression);
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }

        if (tokens.Count == 0 || (tokens.Count == 1 && tokens[0].Kind == TokKind.End))
        {
            error = "请输入检索式";
            return false;
        }

        var i = 0;
        try
        {
            root = ParseOr(tokens, ref i);
            if (tokens[i].Kind != TokKind.End)
            {
                error = $"无法解析：位置附近「{tokens[i].Text}」";
                root = null;
                return false;
            }
            return true;
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            root = null;
            return false;
        }
    }

    private static SearchNode ParseOr(List<Tok> tokens, ref int i)
    {
        var left = ParseAnd(tokens, ref i);
        while (tokens[i].Kind == TokKind.Or)
        {
            i++;
            var right = ParseAnd(tokens, ref i);
            left = new SearchOrNode(left, right);
        }
        return left;
    }

    private static SearchNode ParseAnd(List<Tok> tokens, ref int i)
    {
        var left = ParseNot(tokens, ref i);
        while (true)
        {
            var k = tokens[i].Kind;
            // 显式 AND，或 A NOT B → A AND NOT B
            if (k == TokKind.And)
            {
                i++;
                var right = ParseNot(tokens, ref i);
                left = new SearchAndNode(left, right);
                continue;
            }
            if (k == TokKind.Not)
            {
                i++;
                var right = ParseNot(tokens, ref i);
                left = new SearchAndNode(left, new SearchNotNode(right));
                continue;
            }
            // 两个 term/paren 相邻视为 AND
            if (k is TokKind.Term or TokKind.LParen)
            {
                var right = ParseNot(tokens, ref i);
                left = new SearchAndNode(left, right);
                continue;
            }
            break;
        }
        return left;
    }

    private static SearchNode ParseNot(List<Tok> tokens, ref int i)
    {
        if (tokens[i].Kind == TokKind.Not)
        {
            i++;
            return new SearchNotNode(ParseNot(tokens, ref i));
        }
        return ParsePrimary(tokens, ref i);
    }

    private static SearchNode ParsePrimary(List<Tok> tokens, ref int i)
    {
        var t = tokens[i];
        if (t.Kind == TokKind.Term)
        {
            i++;
            if (string.IsNullOrWhiteSpace(t.Text))
                throw new FormatException("空关键词");
            return new SearchTermNode(t.Text.Trim());
        }
        if (t.Kind == TokKind.LParen)
        {
            i++;
            var inner = ParseOr(tokens, ref i);
            if (tokens[i].Kind != TokKind.RParen)
                throw new FormatException("缺少右括号 ) 或 ）");
            i++;
            return inner;
        }
        throw new FormatException($"期望关键词或括号，遇到「{t.Text}」");
    }

    private static List<Tok> Tokenize(string input)
    {
        var list = new List<Tok>();
        var s = input;
        var n = s.Length;
        var i = 0;

        while (i < n)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c is '(' or '（')
            {
                list.Add(new Tok { Kind = TokKind.LParen, Text = c.ToString() });
                i++;
                continue;
            }
            if (c is ')' or '）')
            {
                list.Add(new Tok { Kind = TokKind.RParen, Text = c.ToString() });
                i++;
                continue;
            }

            if (c is '"' or '\'' or '“' or '”' or '‘' or '’')
            {
                i++;
                var start = i;
                while (i < n && s[i] is not ('"' or '\'' or '“' or '”' or '‘' or '’'))
                    i++;
                if (i >= n)
                    throw new FormatException("引号未闭合");
                var term = s[start..i];
                i++; // skip closing quote
                list.Add(new Tok { Kind = TokKind.Term, Text = term });
                continue;
            }

            // 运算符或裸词
            var wordStart = i;
            while (i < n && !char.IsWhiteSpace(s[i]) && s[i] is not ('(' or ')' or '（' or '）' or '"' or '\'' or '“' or '”' or '‘' or '’'))
                i++;
            var word = s[wordStart..i];
            var upper = word.ToUpperInvariant();
            if (upper is "AND" or "与" or "且")
                list.Add(new Tok { Kind = TokKind.And, Text = word });
            else if (upper is "OR" or "或")
                list.Add(new Tok { Kind = TokKind.Or, Text = word });
            else if (upper is "NOT" or "非" or "不")
                list.Add(new Tok { Kind = TokKind.Not, Text = word });
            else
                list.Add(new Tok { Kind = TokKind.Term, Text = word });
        }

        list.Add(new Tok { Kind = TokKind.End, Text = "" });
        return list;
    }
}
