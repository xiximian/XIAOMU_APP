using System.Text.Json;
using Xiaomuocr.Core.Services;

namespace Xiaomuocr.Core.Models;

/// <summary>
/// PaddleOCR-VL 1.5 / 1.6 optionalPayload 参数。
/// 键名与官方 API 一致（camelCase）。
/// </summary>
public static class OcrVlOptions
{
    public const string SettingsKey = "ocr_vl_options";
    public const string PresetKey = "ocr_vl_preset"; // default | general | rubbing | custom

    public const string PresetDefault = "default";
    public const string PresetGeneral = "general";
    public const string PresetRubbing = "rubbing";
    public const string PresetCustom = "custom";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = null,
    };

    /// <summary>完整默认参数（方向分类默认 False）</summary>
    public static Dictionary<string, object> CreateDefault() => new()
    {
        ["useDocOrientationClassify"] = false,
        ["useDocUnwarping"] = false,
        ["useLayoutDetection"] = true,
        ["useChartRecognition"] = false,
        ["useSealRecognition"] = true,
        ["useOcrForImageBlock"] = false,
        ["mergeTables"] = true,
        ["relevelTitles"] = true,
        ["layoutShapeMode"] = "auto",
        ["promptLabel"] = "ocr",
        ["repetitionPenalty"] = 1,
        ["temperature"] = 0,
        ["topP"] = 1,
        ["minPixels"] = 147384,
        ["maxPixels"] = 2822400,
        ["layoutNms"] = true,
        ["restructurePages"] = true,
    };

    /// <summary>一键推荐：一般文献</summary>
    public static Dictionary<string, object> CreateGeneral() => new()
    {
        ["useDocOrientationClassify"] = false,
        ["useDocUnwarping"] = false,
        ["useLayoutDetection"] = true,
        ["useChartRecognition"] = false,
        ["useSealRecognition"] = false,
        ["useOcrForImageBlock"] = false,
        ["mergeTables"] = false,
        ["relevelTitles"] = true,
        ["layoutShapeMode"] = "auto",
        ["promptLabel"] = "ocr",
        ["layoutNms"] = true,
        ["restructurePages"] = true,
    };

    /// <summary>一键推荐：拓片</summary>
    public static Dictionary<string, object> CreateRubbing() => new()
    {
        ["useDocOrientationClassify"] = false,
        ["useDocUnwarping"] = false,
        ["useLayoutDetection"] = false,
        ["useChartRecognition"] = false,
        ["useSealRecognition"] = false,
        ["useOcrForImageBlock"] = false,
        ["mergeTables"] = false,
        ["relevelTitles"] = false,
        ["layoutShapeMode"] = "auto",
        ["promptLabel"] = "spotting",
        ["layoutNms"] = true,
    };

    public static Dictionary<string, object> FromPreset(string preset) => preset switch
    {
        PresetGeneral => CreateGeneral(),
        PresetRubbing => CreateRubbing(),
        _ => CreateDefault(),
    };

    public static string PresetDisplayName(string preset) => preset switch
    {
        PresetGeneral => "一般",
        PresetRubbing => "拓片",
        PresetCustom => "自定义",
        _ => "默认",
    };

    public static async Task<(string Preset, Dictionary<string, object> Options)> LoadAsync(ISettingsService settings)
    {
        var preset = await settings.GetAsync(PresetKey) ?? PresetDefault;
        var json = await settings.GetAsync(SettingsKey);
        if (string.IsNullOrWhiteSpace(json))
            return (preset == PresetCustom ? PresetDefault : preset, FromPreset(preset == PresetCustom ? PresetDefault : preset));

        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, JsonOpts);
            if (raw == null || raw.Count == 0)
                return (PresetDefault, CreateDefault());
            return (preset, Sanitize(ToObjectDict(raw)));
        }
        catch
        {
            return (PresetDefault, CreateDefault());
        }
    }

    public static async Task SaveAsync(ISettingsService settings, string preset, Dictionary<string, object> options)
    {
        await settings.SetAsync(PresetKey, preset);
        await settings.SetAsync(SettingsKey, JsonSerializer.Serialize(Sanitize(options), JsonOpts));
    }

    /// <summary>
    /// 方向分类固定为 false：模力/当前链路不支持可靠 true；
    /// 开启后 bbox 会落在摆正坐标系，客户端 Undo 易与显示错位。
    /// </summary>
    public static Dictionary<string, object> Sanitize(Dictionary<string, object> options)
    {
        var d = new Dictionary<string, object>(options, StringComparer.Ordinal);
        d["useDocOrientationClassify"] = false;
        return d;
    }

    public static Dictionary<string, object> ToObjectDict(Dictionary<string, JsonElement> raw)
    {
        var d = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (k, v) in raw)
            d[k] = JsonElementToObject(v);
        return d;
    }

    private static object JsonElementToObject(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => e.GetString() ?? "",
        JsonValueKind.Number => e.TryGetInt64(out var i) && Math.Abs(e.GetDouble() - i) < 1e-9
            ? i
            : e.GetDouble(),
        JsonValueKind.Null => null!,
        _ => e.GetRawText(),
    };

    public static bool GetBool(Dictionary<string, object> opts, string key, bool fallback = false)
        => opts.TryGetValue(key, out var v) ? Convert.ToBoolean(v) : fallback;

    public static string GetString(Dictionary<string, object> opts, string key, string fallback = "")
        => opts.TryGetValue(key, out var v) && v != null ? Convert.ToString(v) ?? fallback : fallback;

    public static double GetDouble(Dictionary<string, object> opts, string key, double fallback = 0)
        => opts.TryGetValue(key, out var v) && v != null ? Convert.ToDouble(v) : fallback;

    public static long GetLong(Dictionary<string, object> opts, string key, long fallback = 0)
        => opts.TryGetValue(key, out var v) && v != null ? Convert.ToInt64(v) : fallback;
}
