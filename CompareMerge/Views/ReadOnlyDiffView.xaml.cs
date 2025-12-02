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
        private string? _leftPath;
        private string? _rightPath;

        private string? _fileNameForKeyLookup;

        public void SetFileNameForKeyLookup(string filePathOrName)
        {
            if (string.IsNullOrWhiteSpace(filePathOrName))
            {
                _fileNameForKeyLookup = null;
                return;
            }

            // 전체 경로든, 파일명만이든 들어와도 괜찮게 처리
            _fileNameForKeyLookup = Path.GetFileName(filePathOrName);
        }

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
        // 키 기반 행 상태
        private enum RowState {Unchanged, Added, Deleted, Modified }

        // ── 키 기반 비교 컨텍스트 ──────────────────────────────
        private string _currentDelimiter ="|";
        private List<string>? _keyColumns;
        private int[]? _keyColumnIndexes;
        private HashSet<int>? _keyColumnIndexSet;
        private Dictionary<string, int>? _headerIndexMap;
        private bool _keyContextInitialized;

        public ReadOnlyDiffView()
        {
            InitializeComponent();

            // 기본 라인번호 대신 커스텀 마진 사용
            LeftEditor.ShowLineNumbers = false;
            RightEditor.ShowLineNumbers = false;

            // 양방향 스크롤 동기화 (세로/가로 모두)
            LeftEditor.TextArea.TextView.ScrollOffsetChanged += (_, __) =>
                SyncScroll(LeftEditor, RightEditor);

            RightEditor.TextArea.TextView.ScrollOffsetChanged += (_, __) =>
                SyncScroll(RightEditor, LeftEditor);

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

        // 실제 스크롤 동기화 로직 (양방향)
        //  - 세로: src의 "스크롤 비율" 기준으로 dst 위치 계산
        //  - 가로: offset 그대로 복사
        //  - _syncing 플래그로 루프 방지
        //  - 오프셋 차이가 거의 없으면 아무 것도 안 해서 깜빡임 최소화
        private void SyncScroll(TextEditor src, TextEditor dst)
        {
            if (_syncing) return;

            var srcView = src.TextArea.TextView;
            var dstView = dst.TextArea.TextView;

            // 아직 레이아웃이 안 됐으면 패스
            if (!srcView.VisualLinesValid || !dstView.VisualLinesValid)
                return;

            // ── 1) 세로: 비율 기반 동기화 ──
            double srcScrollable = Math.Max(0, srcView.DocumentHeight - srcView.ActualHeight);
            double dstScrollable = Math.Max(0, dstView.DocumentHeight - dstView.ActualHeight);

            double targetV = dstView.VerticalOffset; // 기본값: 그대로
            bool canSyncVertical = (srcScrollable > 0) && (dstScrollable > 0);

            if (canSyncVertical)
            {
                // src에서 현재 스크롤 비율 (0.0 ~ 1.0)
                double ratio = srcView.VerticalOffset / srcScrollable;
                if (ratio < 0) ratio = 0;
                if (ratio > 1) ratio = 1;

                // dst의 동일 비율 위치
                targetV = ratio * dstScrollable;
            }

            // ── 2) 가로: 기존처럼 절대 offset 복사 ──
            double targetH = srcView.HorizontalOffset;

            bool verticalClose = Math.Abs(dstView.VerticalOffset - targetV) < 0.5;   // 약간 느슨하게
            bool horizontalClose = Math.Abs(dstView.HorizontalOffset - targetH) < 0.5;

            // 둘 다 거의 같으면 아무 것도 안 함
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

        //private void OnPickLeft(object sender, RoutedEventArgs e)
        //{
        //    var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "모든 파일|*.*" };
        //    if (dlg.ShowDialog() == true)
        //    {
        //        _leftPath = dlg.FileName;
        //        LeftEditor.Text = File.ReadAllText(_leftPath);
        //    }
        //}

        //private void OnPickRight(object sender, RoutedEventArgs e)
        //{
        //    var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "모든 파일|*.*" };
        //    if (dlg.ShowDialog() == true)
        //    {
        //        _rightPath = dlg.FileName;
        //        RightEditor.Text = File.ReadAllText(_rightPath);
        //    }
        //}

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

        public void RunCompare()
        {
            var leftRaw = LeftEditor.Text ?? string.Empty;
            var rightRaw = RightEditor.Text ?? string.Empty;

            // 키 정보 초기화 (fileKeyConfig 기반)
            InitKeyContext(leftRaw);

            // 키 기반 행 상태 맵 (키가 없으면 null)
            Dictionary<string, RowState>? rowStates = null;
            if (_keyContextInitialized && _keyColumnIndexSet != null && _headerIndexMap != null)
            {
                rowStates = BuildRowStates(leftRaw, rightRaw);
            }

            // 1) 라인 단위 Diff (레이아웃 + 인라인 기준용)
            var differ = new Differ();
            var side = new SideBySideDiffBuilder(differ).BuildDiffModel(leftRaw, rightRaw);
            var leftPane = side.OldText;
            var rightPane = side.NewText;

            //// 키 기준으로 Modified를 Inserted/Deleted로 재분류
            //NormalizeChangeTypesByKey(leftPane, rightPane);
            // DiffPlex 타입을 키 기반으로 덮어쓰기
            if (rowStates != null)
            {
                OverrideChangeTypesByKey(leftPane, rightPane, rowStates);
            }

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

                // 키 기준 수정 여부 판단
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

                    //int? n = _lineNumbers[docIndex];
                    //if (!n.HasValue) continue;

                    // ─────────────────────────────
                    // ① _lineNumbers 기반 우선 시도
                    // ─────────────────────────────
                    int? n = null;

                    if (_lineNumbers != null &&
                        docIndex < _lineNumbers.Count)
                    {
                        n = _lineNumbers[docIndex];
                    }

                    // ─────────────────────────────
                    // ② fallback: 그냥 실제 라인 번호 찍기
                    //    (_lineNumbers 비어 있거나 null이면)
                    // ─────────────────────────────
                    if (!n.HasValue)
                    {
                        n = vl.FirstDocumentLine.LineNumber;
                    }

                    string text = n.Value.ToString().PadLeft(_digits);

                    double y = vl.GetTextLineVisualYPosition(vl.TextLines[0], VisualYPosition.TextTop);

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
            private static readonly Brush StrongIns = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#602ECC40")));
            private static readonly Brush StrongDel = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#60EA4335")));
            private static readonly Brush StrongMod = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#58FFC107")));

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
            // Line colors
            private static readonly Brush InsLine = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1C2ECC40")));
            private static readonly Brush DelLine = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1CEA4335")));
            private static readonly Brush ModLine = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#16FFC107")));

            // Word colors
            private static readonly Brush InsWord = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#734CAF50")));
            private static readonly Brush DelWord = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#73F44336")));
            private static readonly Brush ModWord = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#73FFC107")));


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

            try
            {
                // 점프하는 동안에는 스크롤 동기화 막기
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
                DiffPiece? lp = (i < leftPane.Lines.Count) ? leftPane.Lines[i] : null;
                DiffPiece? rp = (i < rightPane.Lines.Count) ? rightPane.Lines[i] : null;

                if (IsModifiedLine(lp, rp))
                    modified++;
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

        //// ── 키 기준으로 Modified 라인 재분류 ──────────────────────
        //private void NormalizeChangeTypesByKey(DiffPaneModel leftPane, DiffPaneModel rightPane)
        //{
        //    if (!_keyContextInitialized || _keyColumnIndexSet == null || _headerIndexMap == null)
        //        return;

        //    int n = Math.Max(leftPane.Lines.Count, rightPane.Lines.Count);

        //    for (int i = 0; i < n; i++)
        //    {
        //        DiffPiece? lp = (i < leftPane.Lines.Count) ? leftPane.Lines[i] : null;
        //        DiffPiece? rp = (i < rightPane.Lines.Count) ? rightPane.Lines[i] : null;

        //        if (lp == null && rp == null) continue;

        //        var lt = lp?.Type ?? ChangeType.Imaginary;
        //        var rt = rp?.Type ?? ChangeType.Imaginary;

        //        bool typeModified = (lt == ChangeType.Modified) || (rt == ChangeType.Modified);
        //        if (!typeModified) continue;

        //        // 키 기준으로 "진짜 수정"인지 판단
        //        if (IsModifiedLine(lp, rp))
        //        {
        //            // 키/비키 기준으로 수정이 맞으면 Modified 유지
        //            continue;
        //        }

        //        // 여기까지 왔다는 건: DiffPlex는 Modified라고 했지만
        //        // 키가 다르거나 비키도 변경이 없어서 "수정"으로 보지 않는 경우
        //        // => 이 라인을 삭제/추가로 재해석한다.

        //        if (lp != null)
        //            lp.Type = ChangeType.Deleted;

        //        if (rp != null)
        //            rp.Type = ChangeType.Inserted;
        //    }
        //}
        // ─────────────────────────────────────────────
        //  키 기반 RowState 빌드
        // ─────────────────────────────────────────────

        private Dictionary<string, RowState> BuildRowStates(string leftText, string rightText)
        {
            var result = new Dictionary<string, RowState>(StringComparer.Ordinal);

            // 1) 키 → 라인 문자열 맵 생성 (헤더 제외)
            var leftMap = BuildKeyToLineMap(leftText);
            var rightMap = BuildKeyToLineMap(rightText);

            // 2) 왼쪽 기준: Deleted / Modified / Unchanged
            foreach (var kv in leftMap)
            {
                string key = kv.Key;
                string leftLine = kv.Value;

                if (!rightMap.TryGetValue(key, out var rightLine))
                {
                    result[key] = RowState.Deleted;
                }
                else
                {
                    if (HasNonKeyDiff(leftLine, rightLine))
                        result[key] = RowState.Modified;
                    else
                        result[key] = RowState.Unchanged;
                }
            }

            // 3) 오른쪽에만 있는 키 → Added
            foreach (var kv in rightMap)
            {
                string key = kv.Key;
                if (!result.ContainsKey(key))
                {
                    result[key] = RowState.Added;
                }
            }

            return result;
        }

        /// <summary>
        /// 전체 텍스트에서 헤더를 제외한 각 라인의 키를 추출하여
        /// key → line 전체 문자열 맵을 만든다.
        /// </summary>
        private Dictionary<string, string> BuildKeyToLineMap(string fullText)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);

            if (!_keyContextInitialized || _keyColumnIndexes == null || string.IsNullOrEmpty(fullText))
                return map;

            var lines = fullText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            if (lines.Length == 0)
                return map;

            // 첫 줄은 헤더로 가정
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;

                // 헤더 같은 줄이면 스킵
                if (IsHeaderLine(line)) continue;

                string key = GetKeyFromLine(line);
                if (string.IsNullOrEmpty(key)) continue;

                // 일반적으로 키는 유니크라 가정, 중복이면 첫 번째만 사용
                if (!map.ContainsKey(key))
                    map[key] = line;
            }

            return map;
        }

        /// <summary>
        /// 한 줄 텍스트에서 키 컬럼만 모아 논리 키 문자열을 만든다.
        /// (키 값들 사이에 Unit Separator 사용)
        /// </summary>
        private string GetKeyFromLine(string line)
        {
            if (!_keyContextInitialized || _keyColumnIndexes == null)
                return string.Empty;

            var cols = SplitSafe(line, _currentDelimiter);
            if (cols.Length == 0)
                return string.Empty;

            var parts = new string[_keyColumnIndexes.Length];

            for (int i = 0; i < _keyColumnIndexes.Length; i++)
            {
                int idx = _keyColumnIndexes[i];
                string v = (idx >= 0 && idx < cols.Length) ? (cols[idx] ?? "").Trim() : string.Empty;
                parts[i] = v;
            }

            return string.Join("\u001F", parts); // FileKeyManager에서 쓰는 것과 동일한 구분자 사용
        }

        /// <summary>
        /// 같은 키를 가진 두 줄에서, 키 컬럼을 제외한 컬럼 중
        /// 하나라도 값이 다르면 true (비키 컬럼 변경)
        /// </summary>
        private bool HasNonKeyDiff(string leftText, string rightText)
        {
            if (_keyColumnIndexSet == null)
                return !string.Equals(leftText, rightText, StringComparison.Ordinal);

            var leftCols = SplitSafe(leftText, _currentDelimiter);
            var rightCols = SplitSafe(rightText, _currentDelimiter);
            int maxLen = Math.Max(leftCols.Length, rightCols.Length);

            for (int colIndex = 0; colIndex < maxLen; colIndex++)
            {
                if (_keyColumnIndexSet.Contains(colIndex))
                    continue; // 키 컬럼은 제외

                string lv = colIndex < leftCols.Length ? (leftCols[colIndex] ?? "").Trim() : string.Empty;
                string rv = colIndex < rightCols.Length ? (rightCols[colIndex] ?? "").Trim() : string.Empty;

                if (!string.Equals(lv, rv, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// 키 기준 RowState를 사용해서 DiffPlex가 준 ChangeType을 덮어쓴다.
        /// - 레이아웃(라인 정렬)은 DiffPlex 그대로 사용
        /// - 의미(추가/삭제/수정/유지)는 전부 RowState 기준
        /// </summary>
        private void OverrideChangeTypesByKey(
            DiffPaneModel leftPane,
            DiffPaneModel rightPane,
            Dictionary<string, RowState> rowStates)
        {
            int n = Math.Max(leftPane.Lines.Count, rightPane.Lines.Count);

            for (int i = 0; i < n; i++)
            {
                DiffPiece? lp = (i < leftPane.Lines.Count) ? leftPane.Lines[i] : null;
                DiffPiece? rp = (i < rightPane.Lines.Count) ? rightPane.Lines[i] : null;

                string leftText = lp?.Text ?? string.Empty;
                string rightText = rp?.Text ?? string.Empty;

                // 헤더 라인은 DiffPlex 기본 타입 유지 (보통 첫 줄)
                if (i == 0)
                    continue;

                // 실제 데이터가 아니거나 공백 줄이면 스킵
                if (string.IsNullOrWhiteSpace(leftText) && string.IsNullOrWhiteSpace(rightText))
                    continue;

                // 키 추출
                string leftKey = string.IsNullOrWhiteSpace(leftText) ? string.Empty : GetKeyFromLine(leftText);
                string rightKey = string.IsNullOrWhiteSpace(rightText) ? string.Empty : GetKeyFromLine(rightText);

                bool hasLeftKey = !string.IsNullOrEmpty(leftKey);
                bool hasRightKey = !string.IsNullOrEmpty(rightKey);

                // 1) 양쪽 키가 같으면: 같은 행으로 보고 RowState 따라간다
                if (hasLeftKey && hasRightKey && leftKey == rightKey)
                {
                    if (!rowStates.TryGetValue(leftKey, out var state))
                        continue;

                    switch (state)
                    {
                        case RowState.Unchanged:
                            if (lp != null) lp.Type = ChangeType.Unchanged;
                            if (rp != null) rp.Type = ChangeType.Unchanged;
                            break;

                        case RowState.Modified:
                            if (lp != null) lp.Type = ChangeType.Modified;
                            if (rp != null) rp.Type = ChangeType.Modified;
                            break;

                        case RowState.Deleted:
                            if (lp != null)
                                lp.Type = ChangeType.Deleted;

                            if (rp != null)
                            {
                                rp.Type = ChangeType.Imaginary;

                                if (string.IsNullOrWhiteSpace(rp.Text))
                                {
                                    rp.Text = string.Empty;
                                }
                            }
                            break;

                        case RowState.Added:
                            // 오른쪽에는 실제 데이터가 있어야 함
                            if (rp != null)
                                rp.Type = ChangeType.Inserted;

                            // 왼쪽은 "빈 줄"로 보여야 하니까 Imaginary + Text 비우기
                            if (lp != null)
                            {
                                lp.Type = ChangeType.Imaginary;

                                // 왼쪽에는 실제 데이터가 있는 행이면 지우면 안 되니,
                                // "원래부터 비어있던 줄(공백/탭만 있는 줄)"일 때만 비워주도록 안전하게 처리
                                if (string.IsNullOrWhiteSpace(lp.Text))
                                {
                                    lp.Text = string.Empty;
                                } 
                            }
                            break;
                    }

                    continue;
                }

                // 2) 한쪽에만 키가 있는 경우: 그 키의 RowState를 사용
                if (hasLeftKey && rowStates.TryGetValue(leftKey, out var leftState))
                {
                    switch (leftState)
                    {
                        case RowState.Deleted:
                            if (lp != null) lp.Type = ChangeType.Deleted;
                            if (rp != null) rp.Type = ChangeType.Imaginary;
                            break;
                        case RowState.Modified:
                            if (lp != null) lp.Type = ChangeType.Modified;
                            // 오른쪽은 다른 키와 align 되었을 수 있어서 그대로 두거나, 필요시 조정
                            break;
                        case RowState.Unchanged:
                            if (lp != null) lp.Type = ChangeType.Unchanged;
                            break;
                            // Added는 보통 오른쪽에만 있으므로 여기선 무시
                    }
                }

                if (hasRightKey && rowStates.TryGetValue(rightKey, out var rightState))
                {
                    switch (rightState)
                    {
                        case RowState.Added:
                            if (rp != null) rp.Type = ChangeType.Inserted;
                            if (lp != null) lp.Type = ChangeType.Imaginary;
                            break;
                        case RowState.Modified:
                            if (rp != null) rp.Type = ChangeType.Modified;
                            break;
                        case RowState.Unchanged:
                            if (rp != null) rp.Type = ChangeType.Unchanged;
                            break;
                            // Deleted는 왼쪽에만 있으므로 여기선 무시
                    }
                }
            }
        }


        // ── 키 컨텍스트 초기화 ───────────────────────────────
        private void InitKeyContext(string fullText)
        {
            _keyContextInitialized = false;
            _keyColumns = null;
            _headerIndexMap = null;
            _keyColumnIndexes = null;
            _keyColumnIndexSet = null;

            string? fileName = _fileNameForKeyLookup;
            if (string.IsNullOrEmpty(fileName))
                return; // 파일명을 알 수 없으면 키정보도 못씀 → 타입기반으로만 동작

            var (delimiter, keys) = FileKeyManager.GetFileSettingsOrDefault(fileName, "|");
            _currentDelimiter = string.IsNullOrEmpty(delimiter) ? "|" : delimiter;

            if (keys == null || keys.Count == 0)
                return;

            _keyColumns = keys;

            if (string.IsNullOrEmpty(fullText))
                return;

            // 첫 줄을 헤더로 가정
            string headerLine;
            int idx = fullText.IndexOfAny(new[] { '\r', '\n' });
            if (idx >= 0)
                headerLine = fullText.Substring(0, idx);
            else
                headerLine = fullText;

            if (string.IsNullOrEmpty(headerLine))
                return;

            var headers = SplitSafe(headerLine, _currentDelimiter);
            _headerIndexMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < headers.Length; i++)
            {
                var name = headers[i]?.Trim() ?? $"col{i}";
                if (!_headerIndexMap.ContainsKey(name))
                    _headerIndexMap[name] = i;
            }

            // 키 컬럼 인덱스 계산
            var idxList = new List<int>();
            foreach (var col in _keyColumns)
            {
                if (_headerIndexMap.TryGetValue(col, out int colIdx))
                    idxList.Add(colIdx);
            }

            if (idxList.Count == 0)
            {
                _keyColumns = null;
                _headerIndexMap = null;
                return;
            }

            _keyColumnIndexes = idxList.ToArray();
            _keyColumnIndexSet = new HashSet<int>(_keyColumnIndexes);
            _keyContextInitialized = true;
        }

        private static string[] SplitSafe(string line, string delimiter)
        {
            if (line == null)
                return Array.Empty<string>();

            if (string.IsNullOrEmpty(delimiter))
                return line.Split('|');

            return line.Split(new[] { delimiter }, StringSplitOptions.None);
        }

        private bool IsHeaderLine(string lineText)
        {
            if (_headerIndexMap == null) return false;

            var cols = SplitSafe(lineText, _currentDelimiter);
            if (cols.Length == 0) return false;

            int headerLikeCount = 0;
            foreach (var c in cols)
            {
                var name = (c ?? string.Empty).Trim();
                if (name.Length == 0) continue;
                if (_headerIndexMap.ContainsKey(name))
                    headerLikeCount++;
            }

            return headerLikeCount >= Math.Max(1, cols.Length / 2);
        }

        // ── 키 기준 수정 여부 판단 ──────────────────────────
        private bool IsModifiedLine(DiffPiece? left, DiffPiece? right)
        {
            var lt = left?.Type ?? ChangeType.Unchanged;
            var rt = right?.Type ?? ChangeType.Unchanged;

            bool typeModified = (lt == ChangeType.Modified) || (rt == ChangeType.Modified);

            // 타입 기준으로도 수정이 아니면 바로 false
            if (!typeModified)
                return false;

            // fileKeyConfig에 정보가 없으면 기존 타입 기준으로만 처리
            if (!_keyContextInitialized || _keyColumnIndexSet == null || _headerIndexMap == null)
                return typeModified;

            string leftText = left?.Text ?? string.Empty;
            string rightText = right?.Text ?? string.Empty;

            // 헤더 라인은 키 기반 비교 의미가 적으니, 타입 기준만 따른다
            if (IsHeaderLine(leftText))
                return typeModified;

            var leftCols = SplitSafe(leftText, _currentDelimiter);
            var rightCols = SplitSafe(rightText, _currentDelimiter);
            int maxLen = Math.Max(leftCols.Length, rightCols.Length);

            // 1) 키 컬럼만 먼저 비교: 하나라도 다르면 "수정"으로 안 본다
            foreach (var keyIdx in _keyColumnIndexSet)
            {
                string lv = keyIdx < leftCols.Length ? leftCols[keyIdx].Trim() : string.Empty;
                string rv = keyIdx < rightCols.Length ? rightCols[keyIdx].Trim() : string.Empty;

                if (!string.Equals(lv, rv, StringComparison.Ordinal))
                {
                    // 키 컬럼이 다르면 이 라인은 "다른 행"으로 간주 → 수정 아님(삭제/추가로 처리)
                    return false;
                }
            }

            // 2) 비키 컬럼에서 실제 값 차이가 있는지 확인
            bool nonKeyDiff = false;
            for (int colIndex = 0; colIndex < maxLen; colIndex++)
            {
                if (_keyColumnIndexSet.Contains(colIndex))
                    continue; // 키 컬럼은 이미 위에서 비교 완료

                string lv = colIndex < leftCols.Length ? leftCols[colIndex].Trim() : string.Empty;
                string rv = colIndex < rightCols.Length ? rightCols[colIndex].Trim() : string.Empty;

                if (!string.Equals(lv, rv, StringComparison.Ordinal))
                {
                    nonKeyDiff = true;
                    break;
                }
            }

            // 키는 같고, 비키 컬럼에라도 차이가 있어야 "수정"
            return nonKeyDiff;
        }

        #endregion
    }
}
