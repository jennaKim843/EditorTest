using DiffPlex;
using DiffPlex.Chunkers;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using InnoPVManagementSystem.Common.Services;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace InnoPVManagementSystem.Modules.CompareMerge.Views
{
    public partial class ReadOnlyDiffView : UserControl
    {
        // ------------ 상태 필드 ------------

        private string? _leftPath;
        private string? _rightPath;
        private string? _fileNameForKeyLookup;

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

        // 키 기반 비교 컨텍스트
        private KeyDiffContext? _keyDiff;

        // ------------ 생성자 ------------

        public ReadOnlyDiffView()
        {
            InitializeComponent();

            LeftEditor.ShowLineNumbers = false;
            RightEditor.ShowLineNumbers = false;

            LeftEditor.TextArea.TextView.ScrollOffsetChanged += (_, __) =>
                SyncScroll(LeftEditor, RightEditor);

            RightEditor.TextArea.TextView.ScrollOffsetChanged += (_, __) =>
                SyncScroll(RightEditor, LeftEditor);

            SetupEditor(LeftEditor);
            SetupEditor(RightEditor);

            _leftNumMargin = new DiffLineNumberMargin();
            _rightNumMargin = new DiffLineNumberMargin();
            LeftEditor.TextArea.LeftMargins.Insert(0, _leftNumMargin);
            RightEditor.TextArea.LeftMargins.Insert(0, _rightNumMargin);

            _leftChangeMarker = new CurrentChangeColorizer();
            _rightChangeMarker = new CurrentChangeColorizer();
            LeftEditor.TextArea.TextView.LineTransformers.Add(_leftChangeMarker);
            RightEditor.TextArea.TextView.LineTransformers.Add(_rightChangeMarker);
        }

        // ------------ 공용 설정 ------------

        public void SetFileNameForKeyLookup(string filePathOrName)
        {
            _fileNameForKeyLookup = string.IsNullOrWhiteSpace(filePathOrName)
                ? null
                : Path.GetFileName(filePathOrName);
        }

        private static void SetupEditor(TextEditor ed)
        {
            ed.IsReadOnly = true;
            ed.Options.ConvertTabsToSpaces = true;
            ed.Options.ShowSpaces = false;
            ed.Options.ShowTabs = false;
            ed.Options.EnableHyperlinks = false;
            ed.Options.EnableEmailHyperlinks = false;
        }

        // ------------ 스크롤 동기화 ------------
        private void SyncScroll(TextEditor src, TextEditor dst)
        {
            if (_syncing) return;

            var srcView = src.TextArea.TextView;
            var dstView = dst.TextArea.TextView;

            if (!srcView.VisualLinesValid || !dstView.VisualLinesValid)
                return;

            double srcScrollable = Math.Max(0, srcView.DocumentHeight - srcView.ActualHeight);
            double dstScrollable = Math.Max(0, dstView.DocumentHeight - dstView.ActualHeight);

            double targetV = dstView.VerticalOffset;
            bool canSyncVertical = (srcScrollable > 0) && (dstScrollable > 0);

            if (canSyncVertical)
            {
                double ratio = srcView.VerticalOffset / srcScrollable;
                ratio = Math.Max(0, Math.Min(1, ratio));
                targetV = ratio * dstScrollable;
            }

            double targetH = srcView.HorizontalOffset;

            bool verticalClose = Math.Abs(dstView.VerticalOffset - targetV) < 0.5;
            bool horizontalClose = Math.Abs(dstView.HorizontalOffset - targetH) < 0.5;

            if (verticalClose && horizontalClose)
                return;

            try
            {
                _syncing = true;

                if (!verticalClose && canSyncVertical)
                    dst.ScrollToVerticalOffset(targetV);

                if (!horizontalClose)
                    dst.ScrollToHorizontalOffset(targetH);
            }
            finally
            {
                _syncing = false;
            }
        }

        // ------------ DP & 명령 ------------
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
                new PropertyMetadata(IntraLineMode.Pipe));

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

        public void CompareNow() => RunCompare();

        private void OnCompare(object sender, RoutedEventArgs e) => RunCompare();
        private void OnNextChange(object? sender, RoutedEventArgs e) => GoToChange(+1);
        private void OnPrevChange(object? sender, RoutedEventArgs e) => GoToChange(-1);

        // ------------ Compare 파이프라인 (핵심) ------------
        public void RunCompare()
        {
            var leftRaw = LeftEditor.Text ?? string.Empty;
            var rightRaw = RightEditor.Text ?? string.Empty;

            // 1) 키 컨텍스트 생성 (없으면 null)
            _keyDiff = KeyDiffContext.TryCreate(_fileNameForKeyLookup, leftRaw);

            // 2) 라인 생성: 키가 있으면 키 정렬, 없으면 DiffPlex
            IList<DiffPiece> leftLines;
            IList<DiffPiece> rightLines;

            var differ = new Differ();

            if (_keyDiff != null)
            {
                (leftLines, rightLines) = BuildKeyAlignedLines(_keyDiff, leftRaw, rightRaw);
            }
            else
            {
                var side = new SideBySideDiffBuilder(differ).BuildDiffModel(leftRaw, rightRaw);
                leftLines = side.OldText.Lines;
                rightLines = side.NewText.Lines;
            }

            // 3) 표시용 문자열
            string leftDisplay = BuildAligned(leftLines, leftRaw.Length);
            string rightDisplay = BuildAligned(rightLines, rightRaw.Length);

            LeftEditor.Text = leftDisplay;
            RightEditor.Text = rightDisplay;

            // 4) 라인번호 / 인라인 / 색칠 / 상태 / 앵커
            UpdateLineNumberMargins(leftLines, rightLines);

            var (subsLeft, subsRight) = BuildInlineSubPieces(
                leftLines, rightLines, leftDisplay, rightDisplay, differ);

            ApplyColorizers(leftLines, rightLines, subsLeft, subsRight);
            UpdateStatus(leftLines, rightLines);

            RebuildChangeAnchors(leftLines, rightLines);
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

            _leftChangeMarker?.SetLineTypes(leftLines);
            _rightChangeMarker?.SetLineTypes(rightLines);
        }

        // ------------ 키 정렬 기반 라인 빌더 ------------
        private (IList<DiffPiece> leftLines, IList<DiffPiece> rightLines)
            BuildKeyAlignedLines(KeyDiffContext ctx, string leftText, string rightText)
        {
            static string[] SplitLines(string text) =>
                text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            var leftArr = SplitLines(leftText);
            var rightArr = SplitLines(rightText);

            var leftResult = new List<DiffPiece>();
            var rightResult = new List<DiffPiece>();

            // 1) 헤더 1행
            string leftHeader = leftArr.Length > 0 ? leftArr[0] : string.Empty;
            string rightHeader = rightArr.Length > 0 ? rightArr[0] : leftHeader;

            if (!string.IsNullOrEmpty(leftHeader) || !string.IsNullOrEmpty(rightHeader))
            {
                leftResult.Add(new DiffPiece(leftHeader, ChangeType.Unchanged));
                rightResult.Add(new DiffPiece(rightHeader, ChangeType.Unchanged));
            }

            int dataStart = 1;

            // 2) 구분선(2행) 동일 시 그대로 한 줄 더
            if (leftArr.Length > 1 || rightArr.Length > 1)
            {
                string l2 = leftArr.Length > 1 ? leftArr[1] : string.Empty;
                string r2 = rightArr.Length > 1 ? rightArr[1] : l2;

                if (string.Equals(l2, r2, StringComparison.Ordinal))
                {
                    leftResult.Add(new DiffPiece(l2, ChangeType.Unchanged));
                    rightResult.Add(new DiffPiece(r2, ChangeType.Unchanged));
                    dataStart = 2;
                }
            }

            // 3) 데이터 영역을 키 기반으로 merge-join
            var leftData = ctx.BuildKeyedLines(leftArr, dataStart);
            var rightData = ctx.BuildKeyedLines(rightArr, dataStart);

            int i = 0, j = 0;
            while (i < leftData.Count || j < rightData.Count)
            {
                if (i >= leftData.Count)
                {
                    // 오른쪽만 남음 → Added
                    var r = rightData[j++];
                    leftResult.Add(new DiffPiece(string.Empty, ChangeType.Imaginary));
                    rightResult.Add(new DiffPiece(r.LineText, ChangeType.Inserted));
                    continue;
                }

                if (j >= rightData.Count)
                {
                    // 왼쪽만 남음 → Deleted
                    var l = leftData[i++];
                    leftResult.Add(new DiffPiece(l.LineText, ChangeType.Deleted));
                    rightResult.Add(new DiffPiece(string.Empty, ChangeType.Imaginary));
                    continue;
                }

                var lcur = leftData[i];
                var rcur = rightData[j];

                int cmp = string.Compare(lcur.Key, rcur.Key, StringComparison.Ordinal);
                if (cmp == 0)
                {
                    bool modified = ctx.HasNonKeyDiff(lcur.LineText, rcur.LineText);
                    var type = modified ? ChangeType.Modified : ChangeType.Unchanged;

                    leftResult.Add(new DiffPiece(lcur.LineText, type));
                    rightResult.Add(new DiffPiece(rcur.LineText, type));

                    i++;
                    j++;
                }
                else if (cmp < 0)
                {
                    // 왼쪽 키가 더 작음 → 삭제
                    var l = leftData[i++];
                    leftResult.Add(new DiffPiece(l.LineText, ChangeType.Deleted));
                    rightResult.Add(new DiffPiece(string.Empty, ChangeType.Imaginary));
                }
                else
                {
                    // 오른쪽 키가 더 작음 → 추가
                    var r = rightData[j++];
                    leftResult.Add(new DiffPiece(string.Empty, ChangeType.Imaginary));
                    rightResult.Add(new DiffPiece(r.LineText, ChangeType.Inserted));
                }
            }

            return (leftResult, rightResult);
        }

        // ------------ 인라인 하이라이트 ------------
        private (List<IList<DiffPiece>>? left, List<IList<DiffPiece>>? right)
            BuildInlineSubPieces(
                IList<DiffPiece> leftLines,
                IList<DiffPiece> rightLines,
                string leftDisplay,
                string rightDisplay,
                Differ differ)
        {
            if (Mode == IntraLineMode.None)
                return (null, null);

            var subsLeft = new List<IList<DiffPiece>>(leftLines.Count);
            var subsRight = new List<IList<DiffPiece>>(rightLines.Count);

            var leftLinesArr = leftDisplay.Split('\n');
            var rightLinesArr = rightDisplay.Split('\n');

            int count = Math.Max(leftLines.Count, rightLines.Count);
            var inline = (Mode == IntraLineMode.Word || Mode == IntraLineMode.Character)
                ? new InlineDiffBuilder(differ)
                : null;

            for (int i = 0; i < count; i++)
            {
                DiffPiece? lp = i < leftLines.Count ? leftLines[i] : null;
                DiffPiece? rp = i < rightLines.Count ? rightLines[i] : null;

                subsLeft.Add(Array.Empty<DiffPiece>());
                subsRight.Add(Array.Empty<DiffPiece>());

                if (lp == null || rp == null) continue;
                if (!IsModifiedLine(lp, rp)) continue;

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

                            subsLeft[i] = MapForLeft(inlineModel.Lines);
                            subsRight[i] = MapForRight(inlineModel.Lines);
                            break;
                        }
                }
            }

            return (subsLeft, subsRight);
        }

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

        // ------------ 컬러라이저 / 마진 (기존 유지) ------------

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

            //lt.Insert(0, new WordAwareColorizer(leftLines, Mode, wordSubsLeft));
            //rt.Insert(0, new WordAwareColorizer(rightLines, Mode, wordSubsRight));
            lt.Add(new WordAwareColorizer(
                leftLines,
                Mode,
                wordSubsLeft,
                lineNumber => _leftChangeMarker?.IsCurrentLine(lineNumber) ?? false
            ));

            rt.Add(new WordAwareColorizer(
                rightLines,
                Mode,
                wordSubsRight,
                lineNumber => _rightChangeMarker?.IsCurrentLine(lineNumber) ?? false
            ));

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

            // 스크롤/레이아웃 바뀔 때마다 다시 그리기
            private void TextView_Changed(object? sender, EventArgs e)
            {
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


        // 현재 변경 라인 강조(기존 계열 색을 더 진하게)
        private sealed class CurrentChangeColorizer : DocumentColorizingTransformer
        {
            private static readonly Brush StrongIns = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#602ECC40")));
            private static readonly Brush StrongDel = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#60EA4335")));
            private static readonly Brush StrongMod = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#58FFC107")));

            private IList<DiffPiece>? _lineTypes;
            private int? _lineNumber;

            public void SetLineTypes(IList<DiffPiece> lineTypes) => _lineTypes = lineTypes;
            public void Update(int? lineNumber) => _lineNumber = lineNumber;
            public bool IsCurrentLine(int lineNumber) => _lineNumber.HasValue && _lineNumber.Value == lineNumber;

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
            // Line colors
            private static readonly Brush InsLine = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1C2ECC40")));
            private static readonly Brush DelLine = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1CEA4335")));
            private static readonly Brush ModLine = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#16FFC107")));

            // Word colors
            //private static readonly Brush InsWord = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#734CAF50")));
            //private static readonly Brush DelWord = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#73F44336")));
            //private static readonly Brush ModWord = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#73FFC107")));
            private static readonly Brush InsWord = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#802E7D32"))); // 진한 녹색
            private static readonly Brush DelWord = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#80C62828"))); // 진한 적색
            private static readonly Brush ModWord = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#80EF6C00"))); // 진한 오렌지


            private readonly IList<DiffPiece> _lines;
            private readonly IntraLineMode _mode;
            private readonly List<IList<DiffPiece>>? _subs;
            private readonly Func<int, bool>? _isCurrentLine;

            public WordAwareColorizer(IList<DiffPiece> lines, IntraLineMode mode, List<IList<DiffPiece>>? subs, Func<int, bool>? isCurrentLine)
            {
                _lines = lines;
                _mode = mode;
                _subs = subs;
                _isCurrentLine = isCurrentLine;
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

                bool isCurrent = _isCurrentLine?.Invoke(line.LineNumber) ?? false;

                // 라인 배경
                Brush? lineBg = dp.Type switch
                {
                    ChangeType.Inserted => InsLine,
                    ChangeType.Deleted => DelLine,
                    ChangeType.Modified => ModLine,
                    _ => null
                };

                // 현재행이 아니면 기존처럼 라인 배경 칠함
                //   현재행이면 Strong 배경(다른 Colorizer)이 깔려 있으니 여기선 건드리지 않음
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

        // ------------ 앵커 / 네비게이션 ------------
        private static bool IsChanged(ChangeType t)
            => t == ChangeType.Inserted || t == ChangeType.Deleted || t == ChangeType.Modified;

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

        private void GoToChange(int direction)
        {
            if (_changeAnchors.Count == 0) return;

            _currentChangeIndex = (_currentChangeIndex + direction) % _changeAnchors.Count;
            if (_currentChangeIndex < 0) _currentChangeIndex += _changeAnchors.Count;

            var (l, r) = _changeAnchors[_currentChangeIndex];

            try
            {
                _syncing = true;
                ScrollToLine(LeftEditor, l);
                ScrollToLine(RightEditor, r);
            }
            finally
            {
                _syncing = false;
            }

            _leftChangeMarker?.Update(l);
            _rightChangeMarker?.Update(r);
            LeftEditor.TextArea.TextView.Redraw();
            RightEditor.TextArea.TextView.Redraw();
        }

        private static void ScrollToLine(TextEditor ed, int line)
        {
            if (ed.Document == null || ed.Document.LineCount == 0) return;
            int target = Math.Max(1, Math.Min(line, ed.Document.LineCount));

            ed.TextArea.Caret.Line = target;
            ed.TextArea.Caret.Column = 1;

            ed.ScrollToLine(target);
            ed.TextArea.Caret.BringCaretToView();
        }

        // ------------ 상태 / 유틸 ------------

        private void UpdateStatus(IList<DiffPiece> leftLines, IList<DiffPiece> rightLines)
        {
            int added = CountType(rightLines, ChangeType.Inserted);
            int deleted = CountType(leftLines, ChangeType.Deleted);

            int modified = 0;
            int n = Math.Max(leftLines.Count, rightLines.Count);
            for (int i = 0; i < n; i++)
            {
                DiffPiece? lp = (i < leftLines.Count) ? leftLines[i] : null;
                DiffPiece? rp = (i < rightLines.Count) ? rightLines[i] : null;

                if (IsModifiedLine(lp, rp))
                    modified++;
            }

            AddedRows = added;
            DeletedRows = deleted;
            ModifiedRows = modified;

            StatusText = $"추가 {added} / 삭제 {deleted} / 수정 {modified}";
        }

        private static string BuildAligned(IList<DiffPiece> lines, int reserve)
        {
            var sb = new StringBuilder(reserve + 64);

            for (int i = 0; i < lines.Count; i++)
            {
                var t = lines[i]?.Text;

                // 빈줄이면 " " 으로 채워서 DocumentLine 붕괴 막기
                if (string.IsNullOrEmpty(t))
                    t = " ";

                sb.Append(t);

                if (i < lines.Count - 1)
                    sb.Append('\n');
            }

            return sb.ToString();
        }

        // 원본 행 번호 기준 + Imaginary 는 빈칸
        private static IList<int?> BuildOriginalLineNumbers(IList<DiffPiece> lines)
        {
            var list = new List<int?>(lines.Count);
            int current = 0;   // 실제 행만 번호 증가

            for (int i = 0; i < lines.Count; i++)
            {
                var p = lines[i];

                if (p.Type == ChangeType.Imaginary)
                {
                    // 한쪽에만 존재하는 줄 → 번호 없이 빈 칸
                    list.Add(null);
                }
                else
                {
                    // 실제 행일 때만 1,2,3,... 증가
                    current++;
                    list.Add(current);
                }
            }

            return list;
        }

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

        private static int CountType(IList<DiffPiece> lines, ChangeType type)
        {
            int n = 0;
            foreach (var line in lines)
                if (line.Type == type) n++;
            return n;
        }

        private static string TrimCR(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.EndsWith("\r") ? s[..^1] : s;
        }

        private bool IsModifiedLine(DiffPiece? left, DiffPiece? right)
        {
            var lt = left?.Type ?? ChangeType.Unchanged;
            var rt = right?.Type ?? ChangeType.Unchanged;
            return lt == ChangeType.Modified || rt == ChangeType.Modified;
        }

        // ------------ KeyDiffContext (키 관련 로직 캡슐화) ------------

        private sealed class KeyDiffContext
        {
            public string Delimiter { get; }
            public int[] KeyIndexes { get; }
            public HashSet<int> KeyIndexSet { get; }
            public Dictionary<string, int> HeaderIndexMap { get; }

            private KeyDiffContext(
                string delimiter,
                int[] keyIndexes,
                HashSet<int> keyIndexSet,
                Dictionary<string, int> headerIndexMap)
            {
                Delimiter = delimiter;
                KeyIndexes = keyIndexes;
                KeyIndexSet = keyIndexSet;
                HeaderIndexMap = headerIndexMap;
            }

            public static KeyDiffContext? TryCreate(string? fileName, string fullText)
            {
                if (string.IsNullOrEmpty(fileName) || string.IsNullOrEmpty(fullText))
                    return null;

                var (delimiter, keys) = FileKeyManager.GetFileSettingsOrDefault(fileName, "|");
                var delim = string.IsNullOrEmpty(delimiter) ? "|" : delimiter;
                if (keys == null || keys.Count == 0) return null;

                // 헤더 한 줄 추출
                string headerLine;
                int idx = fullText.IndexOfAny(new[] { '\r', '\n' });
                headerLine = idx >= 0 ? fullText.Substring(0, idx) : fullText;
                if (string.IsNullOrEmpty(headerLine)) return null;

                var headers = SplitSafe(headerLine, delim);

                var headerMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < headers.Length; i++)
                {
                    var name = headers[i]?.Trim() ?? $"col{i}";
                    if (!headerMap.ContainsKey(name))
                        headerMap[name] = i;
                }

                var idxList = new List<int>();
                foreach (var col in keys)
                {
                    if (headerMap.TryGetValue(col, out int colIdx))
                        idxList.Add(colIdx);
                }

                if (idxList.Count == 0) return null;

                var idxArray = idxList.ToArray();
                var idxSet = new HashSet<int>(idxArray);

                return new KeyDiffContext(delim, idxArray, idxSet, headerMap);
            }

            // ----- 헬퍼 메서드들 -----

            public List<(string Key, string LineText)> BuildKeyedLines(string[] lines, int startIndex)
            {
                var list = new List<(string, string)>();

                for (int i = startIndex; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (IsHeaderLine(line)) continue;

                    string key = GetKeyFromLine(line);
                    if (string.IsNullOrEmpty(key)) continue;

                    list.Add((key, line));
                }

                return list;
            }

            public bool HasNonKeyDiff(string leftText, string rightText)
            {
                var leftCols = SplitSafe(leftText, Delimiter);
                var rightCols = SplitSafe(rightText, Delimiter);
                int maxLen = Math.Max(leftCols.Length, rightCols.Length);

                for (int colIndex = 0; colIndex < maxLen; colIndex++)
                {
                    if (KeyIndexSet.Contains(colIndex))
                        continue;

                    string lv = colIndex < leftCols.Length ? (leftCols[colIndex] ?? "").Trim() : string.Empty;
                    string rv = colIndex < rightCols.Length ? (rightCols[colIndex] ?? "").Trim() : string.Empty;

                    if (!string.Equals(lv, rv, StringComparison.Ordinal))
                        return true;
                }

                return false;
            }

            private bool IsHeaderLine(string lineText)
            {
                var cols = SplitSafe(lineText, Delimiter);
                if (cols.Length == 0) return false;

                int headerLikeCount = 0;
                foreach (var c in cols)
                {
                    var name = (c ?? string.Empty).Trim();
                    if (name.Length == 0) continue;
                    if (HeaderIndexMap.ContainsKey(name))
                        headerLikeCount++;
                }

                return headerLikeCount >= Math.Max(1, cols.Length / 2);
            }

            private string GetKeyFromLine(string line)
            {
                var cols = SplitSafe(line, Delimiter);
                if (cols.Length == 0) return string.Empty;

                var parts = new string[KeyIndexes.Length];

                for (int i = 0; i < KeyIndexes.Length; i++)
                {
                    int idx = KeyIndexes[i];
                    string v = (idx >= 0 && idx < cols.Length) ? (cols[idx] ?? "").Trim() : string.Empty;
                    parts[i] = v;
                }

                return string.Join("\u001F", parts);
            }

            private static string[] SplitSafe(string line, string delimiter)
            {
                if (line == null)
                    return Array.Empty<string>();

                if (string.IsNullOrEmpty(delimiter))
                    return line.Split('|');

                return line.Split(new[] { delimiter }, StringSplitOptions.None);
            }
        }
    }
}
