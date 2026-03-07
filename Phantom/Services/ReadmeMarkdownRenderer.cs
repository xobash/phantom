using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Phantom.Services;

public static class ReadmeMarkdownRenderer
{
    private static readonly Regex HeadingRegex = new(@"^\s{0,3}(#{1,6})\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex OrderedListRegex = new(@"^\s*\d+(?:\.|\))\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex UnorderedListRegex = new(@"^\s*[-\*\+]\s+(.*)$", RegexOptions.Compiled);

    private static readonly Brush DocumentForeground = BrushFromHex("#F2F2F2");
    private static readonly Brush MutedForeground = BrushFromHex("#CECECE");
    private static readonly Brush LinkForeground = BrushFromHex("#7EB7FF");
    private static readonly Brush CodeBackground = BrushFromHex("#1A1A1A");
    private static readonly Brush CodeBorder = BrushFromHex("#3D3D3D");

    public static FlowDocument Render(string markdown, Action<Uri> openLink)
    {
        var document = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            Foreground = DocumentForeground,
            Background = Brushes.Transparent,
            PagePadding = new Thickness(0),
            TextAlignment = TextAlignment.Left
        };

        var normalized = markdown.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');

        var paragraphBuffer = new List<string>();
        var listBuffer = new ListBuffer();
        var codeFenceBuffer = new StringBuilder();
        var insideCodeFence = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine ?? string.Empty;

            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph(paragraphBuffer, document, openLink);
                FlushList(listBuffer, document, openLink);

                if (insideCodeFence)
                {
                    document.Blocks.Add(CreateCodeBlock(codeFenceBuffer.ToString()));
                    codeFenceBuffer.Clear();
                    insideCodeFence = false;
                }
                else
                {
                    insideCodeFence = true;
                }

