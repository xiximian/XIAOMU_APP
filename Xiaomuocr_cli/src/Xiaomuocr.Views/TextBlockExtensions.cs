using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Xiaomuocr.Core.Models;
using Xiaomuocr.Core.Services;
using Xiaomuocr.Core.ViewModels;

namespace Xiaomuocr.Views;

/// <summary>
/// 附加属性：让 TextBlock 支持通过 XAML 绑定直接设置 Inlines（逐字高亮）。
/// 同时监听 EditorText，确保「繁→简 / 句读」预览与高亮互不破坏。
/// 仅作用于显式绑定了 BindableSegments 的 TextBlock，避免误改同模板内的序号标签等。
/// </summary>
public static class TextBlockExtensions
{
    public static readonly AttachedProperty<IReadOnlyList<HighlightSegment>?> BindableSegmentsProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, IReadOnlyList<HighlightSegment>?>(
            "BindableSegments", typeof(TextBlockExtensions));

    private static readonly AttachedProperty<OcrTextBlockViewModel?> HookedVmProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, OcrTextBlockViewModel?>(
            "HookedVm", typeof(TextBlockExtensions));

    private static readonly AttachedProperty<PropertyChangedEventHandler?> HookHandlerProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, PropertyChangedEventHandler?>(
            "HookHandler", typeof(TextBlockExtensions));

    public static void SetBindableSegments(AvaloniaObject element, IReadOnlyList<HighlightSegment>? value)
        => element.SetValue(BindableSegmentsProperty, value);

    public static IReadOnlyList<HighlightSegment>? GetBindableSegments(AvaloniaObject element)
        => element.GetValue(BindableSegmentsProperty);

    static TextBlockExtensions()
    {
        BindableSegmentsProperty.Changed.AddClassHandler<TextBlock>(OnSegmentsChanged);
        TextBlock.DataContextProperty.Changed.AddClassHandler<TextBlock>(OnDataContextChanged);
    }

    /// <summary>仅处理显式绑定了 BindableSegments 的查看态 TextBlock。</summary>
    private static bool IsSegmentsBound(TextBlock textBlock)
        => textBlock.IsSet(BindableSegmentsProperty);

    private static void OnSegmentsChanged(TextBlock textBlock, AvaloniaPropertyChangedEventArgs e)
    {
        EnsureVmHook(textBlock);
        ApplyDisplay(textBlock);
    }

    private static void OnDataContextChanged(TextBlock textBlock, AvaloniaPropertyChangedEventArgs e)
    {
        // 同 DataTemplate 内还有绿色序号 TextBlock，绝不能覆盖其 Text
        if (!IsSegmentsBound(textBlock))
            return;

        EnsureVmHook(textBlock);
        ApplyDisplay(textBlock);
    }

    private static void EnsureVmHook(TextBlock textBlock)
    {
        var oldVm = textBlock.GetValue(HookedVmProperty);
        var oldHandler = textBlock.GetValue(HookHandlerProperty);
        var newVm = textBlock.DataContext as OcrTextBlockViewModel;

        if (ReferenceEquals(oldVm, newVm))
            return;

        if (oldVm != null && oldHandler != null)
            oldVm.PropertyChanged -= oldHandler;

        PropertyChangedEventHandler? newHandler = null;
        if (newVm != null)
        {
            newHandler = (_, args) =>
            {
                if (args.PropertyName is nameof(OcrTextBlockViewModel.EditorText)
                    or nameof(OcrTextBlockViewModel.HighlightedSegments)
                    or nameof(OcrTextBlockViewModel.Text))
                {
                    ApplyDisplay(textBlock);
                }
            };
            newVm.PropertyChanged += newHandler;
        }

        textBlock.SetValue(HookedVmProperty, newVm);
        textBlock.SetValue(HookHandlerProperty, newHandler);
    }

    private static void ApplyDisplay(TextBlock textBlock)
    {
        if (!IsSegmentsBound(textBlock))
            return;

        var segments = GetBindableSegments(textBlock);
        var vm = textBlock.DataContext as OcrTextBlockViewModel;

        textBlock.Inlines?.Clear();
        textBlock.Text = null;

        var editor = vm?.EditorText ?? "";
        if (segments == null || segments.Count == 0)
        {
            textBlock.Text = editor;
            return;
        }

        var canonJoined = string.Concat(segments.Select(s => s.Text ?? ""));
        if (string.IsNullOrEmpty(canonJoined))
        {
            textBlock.Text = editor;
            return;
        }

        // 规范字 → 颜色（高亮始终基于 OCR 原文分段，不受句读影响）
        var colors = new string?[canonJoined.Length];
        int ci = 0;
        foreach (var seg in segments)
        {
            var t = seg.Text ?? "";
            for (int k = 0; k < t.Length; k++)
            {
                if (ci < colors.Length)
                    colors[ci++] = seg.BgHex;
            }
        }

        // 无预览差异：按原分段画
        if (string.IsNullOrEmpty(editor) || string.Equals(editor, canonJoined, System.StringComparison.Ordinal))
        {
            textBlock.Inlines ??= new InlineCollection();
            foreach (var seg in segments)
            {
                IBrush? brush = null;
                if (!string.IsNullOrEmpty(seg.BgHex))
                {
                    try { brush = Brush.Parse(seg.BgHex); }
                    catch { /* ignore */ }
                }
                textBlock.Inlines.Add(new Run(seg.Text ?? "") { Background = brush });
            }
            return;
        }

        // 等长预览（典型繁→简）：按下标映射
        if (editor.Length == canonJoined.Length)
        {
            textBlock.Inlines ??= new InlineCollection();
            int cursor = 0;
            foreach (var seg in segments)
            {
                var len = (seg.Text ?? "").Length;
                var runText = cursor + len <= editor.Length
                    ? editor.Substring(cursor, len)
                    : editor[cursor..];
                cursor += len;
                IBrush? brush = null;
                if (!string.IsNullOrEmpty(seg.BgHex))
                {
                    try { brush = Brush.Parse(seg.BgHex); }
                    catch { /* ignore */ }
                }
                textBlock.Inlines.Add(new Run(runText) { Background = brush });
            }
            return;
        }

        // 句读等变长预览：对齐后保留高亮底色 + 显示带标点文本
        var runs = PreviewTextAlign.BuildHighlightedPreviewRuns(canonJoined, colors, editor);
        textBlock.Inlines ??= new InlineCollection();
        foreach (var (text, bg) in runs)
        {
            IBrush? brush = null;
            if (!string.IsNullOrEmpty(bg))
            {
                try { brush = Brush.Parse(bg); }
                catch { /* ignore */ }
            }
            textBlock.Inlines.Add(new Run(text) { Background = brush });
        }
    }
}
