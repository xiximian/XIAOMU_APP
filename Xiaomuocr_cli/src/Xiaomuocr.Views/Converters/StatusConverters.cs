using Avalonia;
using Avalonia.Controls.Documents;
using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Xiaomuocr.Views.Converters;

public class StatusToColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var status = value as string;
        return status switch
        {
            "completed" => new SolidColorBrush(Color.Parse("#2D5A3D")),
            "processing" => new SolidColorBrush(Color.Parse("#9B722E")),
            "failed" => new SolidColorBrush(Color.Parse("#B8443C")),
            _ => new SolidColorBrush(Color.Parse("#5C5344")), // pending or unknown
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class StatusToBgConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var status = value as string;
        return status switch
        {
            "completed" => new SolidColorBrush(Color.Parse("#D4E5D9")),
            "processing" => new SolidColorBrush(Color.Parse("#F2E6C8")),
            "failed" => new SolidColorBrush(Color.Parse("#F5E0DC")),
            _ => new SolidColorBrush(Color.Parse("#D4E5D9")),
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class BoolToHighlightConverter : IValueConverter
{
    private static readonly SolidColorBrush HighlightBrush = new(Color.Parse("#CCFFCC"));
    private static readonly SolidColorBrush TransparentBrush = new(Color.FromArgb(0, 0, 0, 0));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b && b) return HighlightBrush;
        return TransparentBrush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>页码导航：已识别页浅绿背景，未识别页透明</summary>
public class OcrPageNavBgConverter : IValueConverter
{
    private static readonly SolidColorBrush OcrDoneBrush = new(Color.Parse("#D4E5D9"));
    private static readonly SolidColorBrush DefaultBrush = new(Color.FromArgb(0, 0, 0, 0));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? OcrDoneBrush : DefaultBrush;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class BoolToBorderThicknessConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? new Thickness(2) : new Thickness(0);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>将 hex 颜色字符串（如 "#FFEB3B"）转为 SolidColorBrush。null/空返回透明。</summary>
public class StringToHighlightBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush TransparentBrush = new(Color.FromArgb(0, 0, 0, 0));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrEmpty(hex))
        {
            try { return new SolidColorBrush(Color.Parse(hex)); }
            catch { }
        }
        return TransparentBrush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// 将 IReadOnlyList&lt;HighlightSegment&gt; 转为 TextBlock 的 InlineCollection。
/// 每个 HighlightSegment 生成一个 Run，带指定的背景色。
/// </summary>
public class HighlightSegmentsToInlinesConverter : IValueConverter
{
    private static readonly SolidColorBrush HighlightBg = new(Color.Parse("#FFEB3B"));
    private static readonly SolidColorBrush TransparentBg = new(Colors.Transparent);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not IReadOnlyList<Core.Models.HighlightSegment> segments || segments.Count == 0)
            return null; // 返回 null → TextBlock 使用 Text 属性回退

        var inlines = new InlineCollection();
        foreach (var seg in segments)
        {
            var brush = TransparentBg;
            if (!string.IsNullOrEmpty(seg.BgHex))
            {
                try { brush = new SolidColorBrush(Color.Parse(seg.BgHex)); }
                catch { }
            }
            inlines.Add(new Run(seg.Text) { Background = brush });
        }
        return inlines;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
