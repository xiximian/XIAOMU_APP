using System.Text.Json;
using Xiaomuocr.Core.Models;

namespace Xiaomuocr.Core.Services;

public class HighlightService : IHighlightService
{
    public async Task<Dictionary<int, List<HighlightEntry>>> LoadAsync(string jsonDir)
    {
        var result = new Dictionary<int, List<HighlightEntry>>();
        if (string.IsNullOrEmpty(jsonDir)) return result;

        var path = GetHighlightsPath(jsonDir);
        if (!File.Exists(path)) return result;

        try
        {
            var json = await File.ReadAllTextAsync(path);
            var raw = JsonSerializer.Deserialize<Dictionary<string, List<HighlightEntry>>>(json);
            if (raw != null)
            {
                foreach (var kv in raw)
                {
                    if (int.TryParse(kv.Key, out int pageIdx))
                        result[pageIdx] = kv.Value;
                }
            }
        }
        catch { /* 文件损坏时返回空 */ }

        return result;
    }

    public async Task SaveAsync(string jsonDir, Dictionary<int, List<HighlightEntry>> data)
    {
        if (string.IsNullOrEmpty(jsonDir)) return;

        // 转为字符串键写入 JSON
        var dict = data.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);
        var path = GetHighlightsPath(jsonDir);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(dict, options);
        await File.WriteAllTextAsync(path, json);
    }

    public void AddHighlight(Dictionary<int, List<HighlightEntry>> data, int pageIndex, string text, string color = "#FFEB3B")
    {
        if (!data.ContainsKey(pageIndex))
            data[pageIndex] = new List<HighlightEntry>();

        if (!data[pageIndex].Any(h => h.Text == text))
        {
            data[pageIndex].Add(new HighlightEntry { Text = text, Color = color });
        }
    }

    public void RemovePageHighlights(Dictionary<int, List<HighlightEntry>> data, int pageIndex)
    {
        if (data.ContainsKey(pageIndex))
            data.Remove(pageIndex);
    }

    private static string GetHighlightsPath(string jsonDir)
        => Path.Combine(jsonDir, "highlights.json");
}