                continue;
            }

            if (insideCodeFence)
            {
                codeFenceBuffer.AppendLine(line);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph(paragraphBuffer, document, openLink);
                FlushList(listBuffer, document, openLink);
                continue;
            }

            var headingMatch = HeadingRegex.Match(line);
            if (headingMatch.Success)
            {
                FlushParagraph(paragraphBuffer, document, openLink);
                FlushList(listBuffer, document, openLink);

                var marks = headingMatch.Groups[1].Value;
                var headingText = headingMatch.Groups[2].Value.Trim();
                document.Blocks.Add(CreateHeading(headingText, marks.Length, openLink));
                continue;
            }

            if (TryParseListItem(line, out var markerStyle, out var listItemText))
            {
                FlushParagraph(paragraphBuffer, document, openLink);
                if (listBuffer.HasItems && listBuffer.Style != markerStyle)
                {
                    FlushList(listBuffer, document, openLink);
                }

                listBuffer.Add(markerStyle, listItemText);
                continue;
            }

            FlushList(listBuffer, document, openLink);
            paragraphBuffer.Add(line.Trim());
        }

        if (insideCodeFence)
        {
            document.Blocks.Add(CreateCodeBlock(codeFenceBuffer.ToString()));
        }

        FlushParagraph(paragraphBuffer, document, openLink);
        FlushList(listBuffer, document, openLink);

        if (document.Blocks.Count == 0)
        {
            document.Blocks.Add(CreateParagraph("README is empty.", openLink, muted: true));
        }

        return document;
    }

    private static bool TryParseListItem(string line, out TextMarkerStyle markerStyle, out string itemText)
    {
        var unordered = UnorderedListRegex.Match(line);
        if (unordered.Success)
        {
            markerStyle = TextMarkerStyle.Disc;
            itemText = unordered.Groups[1].Value.Trim();
            return true;
        }

        var ordered = OrderedListRegex.Match(line);
        if (ordered.Success)
        {
            markerStyle = TextMarkerStyle.Decimal;
            itemText = ordered.Groups[1].Value.Trim();
            return true;
        }

        markerStyle = TextMarkerStyle.None;
        itemText = string.Empty;
        return false;
    }

    private static void FlushParagraph(List<string> lines, FlowDocument document, Action<Uri> openLink)
    {
        if (lines.Count == 0)
        {
            return;
        }

        var text = string.Join(" ", lines).Trim();
        lines.Clear();
        if (text.Length == 0)
        {
            return;
        }

        document.Blocks.Add(CreateParagraph(text, openLink));
    }

    private static void FlushList(ListBuffer buffer, FlowDocument document, Action<Uri> openLink)
    {
        if (!buffer.HasItems)
        {
            return;
        }

        var list = new List
        {
            MarkerStyle = buffer.Style,
            Margin = new Thickness(22, 0, 0, 10),
            Padding = new Thickness(0),
            Foreground = DocumentForeground
        };

        foreach (var item in buffer.Items)
        {
            var paragraph = new Paragraph
            {
                Margin = new Thickness(0, 0, 0, 2),
                Padding = new Thickness(0),
                LineHeight = 20
            };

            foreach (var inline in ParseInlines(item, openLink))
            {
                paragraph.Inlines.Add(inline);
            }

            list.ListItems.Add(new ListItem(paragraph));
        }

        document.Blocks.Add(list);
        buffer.Clear();
    }

    private static Paragraph CreateHeading(string text, int level, Action<Uri> openLink)
    {
        var fontSize = level switch
        {
            1 => 24,
            2 => 21,
            3 => 19,
            4 => 17,
            5 => 16,
            _ => 15
        };

        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 8, 0, 6),
            FontWeight = FontWeights.SemiBold,
            FontSize = fontSize,
            Foreground = DocumentForeground
        };

        foreach (var inline in ParseInlines(text, openLink))
        {
            paragraph.Inlines.Add(inline);
        }

        return paragraph;
    }

    private static Paragraph CreateParagraph(string text, Action<Uri> openLink, bool muted = false)
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 0, 0, 10),
            LineHeight = 20,
            Foreground = muted ? MutedForeground : DocumentForeground
        };

        foreach (var inline in ParseInlines(text, openLink))
        {
            paragraph.Inlines.Add(inline);
        }

        return paragraph;
    }

    private static Block CreateCodeBlock(string code)
    {
        var codeText = code.TrimEnd('\r', '\n');

        var textBox = new TextBox
        {
            Text = codeText,
            IsReadOnly = true,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = DocumentForeground,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            Padding = new Thickness(10)
        };

        var border = new Border
        {
            Background = CodeBackground,
            BorderBrush = CodeBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 2, 0, 12),
            Child = textBox
        };

        return new BlockUIContainer(border)
        {
            Margin = new Thickness(0)
        };
    }

    private static IEnumerable<Inline> ParseInlines(string text, Action<Uri> openLink)
    {
        var inlines = new List<Inline>();
        var cursor = 0;

        while (cursor < text.Length)
        {
            var nextCode = text.IndexOf('`', cursor);
            var nextLink = text.IndexOf('[', cursor);
            var nextToken = NextTokenIndex(nextCode, nextLink);

            if (nextToken < 0)
            {
                inlines.Add(new Run(text[cursor..]));
                break;
            }

            if (nextToken > cursor)
            {
                inlines.Add(new Run(text[cursor..nextToken]));
                cursor = nextToken;
            }

            if (cursor == nextCode)
            {
                var closeCode = text.IndexOf('`', cursor + 1);
                if (closeCode > cursor + 1)
                {
                    var codeText = text[(cursor + 1)..closeCode];
                    var codeRun = new Run(codeText)
                    {
                        FontFamily = new FontFamily("Consolas"),
                        Background = CodeBackground,
                        Foreground = DocumentForeground
                    };

                    inlines.Add(codeRun);
                    cursor = closeCode + 1;
                    continue;
                }
            }

            if (cursor == nextLink)
            {
                var closeBracket = text.IndexOf(']', cursor + 1);
                if (closeBracket > cursor + 1 && closeBracket + 1 < text.Length && text[closeBracket + 1] == '(')
                {
                    var closeParen = text.IndexOf(')', closeBracket + 2);
                    if (closeParen > closeBracket + 2)
                    {
                        var label = text[(cursor + 1)..closeBracket];
                        var uriText = text[(closeBracket + 2)..closeParen].Trim();
                        if (Uri.TryCreate(uriText, UriKind.Absolute, out var uri))
                        {
                            var hyperlink = new Hyperlink
                            {
                                Cursor = Cursors.Hand,
                                Foreground = LinkForeground,
                                TextDecorations = TextDecorations.Underline,
                                ToolTip = uri.AbsoluteUri
                            };

                            hyperlink.Inlines.Add(new Run(label));
                            hyperlink.Click += (_, _) => openLink(uri);
                            inlines.Add(hyperlink);
                            cursor = closeParen + 1;
                            continue;
                        }
                    }
                }
            }

            inlines.Add(new Run(text[cursor].ToString()));
            cursor++;
        }

        return inlines;
    }

    private static int NextTokenIndex(int codeIndex, int linkIndex)
    {
        if (codeIndex < 0)
        {
            return linkIndex;
        }

        if (linkIndex < 0)
        {
            return codeIndex;
        }

        return Math.Min(codeIndex, linkIndex);
    }

    private static SolidColorBrush BrushFromHex(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }

    private sealed class ListBuffer
    {
        private readonly List<string> _items = [];

        public TextMarkerStyle Style { get; private set; } = TextMarkerStyle.Disc;

        public IReadOnlyList<string> Items => _items;

        public bool HasItems => _items.Count > 0;

        public void Add(TextMarkerStyle style, string item)
        {
            if (!HasItems)
            {
                Style = style;
            }

            _items.Add(item);
        }

        public void Clear()
        {
            _items.Clear();
        }
    }
}
