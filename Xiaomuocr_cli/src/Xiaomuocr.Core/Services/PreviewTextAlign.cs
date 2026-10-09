using System.Text;

namespace Xiaomuocr.Core.Services;

/// <summary>
/// 将预览文本（繁简 / 句读）与规范 OCR 文本对齐，供高亮互不干扰。
/// </summary>
public static class PreviewTextAlign
{
    /// <summary>
    /// 对 editor 每个字符给出对应的 canon 下标；插入的标点继承邻近字的下标。
    /// </summary>
    public static int[] MapEditorToCanon(string canon, string editor)
    {
        var map = new int[editor.Length];
        for (int k = 0; k < map.Length; k++)
            map[k] = -1;

        int i = 0;
        int j = 0;
        while (j < editor.Length)
        {
            if (i < canon.Length && CharsMatch(canon[i], editor[j]))
            {
                map[j] = i;
                i++;
                j++;
                continue;
            }

            // 预览中多出的标点：挂到「即将匹配的字」或前一个字
            if (IsInsertedPreviewChar(editor[j]))
            {
                map[j] = i < canon.Length ? i : (i > 0 ? i - 1 : -1);
                j++;
                continue;
            }

            // 规范侧多余标点（少见）：跳过
            if (i < canon.Length && IsInsertedPreviewChar(canon[i]))
            {
                i++;
                continue;
            }

            // 无法对齐：跳过 editor 字符，尽量不丢后续
            map[j] = i < canon.Length ? i : (i > 0 ? i - 1 : -1);
            j++;
        }

        return map;
    }

    /// <summary>
    /// 将 editor 中的选中子串映射回 canon 子串（去掉句读插入的标点）。
    /// </summary>
    public static string? MapEditorSelectionToCanon(string canon, string editor, string selection)
    {
        if (string.IsNullOrEmpty(canon) || string.IsNullOrEmpty(editor) || string.IsNullOrEmpty(selection))
            return null;

        var idx = editor.IndexOf(selection, StringComparison.Ordinal);
        if (idx < 0)
            return null;

        var map = MapEditorToCanon(canon, editor);
        int start = -1, end = -1;
        for (int j = idx; j < idx + selection.Length && j < editor.Length; j++)
        {
            var ci = map[j];
            if (ci < 0 || ci >= canon.Length)
                continue;
            // 仅计入与规范字匹配的位置（跳过纯插入标点）
            if (!CharsMatch(canon[ci], editor[j]))
                continue;
            if (start < 0) start = ci;
            end = ci;
        }

        if (start < 0 || end < start)
            return null;
        return canon.Substring(start, end - start + 1);
    }

    /// <summary>
    /// 按 canon 上的颜色表，把 editor 拆成 (text, bgHex) 连续片段。
    /// </summary>
    public static List<(string Text, string? BgHex)> BuildHighlightedPreviewRuns(
        string canon,
        string?[] canonColors,
        string editor)
    {
        var runs = new List<(string Text, string? BgHex)>();
        if (string.IsNullOrEmpty(editor))
            return runs;

        var map = MapEditorToCanon(canon, editor);
        var sb = new StringBuilder();
        string? curColor = null;

        void Flush()
        {
            if (sb.Length == 0) return;
            runs.Add((sb.ToString(), curColor));
            sb.Clear();
        }

        for (int j = 0; j < editor.Length; j++)
        {
            string? col = null;
            var ci = map[j];
            if (ci >= 0 && ci < canonColors.Length)
                col = canonColors[ci];

            if (sb.Length > 0 && !ColorsEqual(col, curColor))
                Flush();

            if (sb.Length == 0)
                curColor = col;
            sb.Append(editor[j]);
        }

        Flush();
        return runs;
    }

    public static bool CharsMatch(char canon, char editor)
    {
        if (canon == editor)
            return true;
        // 繁→简预览：规范字转简后与 editor 比对
        var simp = ChineseTextConverter.ToSimplified(canon.ToString());
        return simp.Length > 0 && simp[0] == editor;
    }

    public static bool IsInsertedPreviewChar(char c)
    {
        // 句读模型常见标点 + 空白（预览插入，规范 OCR 通常没有）
        return char.IsWhiteSpace(c)
               || "，。：；？！、；「」『』（）【】《》〈〉…—·“”‘’,.!?;:\"'()[]{}".Contains(c);
    }

    private static bool ColorsEqual(string? a, string? b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
