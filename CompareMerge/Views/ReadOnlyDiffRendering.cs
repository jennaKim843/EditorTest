using DiffPlex.DiffBuilder.Model;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace InnoPVManagementSystem.Modules.CompareMerge.Views
{
    public partial class ReadOnlyDiffView
    {
        // ------------ LineNumber Margin ------------

        /// <summary>
        /// Diff 라인번호를 그려주는 커스텀 마진.
        /// (Imaginary 라인은 빈칸 처리, 자리수 자동 계산 지원)
        /// </summary>
        private sealed class DiffLineNumberMargin : AbstractMargin
        {
            private IList<int?> _lineNumbers = Array.Empty<int?>();
            private int _digits = 2;

            private static readonly Typeface _typeface = new Typeface("Consolas");
            private static readonly Brush _fg = Brushes.Gray;

            /// <summary>
            /// 라인번호 목록과 자리수를 갱신한다.
            /// (스크롤/레이아웃 변경 시 다시 렌더)
            /// </summary>
            public void Update(IList<int?> lineNumbers, int digits)
            {
                _lineNumbers = lineNumbers ?? Array.Empty<int?>();
                _digits = Math.Max(2, digits);
                InvalidateMeasure();
                InvalidateVisual();
            }

            /// <summary>
            /// TextView 변경 시 스크롤/가시라인 변경 이벤트를 연결/해제한다.
            /// (재렌더 트리거)
            /// </summary>
            protected override void OnTextViewChanged(TextView oldTextView, TextView newTextView)
            {
                base.OnTextViewChanged(oldTextView, newTextView);

                if (oldTextView != null)
                {
                    oldTextView.ScrollOffsetChanged -= TextView_Changed;
                    oldTextView.VisualLinesChanged -= TextView_Changed;
                }

                if (newTextView != null)
                {
                    newTextView.ScrollOffsetChanged += TextView_Changed;
                    newTextView.VisualLinesChanged += TextView_Changed;
                }
            }

            /// <summary>
            /// 스크롤/가시라인 변경 시 다시 그리도록 요청한다.
            /// </summary>
            private void TextView_Changed(object? sender, EventArgs e) => InvalidateVisual();

            /// <summary>
            /// 자리수 기준으로 마진 너비를 계산한다.
            /// (숫자 폭 안정화를 위해 '8' 사용)
            /// </summary>
            protected override Size MeasureOverride(Size availableSize)
            {
                var ft = new FormattedText(
                    new string('8', _digits),
                    System.Globalization.CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight,
                    _typeface, 12, _fg, 1.25);

#if NET5_0_OR_GREATER
                double w = ft.WidthIncludingTrailingWhitespace + 12;
#else
                double w = ft.Width + 12;
#endif
                return new Size(w, 0);
            }

            /// <summary>
            /// 현재 보이는 VisualLines에 대해 라인번호를 그린다.
            /// (Performance: 보이는 영역만 렌더)
            /// </summary>
            protected override void OnRender(DrawingContext dc)
            {
                var textView = TextView;
                if (textView == null || !textView.VisualLinesValid)
                    return;

                // 라인번호 영역 옅은 배경
                dc.DrawRectangle(
                    new SolidColorBrush(Color.FromArgb(12, 0, 0, 0)), null,
                    new Rect(0, 0, ActualWidth, ActualHeight));

                foreach (var vl in textView.VisualLines)
                {
                    int docLine = vl.FirstDocumentLine.LineNumber;
                    int index = docLine - 1;

                    if (index < 0 || index >= _lineNumbers.Count)
                        continue;

                    int? n = _lineNumbers[index];
                    if (!n.HasValue)
                        continue;

                    string text = n.Value.ToString().PadLeft(_digits);

                    double y =
                        vl.GetTextLineVisualYPosition(vl.TextLines[0], VisualYPosition.TextTop)
                        - textView.VerticalOffset;

                    var ft = new FormattedText(
                        text,
                        System.Globalization.CultureInfo.CurrentUICulture,
                        FlowDirection.LeftToRight,
                        _typeface, 12, _fg, 1.25);

#if NET5_0_OR_GREATER
                    double textWidth = ft.WidthIncludingTrailingWhitespace;
#else
                    double textWidth = ft.Width;
#endif
                    double x = ActualWidth - textWidth - 6;

                    dc.DrawText(ft, new Point(x, y));
                }
            }
        }

        // ------------ Current Change Colorizer ------------

        /// <summary>
        /// "현재 선택된 변경 라인"을 강하게 강조하는 컬러라이저.
        /// (Inserted/Deleted/Modified 타입별 배경 적용)
        /// </summary>
        private sealed class CurrentChangeColorizer : DocumentColorizingTransformer
        {
            private static readonly Brush StrongIns = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#602ECC40")));
            private static readonly Brush StrongDel = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#60EA4335")));
            private static readonly Brush StrongMod = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#58FFC107")));

            private IList<DiffPiece>? _lineTypes;
            private int? _lineNumber;

            /// <summary>
            /// 라인별 Diff 타입 목록을 세팅한다.
            /// (현재 라인의 ChangeType 판단에 사용)
            /// </summary>
            public void SetLineTypes(IList<DiffPiece> lineTypes) => _lineTypes = lineTypes;

            /// <summary>
            /// 강조할 라인 번호(1-based)를 갱신한다.
            /// (null이면 강조 해제)
            /// </summary>
            public void Update(int? lineNumber) => _lineNumber = lineNumber;

            /// <summary>
            /// 현재 라인 여부를 반환한다.
            /// (외부 컬러라이저가 "현재행" 판단 시 사용)
            /// </summary>
            public bool IsCurrentLine(int lineNumber) => _lineNumber.HasValue && _lineNumber.Value == lineNumber;

            /// <summary>
            /// 현재 라인에만 강한 배경색을 칠한다.
            /// (Line 전체 영역 배경)
            /// </summary>
            protected override void ColorizeLine(DocumentLine line)
            {
                if (!_lineNumber.HasValue) return;
                if (line.LineNumber != _lineNumber.Value) return;
                if (_lineTypes == null) return;

                int index = line.LineNumber - 1;
                if (index < 0 || index >= _lineTypes.Count) return;

                var type = _lineTypes[index].Type;
                Brush? strong = type switch
                {
                    ChangeType.Inserted => StrongIns,
                    ChangeType.Deleted => StrongDel,
                    ChangeType.Modified => StrongMod,
                    _ => null
                };
                if (strong == null) return;

                ChangeLinePart(line.Offset, line.EndOffset, el =>
                {
                    el.TextRunProperties.SetBackgroundBrush(strong);
                });
            }
        }

        // ------------ WordAware Colorizer ------------

        /// <summary>
        /// 라인 배경 + 인라인 변경(토큰/단어/문자)을 함께 처리하는 컬러라이저.
        /// (Pipe 모드의 Modified 토큰은 별도 렌더러로 분리)
        /// </summary>
        private sealed class WordAwareColorizer : DocumentColorizingTransformer
        {
            private static readonly Brush InsLine = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1C2ECC40")));
            private static readonly Brush DelLine = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1CEA4335")));
            private static readonly Brush ModLine = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#16FFC107")));

            private static readonly Brush InsWord = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#802E7D32")));
            private static readonly Brush DelWord = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#80C62828")));
            private static readonly Brush ModWord = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#80EF6C00")));

            private readonly IList<DiffPiece> _lines;
            private readonly IntraLineMode _mode;
            private readonly List<IList<DiffPiece>>? _subs;
            private readonly Func<int, bool>? _isCurrentLine;

            /// <summary>
            /// 라인 타입/인라인 토큰/현재행 판정 함수를 받아 컬러링을 수행한다.
            /// (UI 바인딩 없이 순수 렌더 로직)
            /// </summary>
            public WordAwareColorizer(IList<DiffPiece> lines, IntraLineMode mode, List<IList<DiffPiece>>? subs, Func<int, bool>? isCurrentLine)
            {
                _lines = lines;
                _mode = mode;
                _subs = subs;
                _isCurrentLine = isCurrentLine;
            }

            /// <summary>
            /// 라인 배경(기본) + 인라인 변경 영역 배경을 칠한다.
            /// (현재행은 Strong 배경이 있으므로 라인 배경은 생략)
            /// </summary>
            protected override void ColorizeLine(DocumentLine line)
            {
                int index = line.LineNumber - 1;
                if (index < 0 || index >= _lines.Count) return;

                var dp = _lines[index];
                bool isCurrent = _isCurrentLine?.Invoke(line.LineNumber) ?? false;

                // 라인 배경
                Brush? lineBg = dp.Type switch
                {
                    ChangeType.Inserted => InsLine,
                    ChangeType.Deleted => DelLine,
                    ChangeType.Modified => ModLine,
                    _ => null
                };

                if (!isCurrent && lineBg != null)
                {
                    ChangeLinePart(line.Offset, line.EndOffset, el =>
                    {
                        el.TextRunProperties.SetBackgroundBrush(lineBg);
                    });
                }

                // 인라인 하이라이트
                if (_mode == IntraLineMode.None) return;
                if (_subs == null) return;
                if (index >= _subs.Count) return;

                var subs = _subs[index];
                if (subs == null || subs.Count == 0) return;

                int col = 0;
                foreach (var sp in subs)
                {
                    string t = sp.Text ?? string.Empty;
                    if (t.Length == 0) continue;

                    // Pipe 모드의 Modified는 컬럼 렌더러에서 박스 처리 (중복 방지)
                    Brush? wbg = sp.Type switch
                    {
                        ChangeType.Inserted => InsWord,
                        ChangeType.Deleted => DelWord,
                        ChangeType.Modified => (_mode == IntraLineMode.Pipe ? null : ModWord),
                        _ => null
                    };

                    if (wbg != null)
                    {
                        int start = line.Offset + col;
                        int end = start + t.Length;

                        int safeStart = Math.Max(line.Offset, Math.Min(start, line.EndOffset));
                        int safeEnd = Math.Max(safeStart, Math.Min(end, line.EndOffset));

                        if (safeStart < safeEnd)
                        {
                            ChangeLinePart(safeStart, safeEnd, el =>
                            {
                                el.TextRunProperties.SetBackgroundBrush(wbg);
                            });
                        }
                    }

                    col += t.Length;
                }
            }
        }

        // ------------ Column Highlight Renderer ------------

        /// <summary>
        /// Pipe 모드에서 Modified 토큰만 "박스(배경+테두리)"로 표시하는 렌더러.
        /// (KnownLayer.Selection 사용: 라인 배경 위에 표시)
        /// </summary>
        private sealed class ColumnHighlightRenderer : IBackgroundRenderer
        {
            public KnownLayer Layer => KnownLayer.Selection;

            private IReadOnlyList<TextSegment> _segments = Array.Empty<TextSegment>();
            private readonly Brush _fill;
            private readonly Pen _pen;

            public ColumnHighlightRenderer(Brush fill, Pen pen)
            {
                _fill = fill;
                _pen = pen;
            }

            /// <summary>
            /// 하이라이트 대상 세그먼트(문서 오프셋 기반)를 갱신한다.
            /// </summary>
            public void Update(IList<TextSegment> segments)
                => _segments = (segments as IReadOnlyList<TextSegment>) ?? segments.ToArray();

            /// <summary>
            /// 현재 보이는 영역 기준으로 세그먼트의 사각형을 계산해
            /// 배경/테두리를 그린다.
            /// </summary>
            public void Draw(TextView textView, DrawingContext drawingContext)
            {
                if (_segments.Count == 0) return;
                if (!textView.VisualLinesValid) return;

                foreach (var seg in _segments)
                {
                    foreach (var r in BackgroundGeometryBuilder.GetRectsForSegment(textView, seg))
                    {
                        var rect = new Rect(r.Location, r.Size);
                        rect.Inflate(1.0, 0.5);

                        drawingContext.DrawRectangle(_fill, null, rect);
                        drawingContext.DrawRectangle(null, _pen, rect);
                    }
                }
            }
        }

        /// <summary>
        /// Pipe 모드 인라인 결과(subsByLine)에서
        /// Modified 토큰에 해당하는 문서 TextSegment를 계산한다.
        /// (ColumnHighlightRenderer 입력 전용)
        /// </summary>
        internal static IList<TextSegment> BuildPipeModifiedTokenSegments(TextDocument? doc, List<IList<DiffPiece>> subsByLine)
        {
            if (doc == null || subsByLine == null || subsByLine.Count == 0)
                return Array.Empty<TextSegment>();

            var segs = new List<TextSegment>(256);
            int lineCount = Math.Min(doc.LineCount, subsByLine.Count);

            for (int lineNo = 1; lineNo <= lineCount; lineNo++)
            {
                var docLine = doc.GetLineByNumber(lineNo);
                var subs = subsByLine[lineNo - 1];
                if (subs == null || subs.Count == 0) continue;

                int col = 0;

                foreach (var sp in subs)
                {
                    string t = sp.Text ?? string.Empty;
                    if (t.Length == 0) continue;

                    // Modified 토큰만 세그먼트로 수집 (구분자 제외)
                    if (sp.Type == ChangeType.Modified && t != "|")
                    {
                        int start = docLine.Offset + col;
                        int end = start + t.Length;

                        int safeStart = Math.Max(docLine.Offset, Math.Min(start, docLine.EndOffset));
                        int safeEnd = Math.Max(safeStart, Math.Min(end, docLine.EndOffset));

                        if (safeStart < safeEnd)
                            segs.Add(new TextSegment { StartOffset = safeStart, Length = safeEnd - safeStart });
                    }

                    col += t.Length;
                }
            }

            return segs;
        }
    }
}
