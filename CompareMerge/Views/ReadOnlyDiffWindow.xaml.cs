using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using InnoPVManagementSystem.Innolinc;
using InnoPVManagementSystem.Common.Utils;
using InnoPVManagementSystem.Common.Services;
using ICSharpCode.AvalonEdit.Document;
using InnoPVManagementSystem.Modules.CompareMerge.Services;

namespace InnoPVManagementSystem.Modules.CompareMerge.Views
{
    public partial class ReadOnlyDiffWindow : Window
    {
        // 테이블 모드 토글 (원하면 외부에서 주입/바인딩 가능)
        private readonly bool UseTableMode = true;

        public ReadOnlyDiffWindow(string leftPath, string rightPath)
        {
            InitializeComponent();

            DiffView.SetFileNameForKeyLookup(leftPath);

            // DiffView(= AvalonEdit)에서 실제 표시 폰트/사이즈를 가져와서 폭 계산에 사용
            var fontFamily = DiffView.LeftEditor?.FontFamily ?? DiffView.RightEditor?.FontFamily ?? new FontFamily("Consolas");
            var fontSize = (DiffView.LeftEditor?.FontSize > 0 ? DiffView.LeftEditor.FontSize : DiffView.RightEditor?.FontSize) ?? 12.0;

            string leftText;
            string rightText;

            if (UseTableMode && IsTabular(leftPath) && IsTabular(rightPath))
            {
                var leftData = LoadTabularData(leftPath);
                var rightData = LoadTabularData(rightPath);

                var pair = CsvTableFormatter.ToTableTextPair(leftData, rightData, fontFamily, fontSize);
                leftText = pair.left;
                rightText = pair.right;
            }
            else
            {
                if (UseTableMode && IsTabular(leftPath))
                {
                    var leftData = LoadTabularData(leftPath);
                    leftText = CsvTableFormatter.ToTableText(leftData, fontFamily, fontSize);
                }
                else
                {
                    leftText = LoadRawText(leftPath);
                }

                if (UseTableMode && IsTabular(rightPath))
                {
                    var rightData = LoadTabularData(rightPath);
                    rightText = CsvTableFormatter.ToTableText(rightData, fontFamily, fontSize);
                }
                else
                {
                    rightText = LoadRawText(rightPath);
                }
            }

            DiffView.LeftEditor.Document = new TextDocument(leftText ?? string.Empty);
            DiffView.RightEditor.Document = new TextDocument(rightText ?? string.Empty);

            Loaded += (s, e) => DiffView.CompareNow();

            Title = $"파일 비교 (읽기 전용)  —  L: {System.IO.Path.GetFileName(leftPath)}  |  R: {System.IO.Path.GetFileName(rightPath)}";
        }


        // --- Helpers --------------------------------------------------------

        // CSV/IO 같은 "표형식" 파일 여부
        private static bool IsTabular(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".csv" || ext == ".io";
        }

        // 표형식 데이터를 List<List<string>>로 로딩
        private static List<List<string>> LoadTabularData(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            List<List<string>> rows;

            if (ext == ".io")
            {
                // 수정 불가한 기존 변환기 그대로 사용
                var result = IIO.IOtoCsv(path);
                rows = result.csvData; // List<List<string>>
            }else if (ext == ".csv")
            {
                rows = ParserUtil.ParsePipeDelimitedCsv(path);
            }
            else
            {
                // 그 외는 빈 데이터
                rows = new List<List<string>>();
            }

            // path: 전체 경로여도 됨 (TryGetFileSettings 내부에서 다시 Path.GetFileName 처리함)
            if (FileKeyManager.TryGetFileSettings(path, out var delimiter, out var keyColumns)
                && keyColumns != null
                && keyColumns.Count > 0)
            {
                rows = SortRowsByKeys(rows, keyColumns);
            }

            return rows;
        }

        // 일반 텍스트 파일 로드 (UTF-8)
        private static string LoadRawText(string path)
        {
            return File.ReadAllText(path, Encoding.UTF8);
        }

        private static List<List<string>> SortRowsByKeys( List<List<string>> rows, IReadOnlyList<string> keyColumns)
        {
            if (rows == null || rows.Count <= 1)
                return rows;

            var header = rows[0];
            var dataRows = rows.Skip(1).ToList();

            // 키 컬럼 인덱스 매핑 (헤더 순서기준)
            var headerMap = header
                .Select((col, idx) => new { col, idx })
                .ToDictionary(x => x.col.Trim(), x => x.idx, StringComparer.OrdinalIgnoreCase);

            // 실제 정렬
            dataRows = dataRows
                .OrderBy(r => r, Comparer<List<string>>.Create((x, y) =>
                    CompareRowKeys(x, y, keyColumns, headerMap)))
                .ToList();

            // 다시 헤더 + 데이터 결합
            var result = new List<List<string>> { header };
            result.AddRange(dataRows);
            return result;
        }

        private static int CompareRowKeys( List<string> x, List<string> y, IReadOnlyList<string> keyColumns, Dictionary<string, int> headerMap)
        {
            for (int i = 0; i < keyColumns.Count; i++)
            {
                string col = keyColumns[i];

                // 헤더에서 해당 키 컬럼의 인덱스 찾기
                if (!headerMap.TryGetValue(col, out int idx))
                    continue;

                string xv = idx < x.Count ? x[idx]?.Trim() ?? string.Empty : string.Empty;
                string yv = idx < y.Count ? y[idx]?.Trim() ?? string.Empty : string.Empty;

                int result;

                // 값이 날짜처럼 해석되면 날짜 비교, 아니면 문자열 비교
                DateTime? dx = DateUtil.ParseAnyYmdOrNull(xv);
                DateTime? dy = DateUtil.ParseAnyYmdOrNull(yv);

                if (dx is not null || dy is not null)
                {
                    // 둘 중 하나라도 날짜로 해석되면 날짜 기준 비교
                    result = Nullable.Compare(dx, dy);
                }
                else
                {
                    // 일반 문자열 비교
                    result = string.CompareOrdinal(xv, yv);
                }

                if (result != 0)
                    return result;
            }

            // Composite Key가 완전히 동일하면 → 행 전체 문자열로 정렬
            string fullLineX = string.Join("|", x ?? new List<string>());
            string fullLineY = string.Join("|", y ?? new List<string>());

            return string.CompareOrdinal(fullLineX, fullLineY);
        }


    }
}
