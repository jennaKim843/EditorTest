using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Search;
using InnoPVManagementSystem.Common.IO;
using InnoPVManagementSystem.Modules.CompareMerge.Services;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace InnoPVManagementSystem.Modules.CompareMerge.Views
{
    /// <summary>
    /// ReadOnlyDiffView 본체(UI/이벤트/파이프라인) 구현.
    /// (Diff 실행 → 표시 텍스트 구성 → 라인번호/컬러/네비게이션/상태 갱신까지 담당)
    /// </summary>
    public partial class ReadOnlyDiffView : UserControl
    {
        // ------------ 상태 필드 ------------

        /// <summary>키 기반 비교에 사용할 파일명(경로에서 파일명만 보관).</summary>
        private string? _fileNameForKeyLookup;

        /// <summary>스크롤 동기화 시 재귀 루프 방지 플래그.</summary>
        private bool _syncing;

        /// <summary>좌/우 커스텀 라인번호 마진(기본 LineNumbers 대신 사용).</summary>
        private DiffLineNumberMargin? _leftNumMargin;
        private DiffLineNumberMargin? _rightNumMargin;

        /// <summary>변경 블록 시작점 목록(다음/이전 이동 앵커).</summary>
        private readonly List<(int leftLine, int rightLine)> _changeAnchors = new();

        /// <summary>현재 선택된 변경 블록 인덱스(앵커 내 위치).</summary>
        private int _currentChangeIndex = -1;

        /// <summary>현재 변경 라인 강조 컬러라이저(좌/우).</summary>
        private CurrentChangeColorizer? _leftChangeMarker;
        private CurrentChangeColorizer? _rightChangeMarker;

        /// <summary>Pipe 모드에서 수정 컬럼 박스 하이라이트(좌/우).</summary>
        private ColumnHighlightRenderer? _leftColHighlight;
        private ColumnHighlightRenderer? _rightColHighlight;

        /// <summary>인라인 비교 모드(없음/단어/문자/파이프 컬럼).</summary>
        public enum IntraLineMode { None, Word, Character, Pipe }

        // ------------ 생성자 ------------

        /// <summary>
        /// 컨트롤 초기화.
        /// (폰트/에디터 옵션/마진/컬러라이저/검색패널/컬럼렌더러/스크롤동기화 연결)
        /// </summary>
        public ReadOnlyDiffView()
        {
            InitializeComponent();

            ApplyDiffFont(); // (Embedded 폰트 적용)

            // 기본 라인번호는 끄고 커스텀 마진 사용
            LeftEditor.ShowLineNumbers = false;
            RightEditor.ShowLineNumbers = false;

            SetupEditor(LeftEditor);
            SetupEditor(RightEditor);

            // 커스텀 라인번호 마진 삽입
            _leftNumMargin = new DiffLineNumberMargin();
            _rightNumMargin = new DiffLineNumberMargin();
            LeftEditor.TextArea.LeftMargins.Insert(0, _leftNumMargin);
            RightEditor.TextArea.LeftMargins.Insert(0, _rightNumMargin);

            // 현재 변경 라인 강조 컬러라이저 등록
            _leftChangeMarker = new CurrentChangeColorizer();
            _rightChangeMarker = new CurrentChangeColorizer();
            LeftEditor.TextArea.TextView.LineTransformers.Add(_leftChangeMarker);
            RightEditor.TextArea.TextView.LineTransformers.Add(_rightChangeMarker);

            // Ctrl+F 검색 패널 설치
            InitSearchPanels();

            // Pipe 컬럼 박스 하이라이트 렌더러 설치
            InitColumnHighlightRenderers();

            // 스크롤 동기화 + 헤더 오버레이 위치 보정
            LeftEditor.TextArea.TextView.ScrollOffsetChanged += (_, __) =>
            {
                SyncScroll(LeftEditor, RightEditor);
                UpdateHeaderOverlay(LeftEditor, LeftHeaderOverlay, LeftHeaderText, _leftNumMargin?.ActualWidth ?? 0);
            };

            RightEditor.TextArea.TextView.ScrollOffsetChanged += (_, __) =>
            {
                SyncScroll(RightEditor, LeftEditor);
                UpdateHeaderOverlay(RightEditor, RightHeaderOverlay, RightHeaderText, _rightNumMargin?.ActualWidth ?? 0);
            };
        }

        // ------------ 폰트 / 검색 / 렌더러 초기화 ------------

        /// <summary>
        /// Diff 뷰에 사용할 폰트를 적용한다.
        /// (EmbeddedResource 폰트를 추출해 FontFamily로 로드)
        /// </summary>
        private void ApplyDiffFont()
        {
            var font = LoadEmbeddedFontFamily(
                "NanumGothicCoding",
                "InnoPVManagementSystem.Resources.Font.NanumGothicCoding.ttf",
                "InnoPVManagementSystem.Resources.Font.NanumGothicCodingBold.ttf"
            );

            LeftEditor.FontFamily = font;
            RightEditor.FontFamily = font;
            LeftHeaderText.FontFamily = font;
            RightHeaderText.FontFamily = font;
        }

        /// <summary>
        /// Ctrl+F 검색 패널(SearchPanel)을 좌/우 에디터에 설치한다.
        /// (헤더 오버레이에 가리지 않도록 마진 조정)
        /// </summary>
        private void InitSearchPanels()
        {
            var leftPanel = SearchPanel.Install(LeftEditor.TextArea);
            var rightPanel = SearchPanel.Install(RightEditor.TextArea);

            leftPanel.Margin = new Thickness(0, 18, 8, 0);
            rightPanel.Margin = new Thickness(0, 18, 8, 0);
        }

        /// <summary>
        /// Pipe 모드 수정 컬럼을 박스(배경+테두리)로 표시하는 렌더러를 설치한다.
        /// (KnownLayer.Selection 레이어 사용)
        /// </summary>
        private void InitColumnHighlightRenderers()
        {
            var fill = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#33EF6C00")));
            var penBrush = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFEF6C00")));
            var pen = new Pen(penBrush, 1.5) { LineJoin = PenLineJoin.Round };
            Freeze(pen);

            _leftColHighlight = new ColumnHighlightRenderer(fill, pen);
            _rightColHighlight = new ColumnHighlightRenderer(fill, pen);

            LeftEditor.TextArea.TextView.BackgroundRenderers.Add(_leftColHighlight);
            RightEditor.TextArea.TextView.BackgroundRenderers.Add(_rightColHighlight);
        }

        /// <summary>
        /// AvalonEdit 공통 옵션 초기화.
        /// (읽기 전용, 공백/탭 표시 끄기, 링크 기능 비활성화)
        /// </summary>
        private static void SetupEditor(TextEditor ed)
        {
            ed.IsReadOnly = true;
            ed.Options.ConvertTabsToSpaces = true;
            ed.Options.ShowSpaces = false;
            ed.Options.ShowTabs = false;
            ed.Options.EnableHyperlinks = false;
            ed.Options.EnableEmailHyperlinks = false;
        }

        /// <summary>
        /// Freezable 객체를 Freeze하여 렌더 성능/안정성을 확보한다.
        /// </summary>
        internal static T Freeze<T>(T f) where T : Freezable
        {
            if (f.CanFreeze) f.Freeze();
            return f;
        }

        // ------------ 헤더 오버레이 ------------

        /// <summary>
        /// 스크롤 위치에 따라 고정 헤더 오버레이를 표시/숨김 처리하고
        /// 가로 스크롤 + 라인번호 마진 폭에 맞춰 X 위치를 보정한다.
        /// </summary>
        private static void UpdateHeaderOverlay(TextEditor editor, Border headerBorder, TextBlock headerText, double lineNumberMarginWidth)
        {
            var tv = editor.TextArea.TextView;

            headerBorder.Visibility = tv.ScrollOffset.Y <= 0.1
                ? Visibility.Collapsed
                : Visibility.Visible;

            if (headerText.RenderTransform is not TranslateTransform tt)
            {
                tt = new TranslateTransform();
                headerText.RenderTransform = tt;
            }

            tt.X = lineNumberMarginWidth - tv.ScrollOffset.X;
        }

        /// <summary>
        /// 문서의 첫 줄(헤더)을 읽어 헤더 텍스트에 세팅한다.
        /// (모든 파일 1행이 헤더라는 전제)
        /// </summary>
        private void SetHeaderFromDocument(TextEditor editor, TextBlock headerTextBlock)
        {
            if (editor.Document == null || editor.Document.LineCount == 0)
            {
                headerTextBlock.Text = string.Empty;
                return;
            }

            var firstLine = editor.Document.GetLineByNumber(1);
            headerTextBlock.Text = editor.Document.GetText(firstLine);
        }

        // ------------ 공용 설정 ------------

        /// <summary>
        /// 키 기반 비교에 사용할 파일명을 설정한다.
        /// (전체 경로가 들어와도 파일명만 유지)
        /// </summary>
        public void SetFileNameForKeyLookup(string filePathOrName)
        {
            _fileNameForKeyLookup = string.IsNullOrWhiteSpace(filePathOrName)
                ? null
                : Path.GetFileName(filePathOrName);
        }

        // ------------ 스크롤 동기화 ------------

        /// <summary>
        /// src 스크롤에 맞춰 dst 스크롤을 동기화한다.
        /// (세로는 높이 차이를 고려해 비율 기반, 가로는 그대로 복사)
        /// </summary>
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

        /// <summary>상태 텍스트(추가/삭제/수정 건수 등).</summary>
        public string StatusText
        {
            get => (string)GetValue(StatusTextProperty);
            set => SetValue(StatusTextProperty, value);
        }
        public static readonly DependencyProperty StatusTextProperty =
            DependencyProperty.Register(nameof(StatusText), typeof(string), typeof(ReadOnlyDiffView),
                new PropertyMetadata(""));

        /// <summary>인라인 비교 모드(단어/문자/파이프).</summary>
        public IntraLineMode Mode
        {
            get => (IntraLineMode)GetValue(ModeProperty);
            set => SetValue(ModeProperty, value);
        }
        public static readonly DependencyProperty ModeProperty =
            DependencyProperty.Register(nameof(Mode), typeof(IntraLineMode), typeof(ReadOnlyDiffView),
                new PropertyMetadata(IntraLineMode.Pipe));

        /// <summary>추가 행 수.</summary>
        public int AddedRows
        {
            get => (int)GetValue(AddedRowsProperty);
            set => SetValue(AddedRowsProperty, value);
        }
        public static readonly DependencyProperty AddedRowsProperty =
            DependencyProperty.Register(nameof(AddedRows), typeof(int), typeof(ReadOnlyDiffView),
                new PropertyMetadata(0));

        /// <summary>삭제 행 수.</summary>
        public int DeletedRows
        {
            get => (int)GetValue(DeletedRowsProperty);
            set => SetValue(DeletedRowsProperty, value);
        }
        public static readonly DependencyProperty DeletedRowsProperty =
            DependencyProperty.Register(nameof(DeletedRows), typeof(int), typeof(ReadOnlyDiffView),
                new PropertyMetadata(0));

        /// <summary>수정 행 수.</summary>
        public int ModifiedRows
        {
            get => (int)GetValue(ModifiedRowsProperty);
            set => SetValue(ModifiedRowsProperty, value);
        }
        public static readonly DependencyProperty ModifiedRowsProperty =
            DependencyProperty.Register(nameof(ModifiedRows), typeof(int), typeof(ReadOnlyDiffView),
                new PropertyMetadata(0));

        /// <summary>외부 호출용: 비교 실행 진입점.</summary>
        public void CompareNow() => RunCompare();

        /// <summary>버튼 핸들러: 비교 실행.</summary>
        private void OnCompare(object sender, RoutedEventArgs e) => RunCompare();

        /// <summary>버튼 핸들러: 다음 변경 위치로 이동.</summary>
        private void OnNextChange(object? sender, RoutedEventArgs e) => GoToChange(+1);

        /// <summary>버튼 핸들러: 이전 변경 위치로 이동.</summary>
        private void OnPrevChange(object? sender, RoutedEventArgs e) => GoToChange(-1);

        // ------------ Compare 파이프라인 ------------

        /// <summary>
        /// 현재 텍스트 기준으로 Diff를 수행하고 UI를 갱신한다.
        /// (라인 정렬 → 표시 텍스트 구성 → 라인번호/인라인/컬러/상태/앵커/헤더 순으로 갱신)
        /// </summary>
        public void RunCompare()
        {
            var leftRaw = LeftEditor.Text ?? string.Empty;
            var rightRaw = RightEditor.Text ?? string.Empty;

            var differ = new Differ();

            // 키 컨텍스트 생성(fileKeyConfig 기반, 없으면 null)
            var keyCtx = ReadOnlyDiffCore.TryCreateKeyContext(_fileNameForKeyLookup, leftRaw);

            IList<DiffPiece> leftLines;
            IList<DiffPiece> rightLines;

            // 키 정렬 기반 또는 DiffPlex 기본 모델 선택
            if (keyCtx != null)
            {
                (leftLines, rightLines) = ReadOnlyDiffCore.BuildKeyAlignedLines(keyCtx, leftRaw, rightRaw);
            }
            else
            {
                var side = new SideBySideDiffBuilder(differ).BuildDiffModel(leftRaw, rightRaw);
                leftLines = side.OldText.Lines;
                rightLines = side.NewText.Lines;
            }

            // 에디터 표시 텍스트 구성(빈 라인은 " "로 보정)
            string leftDisplay = ReadOnlyDiffCore.BuildAligned(leftLines, leftRaw.Length);
            string rightDisplay = ReadOnlyDiffCore.BuildAligned(rightLines, rightRaw.Length);

            LeftEditor.Text = leftDisplay;
            RightEditor.Text = rightDisplay;

            // 라인번호 갱신
            UpdateLineNumberMargins(leftLines, rightLines);

            // 인라인(subpieces) 구성(모드에 따라 word/char/pipe)
            var (subsLeft, subsRight) = ReadOnlyDiffCore.BuildInlineSubPieces(
                mode: Mode,
                leftLines: leftLines,
                rightLines: rightLines,
                leftDisplay: leftDisplay,
                rightDisplay: rightDisplay,
                differ: differ);

            // 컬러라이저 + Pipe 컬럼 박스 하이라이트 갱신
            ApplyColorizers(leftLines, rightLines, subsLeft, subsRight);

            // 상태(추가/삭제/수정) 계산/반영
            UpdateStatus(leftLines, rightLines);

            // 변경 앵커 재구성(네비게이션용)
            RebuildChangeAnchors(leftLines, rightLines);

            // 첫 변경 위치로 자동 이동(있으면)
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
            }

            // 현재행 강조용 라인 타입 목록 세팅
            _leftChangeMarker?.SetLineTypes(leftLines);
            _rightChangeMarker?.SetLineTypes(rightLines);

            // 헤더 오버레이 텍스트 갱신
            SetHeaderFromDocument(LeftEditor, LeftHeaderText);
            SetHeaderFromDocument(RightEditor, RightHeaderText);

            // 최종 redraw
            LeftEditor.TextArea.TextView.Redraw();
            RightEditor.TextArea.TextView.Redraw();
        }

        /// <summary>
        /// 라인/인라인 컬러라이저를 적용하고, Pipe 모드면 컬럼 박스 하이라이트도 갱신한다.
        /// (기존 WordAwareColorizer 제거 후 재등록)
        /// </summary>
        private void ApplyColorizers(
            IList<DiffPiece> leftLines,
            IList<DiffPiece> rightLines,
            List<IList<DiffPiece>>? subsLeft,
            List<IList<DiffPiece>>? subsRight)
        {
            var lt = LeftEditor.TextArea.TextView.LineTransformers;
            var rt = RightEditor.TextArea.TextView.LineTransformers;

            RemoveTransformers<WordAwareColorizer>(lt);
            RemoveTransformers<WordAwareColorizer>(rt);

            lt.Add(new WordAwareColorizer(leftLines, Mode, subsLeft, line => _leftChangeMarker?.IsCurrentLine(line) ?? false));
            rt.Add(new WordAwareColorizer(rightLines, Mode, subsRight, line => _rightChangeMarker?.IsCurrentLine(line) ?? false));

            // Pipe 모드에서만 Modified 토큰 세그먼트 계산 후 렌더러에 전달
            bool canPipe = Mode == IntraLineMode.Pipe && subsLeft != null && subsRight != null;

            _leftColHighlight?.Update(
                canPipe ? BuildPipeModifiedTokenSegments(LeftEditor.Document, subsLeft!) : Array.Empty<ICSharpCode.AvalonEdit.Document.TextSegment>());

            _rightColHighlight?.Update(
                canPipe ? BuildPipeModifiedTokenSegments(RightEditor.Document, subsRight!) : Array.Empty<ICSharpCode.AvalonEdit.Document.TextSegment>());
        }

        /// <summary>
        /// LineTransformers 목록에서 특정 타입 트랜스포머를 제거한다.
        /// (중복 등록 방지)
        /// </summary>
        private static void RemoveTransformers<T>(IList<IVisualLineTransformer> list)
        {
            for (int i = list.Count - 1; i >= 0; i--)
                if (list[i] is T) list.RemoveAt(i);
        }

        /// <summary>
        /// diff 결과 기반으로 좌/우 커스텀 라인번호 마진을 갱신한다.
        /// </summary>
        private void UpdateLineNumberMargins(IList<DiffPiece> leftLines, IList<DiffPiece> rightLines)
        {
            var leftNums = ReadOnlyDiffCore.BuildOriginalLineNumbers(leftLines);
            var rightNums = ReadOnlyDiffCore.BuildOriginalLineNumbers(rightLines);

            _leftNumMargin?.Update(leftNums, ReadOnlyDiffCore.DigitCountFromMax(leftNums));
            _rightNumMargin?.Update(rightNums, ReadOnlyDiffCore.DigitCountFromMax(rightNums));
        }

        // ------------ 앵커 / 네비게이션 ------------

        /// <summary>변경 타입 여부(Inserted/Deleted/Modified).</summary>
        private static bool IsChanged(ChangeType t)
            => t == ChangeType.Inserted || t == ChangeType.Deleted || t == ChangeType.Modified;

        /// <summary>
        /// 변경 블록 시작점을 추출하여 앵커 목록을 재구성한다.
        /// (연속 변경 구간의 첫 라인만 앵커로 등록)
        /// </summary>
        private void RebuildChangeAnchors(IList<DiffPiece> leftLines, IList<DiffPiece> rightLines)
        {
            _changeAnchors.Clear();
            int count = Math.Max(leftLines.Count, rightLines.Count);
            bool inBlock = false;

            for (int i = 0; i < count; i++)
            {
                var lt = i < leftLines.Count ? leftLines[i].Type : ChangeType.Imaginary;
                var rt = i < rightLines.Count ? rightLines[i].Type : ChangeType.Imaginary;
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

        /// <summary>
        /// direction(+1/-1)에 따라 다음/이전 변경 앵커로 이동한다.
        /// (범위 벗어나면 안내 메시지)
        /// </summary>
        private void GoToChange(int direction)
        {
            if (_changeAnchors.Count == 0) return;

            int newIndex = _currentChangeIndex + direction;

            if (newIndex < 0)
            {
                MessageBox.Show("첫 번째 변경 위치입니다.", "안내", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (newIndex >= _changeAnchors.Count)
            {
                MessageBox.Show("마지막 변경 위치입니다.", "안내", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _currentChangeIndex = newIndex;
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

            // 현재 변경 라인 강조 갱신
            _leftChangeMarker?.Update(l);
            _rightChangeMarker?.Update(r);

            LeftEditor.TextArea.TextView.Redraw();
            RightEditor.TextArea.TextView.Redraw();
        }

        /// <summary>
        /// 지정 라인으로 스크롤 + 캐럿 이동(가시성 확보).
        /// (BringCaretToView 포함)
        /// </summary>
        private static void ScrollToLine(TextEditor ed, int line)
        {
            if (ed.Document == null || ed.Document.LineCount == 0) return;
            int target = Math.Max(1, Math.Min(line, ed.Document.LineCount));

            ed.TextArea.Caret.Line = target;
            ed.TextArea.Caret.Column = 1;

            ed.ScrollToLine(target);
            ed.TextArea.Caret.BringCaretToView();
        }

        // ------------ 상태 ------------

        /// <summary>
        /// diff 결과를 바탕으로 추가/삭제/수정 건수를 계산하고 상태 텍스트를 갱신한다.
        /// </summary>
        private void UpdateStatus(IList<DiffPiece> leftLines, IList<DiffPiece> rightLines)
        {
            int added = ReadOnlyDiffCore.CountType(rightLines, ChangeType.Inserted);
            int deleted = ReadOnlyDiffCore.CountType(leftLines, ChangeType.Deleted);

            int modified = 0;
            int n = Math.Max(leftLines.Count, rightLines.Count);

            for (int i = 0; i < n; i++)
            {
                DiffPiece? lp = i < leftLines.Count ? leftLines[i] : null;
                DiffPiece? rp = i < rightLines.Count ? rightLines[i] : null;

                if (ReadOnlyDiffCore.IsModifiedLine(lp, rp))
                    modified++;
            }

            AddedRows = added;
            DeletedRows = deleted;
            ModifiedRows = modified;

            StatusText = $"추가 {added} / 삭제 {deleted} / 수정 {modified}";
        }

        // ------------ Embedded Font ------------

        /// <summary>Diff 폰트 캐시(중복 로드 방지).</summary>
        private static FontFamily? _cachedDiffFont;

        /// <summary>
        /// EmbeddedResource 폰트를 디스크로 추출한 뒤 FontFamily로 로드한다.
        /// (AvalonEdit에서 안정적으로 폰트 적용하기 위한 방식)
        /// </summary>
        private static FontFamily LoadEmbeddedFontFamily(string familyName, params string[] embeddedResourceNames)
        {
            if (_cachedDiffFont != null)
                return _cachedDiffFont;

            var dir = FileUtil.GetInnoLincResourcePath("Fonts");
            Directory.CreateDirectory(dir);

            ExtractIfMissing(dir, "NanumGothicCoding.ttf", embeddedResourceNames[0]);

            if (embeddedResourceNames.Length > 1)
                ExtractIfMissing(dir, "NanumGothicCodingBold.ttf", embeddedResourceNames[1]);

            _cachedDiffFont = new FontFamily(
                new Uri(dir + Path.DirectorySeparatorChar),
                $"./#{familyName}");

            return _cachedDiffFont;
        }

        /// <summary>
        /// 리소스 폰트를 파일로 추출한다(없거나 0바이트면 재추출).
        /// </summary>
        private static void ExtractIfMissing(string dir, string fileName, string resourceName)
        {
            var path = Path.Combine(dir, fileName);

            if (File.Exists(path) && new FileInfo(path).Length > 0)
                return;

            var asm = Assembly.GetExecutingAssembly();
            using var s = asm.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"EmbeddedResource not found: {resourceName}");

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            s.CopyTo(fs);
        }
    }
}
