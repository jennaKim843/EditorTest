using DiffPlex;
using DiffPlex.Chunkers;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace InnoPVManagementSystem.Modules.CompareMerge.Views
{
    public partial class ReadOnlyDiffView : UserControl
    {
        private string? _leftPath;
        private string? _rightPath;

        // 스크롤 동기화 루프 방지
        private bool _syncing;

        // 좌/우 라인번호 마진
        private DiffLineNumberMargin? _leftNumMargin;
        private DiffLineNumberMargin? _rightNumMargin;

        // 변경 블록 내비게이션
        private readonly List<(int leftLine, int rightLine)> _changeAnchors = new();
        private int _currentChangeIndex = -1;

        // 현재 변경 라인 하이라이트
        private CurrentChangeColorizer? _leftChangeMarker;
        private CurrentChangeColorizer? _rightChangeMarker;

        // 인라인 비교 모드
        public enum IntraLineMode { None, Word, Character, Pipe }

        public ReadOnlyDiffView()
        {
            InitializeComponent();

            // 기본 라인번호 대신 커스텀 마진 사용
            LeftEditor.ShowLineNumbers = false;
            RightEditor.ShowLineNumbers = false;

            // 스크롤 동기화
            LeftEditor.TextArea.TextView.ScrollOffsetChanged += (_, __) =>
            {
                if (_syncing) return;
                try { _syncing = true; RightEditor.ScrollToVerticalOffset(LeftEditor.TextArea.TextView.VerticalOffset); }
                finally { _syncing = false; }
            };
            RightEditor.TextArea.TextView.ScrollOffsetChanged += (_, __) =>
            {
                if (_syncing) return;
                try { _syncing = true; LeftEditor.ScrollToVerticalOffset(RightEditor.TextArea.TextView.VerticalOffset); }
                finally { _syncing = false; }
            };

            SetupEditor(LeftEditor);
            SetupEditor(RightEditor);

            // 라인번호 마진 추가
            _leftNumMargin = new DiffLineNumberMargin();
            _rightNumMargin = new DiffLineNumberMargin();
            LeftEditor.TextArea.LeftMargins.Insert(0, _leftNumMargin);
            RightEditor.TextArea.LeftMargins.Insert(0, _rightNumMargin);

            // 현재 변경 라인 마커
            _leftChangeMarker = new CurrentChangeColorizer();
            _rightChangeMarker = new CurrentChangeColorizer();
            LeftEditor.TextArea.TextView.LineTransformers.Add(_leftChangeMarker);
            RightEditor.TextArea.TextView.LineTransformers.Add(_rightChangeMarker);
        }

        #region DependencyProperties

        private static void SetupEditor(TextEditor ed)
        {
            ed.IsReadOnly = true;
            ed.Options.ConvertTabsToSpaces = true;
            ed.Options.ShowSpaces = false;
            ed.Options.ShowTabs = false;
            ed.Options.EnableHyperlinks = false;
            ed.Options.EnableEmailHyperlinks = false;
        }

        public string StatusText
        {
            get => (string)GetValue(StatusTextProperty);
            set => SetValue(StatusTextProperty, value);
        }
        public static readonly DependencyProperty StatusTextProperty =
            DependencyProperty.Register(nameof(StatusText), typeof(string), typeof(ReadOnlyDiffView),
                new PropertyMetadata(""));

        public IntraLineMode Mode
        {
            get => (IntraLineMode)GetValue(ModeProperty);
            set => SetValue(ModeProperty, value);
        }
        public static readonly DependencyProperty ModeProperty =
            DependencyProperty.Register(nameof(Mode), typeof(IntraLineMode), typeof(ReadOnlyDiffView),
                new PropertyMetadata(IntraLineMode.Pipe)); // 기본 Pipe

        #endregion

        #region Public UI Handlers

        public void CompareNow() => RunCompare();

        private void OnPickLeft(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "모든 파일|*.*" };
            if (dlg.ShowDialog() == true)
            {
                _leftPath = dlg.FileName;
                LeftEditor.Text = File.ReadAllText(_leftPath);
            }
        }

        private void OnPickRight(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "모든 파일|*.*" };
            if (dlg.ShowDialog() == true)
            {
                _rightPath = dlg.FileName;
                RightEditor.Text = File.ReadAllText(_rightPath);
            }
        }

        private void OnCompare(object sender, RoutedEventArgs e) => RunCompare();

        private void OnNextChange(object? sender, RoutedEventArgs e) => GoToChange(+1);
        private void OnPrevChange(object? sender, RoutedEventArgs e) => GoToChange(-1);

        // ── 카운트 바인딩용 DependencyProperty ──
        public int AddedRows
        {
            get => (int)GetValue(AddedRowsProperty);
            set => SetValue(AddedRowsProperty, value);
        }
        public static readonly DependencyProperty AddedRowsProperty =
            DependencyProperty.Register(nameof(AddedRows), typeof(int), typeof(ReadOnlyDiffView),
                new PropertyMetadata(0));

        public int DeletedRows
        {
            get => (int)GetValue(DeletedRowsProperty);
            set => SetValue(DeletedRowsProperty, value);
        }
        public static readonly DependencyProperty DeletedRowsProperty =
            DependencyProperty.Register(nameof(DeletedRows), typeof(int), typeof(ReadOnlyDiffView),
                new PropertyMetadata(0));

        public int ModifiedRows
        {
            get => (int)GetValue(ModifiedRowsProperty);
            set => SetValue(ModifiedRowsProperty, value);
        }
        public static readonly DependencyProperty ModifiedRowsProperty =
            DependencyProperty.Register(nameof(ModifiedRows), typeof(int), typeof(ReadOnlyDiffView),
                new PropertyMetadata(0));

        #endregion

        #region Orchestrator

        private void RunCompare()
        {
            var leftRaw = LeftEditor.Text ?? string.Empty;
            var rightRaw = RightEditor.Text ?? string.Empty;

            // 1) 라인 단위 Diff
            var differ = new Differ();
            var side = new SideBySideDiffBuilder(differ).BuildDiffModel(leftRaw, rightRaw);
            var leftPane = side.OldText;
            var rightPane = side.NewText;

            // 2) 표시용 문자열 (CR 제거)
            string leftDisplay = BuildAligned(leftPane.Lines, leftRaw.Length);
            string rightDisplay = BuildAligned(rightPane.Lines, rightRaw.Length);

            LeftEditor.Text = leftDisplay;
            RightEditor.Text = rightDisplay;

            // 3) 라인번호 마진 갱신
            UpdateLineNumberMargins(leftPane.Lines, rightPane.Lines);

            // 4) 인라인 하이라이트 계산 (수정된 라인만)
            var (subsLeft, subsRight) = BuildInlineSubPieces(leftPane, rightPane, leftDisplay, rightDisplay, differ);

            // 5) 컬러라이저 반영
            ApplyColorizers(leftPane.Lines, rightPane.Lines, subsLeft, subsRight);

            // 6) 상태 표시
            UpdateStatus(leftPane, rightPane);

            // 7) 변경 블록 내비게이션 준비
            RebuildChangeAnchors(leftPane.Lines, rightPane.Lines);
            if (_changeAnchors.Count > 0)
            {
                _currentChangeIndex = -1;
                GoToChange(+1);
            }
            else
            {
                _currentChangeIndex = -1;
                _leftChangeMarker?.Update(null);
                _rightChangeMarker?.Update(null);
                LeftEditor.TextArea.TextView.Redraw();
                RightEditor.TextArea.TextView.Redraw();
            }

            // 8) 현재 변경 강조가 라인 타입을 참조할 수 있도록 주입
            _leftChangeMarker?.SetLineTypes(leftPane.Lines);
            _rightChangeMarker?.SetLineTypes(rightPane.Lines);
        }

        #endregion

        #region Inline Pieces

        // 인라인 하이라이트 조각 생성(좌/우)
        private (List<IList<DiffPiece>>? left, List<IList<DiffPiece>>? right)
            BuildInlineSubPieces(
                DiffPaneModel leftPane,
                DiffPaneModel rightPane,
                string leftDisplay,
                string rightDisplay,
                Differ differ)
        {
            if (Mode == IntraLineMode.None)
                return (null, null);

            var subsLeft = new List<IList<DiffPiece>>(leftPane.Lines.Count);
            var subsRight = new List<IList<DiffPiece>>(rightPane.Lines.Count);

            var leftLinesArr = leftDisplay.Split('\n');
            var rightLinesArr = rightDisplay.Split('\n');

            int count = Math.Max(leftPane.Lines.Count, rightPane.Lines.Count);
            var inline = (Mode == IntraLineMode.Word || Mode == IntraLineMode.Character)
                ? new InlineDiffBuilder(differ)
                : null;

            for (int i = 0; i < count; i++)
            {
                var lp = i < leftPane.Lines.Count ? leftPane.Lines[i] : null;
                var rp = i < rightPane.Lines.Count ? rightPane.Lines[i] : null;

                subsLeft.Add(Array.Empty<DiffPiece>());
                subsRight.Add(Array.Empty<DiffPiece>());

                if (lp == null || rp == null) continue;
                if (lp.Type != ChangeType.Modified && rp.Type != ChangeType.Modified) continue;

                var l = i < leftLinesArr.Length ? TrimCR(leftLinesArr[i]) : string.Empty;
                var r = i < rightLinesArr.Length ? TrimCR(rightLinesArr[i]) : string.Empty;

                switch (Mode)
                {
                    case IntraLineMode.Pipe:
                        {
                            var (L, R) = BuildPipePiecesForSides(l, r);
                            subsLeft[i] = L;
                            subsRight[i] = R;
                            break;
                        }
                    case IntraLineMode.Word:
                    case IntraLineMode.Character:
                        {
                            IChunker chunker = (Mode == IntraLineMode.Character)
                                ? CharacterChunker.Instance
                                : WordChunker.Instance;

                            var inlineModel = inline!.BuildDiffModel(l, r, ignoreWhitespace: false, ignoreCase: false, chunker);
                            if (inlineModel?.Lines == null) break;

                            subsLeft[i] = MapForLeft(inlineModel.Lines);   // 삽입은 왼쪽에서 숨김
                            subsRight[i] = MapForRight(inlineModel.Lines); // 삭제는 오른쪽에서 숨김
                            break;
                        }
                }
            }

            return (subsLeft, subsRight);
        }

        // Pipe 토큰 단위 비교
        private static (IList<DiffPiece> leftPieces, IList<DiffPiece> rightPieces)
            BuildPipePiecesForSides(string leftLine, string rightLine)
        {
            var ltoks = leftLine.Split('|');
            var rtoks = rightLine.Split('|');

            int n = Math.Max(ltoks.Length, rtoks.Length);
            var lres = new List<DiffPiece>(n * 2);
            var rres = new List<DiffPiece>(n * 2);

            for (int idx = 0; idx < n; idx++)
            {
                string la = (idx < ltoks.Length) ? ltoks[idx] : string.Empty;
                string ra = (idx < rtoks.Length) ? rtoks[idx] : string.Empty;
                bool same = string.Equals(la, ra, StringComparison.Ordinal);

                lres.Add(new DiffPiece(la, same ? ChangeType.Unchanged : ChangeType.Modified));
                rres.Add(new DiffPiece(ra, same ? ChangeType.Unchanged : ChangeType.Modified));

                bool needSep = (idx < ltoks.Length - 1) || (idx < rtoks.Length - 1);
                if (needSep)
                {
                    lres.Add(new DiffPiece("|", ChangeType.Unchanged));
                    rres.Add(new DiffPiece("|", ChangeType.Unchanged));
                }
            }
            return (lres, rres);
        }

        // 왼쪽: Inserted → Unchanged 로 매핑
        private static IList<DiffPiece> MapForLeft(IList<DiffPiece> src)
        {
            if (src.Count == 0) return Array.Empty<DiffPiece>();
            var arr = new DiffPiece[src.Count];
            for (int i = 0; i < src.Count; i++)
            {
                var s = src[i];
                var t = (s.Type == ChangeType.Inserted) ? ChangeType.Unchanged : s.Type;
                arr[i] = new DiffPiece(s.Text ?? "", t, s.Position);
            }
            return arr;
        }

        // 오른쪽: Deleted → Unchanged 로 매핑
        private static IList<DiffPiece> MapForRight(IList<DiffPiece> src)
        {
            if (src.Count == 0) return Array.Empty<DiffPiece>();
            var arr = new DiffPiece[src.Count];
            for (int i = 0; i < src.Count; i++)
            {
                var s = src[i];
                var t = (s.Type == ChangeType.Deleted) ? ChangeType.Unchanged : s.Type;
                arr[i] = new DiffPiece(s.Text ?? "", t, s.Position);
            }
            return arr;
        }

        #endregion

        #region Colorizers + Margins

        // 컬러라이저 재적용
        private void ApplyColorizers(
            IList<DiffPiece> leftLines,
            IList<DiffPiece> rightLines,
            List<IList<DiffPiece>>? wordSubsLeft,
            List<IList<DiffPiece>>? wordSubsRight)
        {
            var lt = LeftEditor.TextArea.TextView.LineTransformers;
            var rt = RightEditor.TextArea.TextView.LineTransformers;

            RemoveTransformers<WordAwareColorizer>(lt);
            RemoveTransformers<WordAwareColorizer>(rt);

            lt.Insert(0, new WordAwareColorizer(leftLines, Mode, wordSubsLeft));
            rt.Insert(0, new WordAwareColorizer(rightLines, Mode, wordSubsRight));

            LeftEditor.TextArea.TextView.Redraw();
            RightEditor.TextArea.TextView.Redraw();
        }

        private static void RemoveTransformers<T>(IList<IVisualLineTransformer> list)
        {
            for (int i = list.Count - 1; i >= 0; i--)
                if (list[i] is T) list.RemoveAt(i);
        }

        private void UpdateLineNumberMargins(IList<DiffPiece> leftLines, IList<DiffPiece> rightLines)
        {
            var leftNums = BuildOriginalLineNumbers(leftLines);
            var rightNums = BuildOriginalLineNumbers(rightLines);

            _leftNumMargin?.Update(leftNums, DigitCountFromMax(leftNums));
            _rightNumMargin?.Update(rightNums, DigitCountFromMax(rightNums));
        }

        // 커스텀 라인번호 마진
        private sealed class DiffLineNumberMargin : AbstractMargin
        {
            private IList<int?> _lineNumbers = Array.Empty<int?>();
            private int _digits = 2;

            private static readonly Typeface _typeface = new Typeface("Consolas");
            private static readonly Brush _fg = Brushes.Gray;

            public void Update(IList<int?> lineNumbers, int digits)
            {
                _lineNumbers = lineNumbers ?? Array.Empty<int?>();
                _digits = Math.Max(2, digits);
                InvalidateMeasure();
                InvalidateVisual();
            }

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

            protected override void OnRender(DrawingContext dc)
            {
                var textView = this.TextView;
                if (textView == null || !textView.VisualLinesValid)
                    return;

                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(12, 0, 0, 0)), null, new Rect(0, 0, ActualWidth, ActualHeight));

                foreach (var vl in textView.VisualLines)
                {
                    int docIndex = vl.FirstDocumentLine.LineNumber - 1;
                    if (_lineNumbers == null || docIndex < 0 || docIndex >= _lineNumbers.Count)
                        continue;

                    int? n = _lineNumbers[docIndex];
                    if (!n.HasValue) continue;

                    double y = vl.GetTextLineVisualYPosition(vl.TextLines[0], VisualYPosition.TextTop);
                    string text = n.Value.ToString().PadLeft(_digits);

                    var ft = new FormattedText(
                        text, System.Globalization.CultureInfo.CurrentUICulture,
                        FlowDirection.LeftToRight, _typeface, 12, _fg, 1.25);
#if NET5_0_OR_GREATER
                    double textWidth = ft.WidthIncludingTrailingWhitespace;
#else
                    double textWidth = ft.Width;
#endif
                    double x = ActualWidth - textWidth - 6;
                    dc.DrawText(ft, new Point(Math.Max(0, x), y));
                }
            }
        }

        // 현재 변경 라인 강조(기존 계열 색을 더 진하게)
        private sealed class CurrentChangeColorizer : DocumentColorizingTransformer
        {
            private static readonly Brush StrongIns = Freeze(new SolidColorBrush(Color.FromArgb(96, 46, 204, 64)));
            private static readonly Brush StrongDel = Freeze(new SolidColorBrush(Color.FromArgb(96, 234, 67, 53)));
            private static readonly Brush StrongMod = Freeze(new SolidColorBrush(Color.FromArgb(88, 255, 193, 7)));

            private IList<DiffPiece>? _lineTypes;
            private int? _lineNumber;

            public void SetLineTypes(IList<DiffPiece> lineTypes) => _lineTypes = lineTypes;
            public void Update(int? lineNumber) => _lineNumber = lineNumber;

            private static Brush Freeze(Brush b)
            {
                if (b is Freezable f && f.CanFreeze) f.Freeze();
                return b;
            }

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

        // 라인 배경 + 인라인 하이라이트
        private sealed class WordAwareColorizer : DocumentColorizingTransformer
        {
            private static readonly Brush InsLine = Freeze(new SolidColorBrush(Color.FromArgb(28, 46, 204, 64)));
            private static readonly Brush DelLine = Freeze(new SolidColorBrush(Color.FromArgb(28, 234, 67, 53)));
            private static readonly Brush ModLine = Freeze(new SolidColorBrush(Color.FromArgb(22, 255, 193, 7)));

            private static readonly Brush InsWord = Freeze(new SolidColorBrush(Color.FromArgb(115, 76, 175, 80)));
            private static readonly Brush DelWord = Freeze(new SolidColorBrush(Color.FromArgb(115, 244, 67, 54)));
            private static readonly Brush ModWord = Freeze(new SolidColorBrush(Color.FromArgb(115, 255, 193, 7)));

            private readonly IList<DiffPiece> _lines;
            private readonly IntraLineMode _mode;
            private readonly List<IList<DiffPiece>>? _subs;

            public WordAwareColorizer(IList<DiffPiece> lines, IntraLineMode mode, List<IList<DiffPiece>>? subs)
            {
                _lines = lines;
                _mode = mode;
                _subs = subs;
            }

            private static Brush Freeze(Brush b)
            {
                if (b is Freezable f && f.CanFreeze) f.Freeze();
                return b;
            }

            protected override void ColorizeLine(DocumentLine line)
            {
                int index = line.LineNumber - 1;
                if (index < 0 || index >= _lines.Count) return;

                var dp = _lines[index];

                // 라인 배경
                Brush? lineBg = dp.Type switch
                {
                    ChangeType.Inserted => InsLine,
                    ChangeType.Deleted => DelLine,
                    ChangeType.Modified => ModLine,
                    _ => null
                };
                if (lineBg != null)
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
                    var t = sp.Text ?? string.Empty;
                    if (t.Length == 0) continue;

                    Brush? wbg = sp.Type switch
                    {
                        ChangeType.Inserted => InsWord,
                        ChangeType.Deleted => DelWord,
                        ChangeType.Modified => ModWord,
                        _ => null
                    };

                    if (wbg != null)
                    {
                        int len = t.Length;
                        int start = line.Offset + col;
                        int end = start + len;

                        // 오프셋 안전 클램프
                        int safeStart = Math.Max(line.Offset, Math.Min(start, line.EndOffset));
                        int safeEnd = Math.Max(safeStart, Math.Min(end, line.EndOffset));

                        if (safeStart < safeEnd)
                        {
                            ChangeLinePart(safeStart, safeEnd, el =>
                            {
                                el.TextRunProperties.SetBackgroundBrush(wbg);
                                var tf = el.TextRunProperties.Typeface;
                                el.TextRunProperties.SetTypeface(new Typeface(
                                    tf.FontFamily, tf.Style, FontWeights.SemiBold, tf.Stretch));
                            });
                        }
                    }

                    col += t.Length;
                }
            }
        }

        #endregion

        #region Anchors + Navigation

        private static bool IsChanged(ChangeType t)
            => t == ChangeType.Inserted || t == ChangeType.Deleted || t == ChangeType.Modified;

        // 변경 블록의 첫 줄을 앵커로 수집
        private void RebuildChangeAnchors(IList<DiffPiece> leftLines, IList<DiffPiece> rightLines)
        {
            _changeAnchors.Clear();
            int count = Math.Max(leftLines.Count, rightLines.Count);
            bool inBlock = false;

            for (int i = 0; i < count; i++)
            {
                var lt = (i < leftLines.Count) ? leftLines[i].Type : ChangeType.Imaginary;
                var rt = (i < rightLines.Count) ? rightLines[i].Type : ChangeType.Imaginary;
                bool changed = IsChanged(lt) || IsChanged(rt);

                if (changed && !inBlock)
                {
                    int l = Math.Min(i + 1, LeftEditor.Document?.LineCount ?? 1);
                    int r = Math.Min(i + 1, RightEditor.Document?.LineCount ?? 1);
                    _changeAnchors.Add((l, r));
                    inBlock = true;
                }
                else if (!changed)
                {
                    inBlock = false;
                }
            }
        }

        // 다음/이전 변경 블록으로 이동
        private void GoToChange(int direction)
        {
            if (_changeAnchors.Count == 0) return;

            _currentChangeIndex = (_currentChangeIndex + direction) % _changeAnchors.Count;
            if (_currentChangeIndex < 0) _currentChangeIndex += _changeAnchors.Count;

            var (l, r) = _changeAnchors[_currentChangeIndex];

            ScrollToLine(LeftEditor, l);
            ScrollToLine(RightEditor, r);

            _leftChangeMarker?.Update(l);
            _rightChangeMarker?.Update(r);
            LeftEditor.TextArea.TextView.Redraw();
            RightEditor.TextArea.TextView.Redraw();
        }

        // 특정 라인으로 스크롤
        private static void ScrollToLine(TextEditor ed, int line)
        {
            if (ed.Document == null || ed.Document.LineCount == 0) return;
            int target = Math.Max(1, Math.Min(line, ed.Document.LineCount));

            ed.TextArea.Caret.Line = target;
            ed.TextArea.Caret.Column = 1;

            ed.ScrollToLine(target);
            ed.TextArea.Caret.BringCaretToView();
        }

        #endregion

        #region Status + Utils

        // 상태 텍스트 갱신
        private void UpdateStatus(DiffPaneModel leftPane, DiffPaneModel rightPane)
        {
            int added = CountType(rightPane, ChangeType.Inserted);
            int deleted = CountType(leftPane, ChangeType.Deleted);

            int modified = 0;
            int n = Math.Max(leftPane.Lines.Count, rightPane.Lines.Count);
            for (int i = 0; i < n; i++)
            {
                var lt = (i < leftPane.Lines.Count) ? leftPane.Lines[i].Type : ChangeType.Unchanged;
                var rt = (i < rightPane.Lines.Count) ? rightPane.Lines[i].Type : ChangeType.Unchanged;
                if (lt == ChangeType.Modified || rt == ChangeType.Modified) modified++;
            }

            AddedRows = added;
            DeletedRows = deleted;
            ModifiedRows = modified;

            StatusText = $"추가 {added} / 삭제 {deleted} / 수정 {modified}";
        }

        // Diff 라인 → 표시 문자열
        private static string BuildAligned(IList<DiffPiece> lines, int reserve)
        {
            var sb = new StringBuilder(reserve + 64);
            for (int i = 0; i < lines.Count; i++)
            {
                var t = lines[i]?.Text ?? "";
                if (t.Length > 0 && t[^1] == '\r') t = t[..^1];
                sb.Append(t);
                if (i < lines.Count - 1) sb.Append('\n');
            }
            return sb.ToString();
        }

        // Imaginary 제외 원본 라인번호
        private static IList<int?> BuildOriginalLineNumbers(IList<DiffPiece> lines)
        {
            var list = new List<int?>(lines.Count);
            int current = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                var p = lines[i];
                if (p.Type == ChangeType.Imaginary)
                {
                    list.Add(null);
                }
                else
                {
                    current++;
                    list.Add(current);
                }
            }
            return list;
        }

        // 라인번호 자리수
        private static int DigitCountFromMax(IList<int?> nums)
        {
            int max = 0;
            foreach (var n in nums)
                if (n.HasValue && n.Value > max) max = n.Value;

            if (max <= 0) return 2;

            int d = 0;
            while (max > 0) { d++; max /= 10; }
            return Math.Max(2, d);
        }

        // 타입 카운트
        private static int CountType(DiffPaneModel pane, ChangeType type)
        {
            int n = 0;
            foreach (var line in pane.Lines)
                if (line.Type == type) n++;
            return n;
        }

        private static string TrimCR(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.EndsWith("\r") ? s[..^1] : s;
        }

        #endregion
    }
}
