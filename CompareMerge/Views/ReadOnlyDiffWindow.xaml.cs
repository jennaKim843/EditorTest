using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using InnoPVManagementSystem.Innolinc;
using InnoPVManagementSystem.Common.Utils;  // CsvTableFormatter
using InnoPVManagementSystem.Common.Services;
using ICSharpCode.AvalonEdit.Document;

namespace InnoPVManagementSystem.Modules.CompareMerge.Views
{
    public partial class ReadOnlyDiffWindow : Window
    {
        // 테이블 모드 토글 (원하면 외부에서 주입/바인딩 가능)
        private readonly bool UseTableMode = true;

        //public ReadOnlyDiffWindow(string leftPath, string rightPath)
        //{
        //    InitializeComponent();

        //    DiffView.SetFileNameForKeyLookup(leftPath);

        //    // 고정폭 폰트(테이블 정렬 안정성)
        //    DiffView.LeftEditor.FontFamily = new FontFamily("Consolas");
        //    DiffView.RightEditor.FontFamily = new FontFamily("Consolas");

        //    if (UseTableMode && IsTabular(leftPath) && IsTabular(rightPath))
        //    {
        //        // 좌/우 둘 다 표 데이터면 -> 같은 폭으로 렌더 (노이즈 최소화)
        //        var leftData = LoadTabularData(leftPath);
        //        var rightData = LoadTabularData(rightPath);

        //        var (lText, rText) = CsvTableFormatter.ToTableTextPair(leftData, rightData);
        //        DiffView.LeftEditor.Text = lText;
        //        DiffView.RightEditor.Text = rText;
        //    }
        //    else
        //    {
        //        // 한쪽만 표이거나, 둘 다 일반 텍스트인 경우
        //        if (UseTableMode && IsTabular(leftPath))
        //        {
        //            var leftData = LoadTabularData(leftPath);
        //            DiffView.LeftEditor.Text = CsvTableFormatter.ToTableText(leftData);
        //        }
        //        else
        //        {
        //            DiffView.LeftEditor.Text = LoadRawText(leftPath);
        //        }

        //        if (UseTableMode && IsTabular(rightPath))
        //        {
        //            var rightData = LoadTabularData(rightPath);
        //            DiffView.RightEditor.Text = CsvTableFormatter.ToTableText(rightData);
        //        }
        //        else
        //        {
        //            DiffView.RightEditor.Text = LoadRawText(rightPath);
        //        }
        //    }

        //    DiffView.CompareNow();
        //    Title = $"파일 비교 (읽기 전용)  —  L: {System.IO.Path.GetFileName(leftPath)}  |  R: {System.IO.Path.GetFileName(rightPath)}";
        //}
        public ReadOnlyDiffWindow(string leftPath, string rightPath)
        {
            InitializeComponent();

            DiffView.SetFileNameForKeyLookup(leftPath);

            // 고정폭 폰트
            DiffView.LeftEditor.FontFamily = new FontFamily("Consolas");
            DiffView.RightEditor.FontFamily = new FontFamily("Consolas");

            string leftText;
            string rightText;

            if (UseTableMode && IsTabular(leftPath) && IsTabular(rightPath))
            {
                var leftData = LoadTabularData(leftPath);
                var rightData = LoadTabularData(rightPath);

                var pair = CsvTableFormatter.ToTableTextPair(leftData, rightData);
                leftText = pair.left;
                rightText = pair.right;
            }
            else
            {
                if (UseTableMode && IsTabular(leftPath))
                {
                    var leftData = LoadTabularData(leftPath);
                    leftText = CsvTableFormatter.ToTableText(leftData);
                }
                else
                {
                    leftText = LoadRawText(leftPath);
                }

                if (UseTableMode && IsTabular(rightPath))
                {
                    var rightData = LoadTabularData(rightPath);
                    rightText = CsvTableFormatter.ToTableText(rightData);
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

        private static List<List<string>> SortRowsByKeys(　List<List<string>> rows,　IReadOnlyList<string> keyColumns)
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

        // 날짜로 취급할 키 컬럼 이름
        private static readonly HashSet<string> DateKeyNames =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "valid_date",
                "expiration_date"
                // 추가
            };

        private static int CompareRowKeys( List<string> x, List<string> y, IReadOnlyList<string> keyColumns, Dictionary<string, int> headerMap)
        {
            for (int i = 0; i < keyColumns.Count; i++)
            {
                string col = keyColumns[i];

                // 헤더에서 해당 키 컬럼의 인덱스 찾기
                if (!headerMap.TryGetValue(col, out int idx))
                    continue;

                string xv = idx < x.Count ? x[idx].Trim() : "";
                string yv = idx < y.Count ? y[idx].Trim() : "";

                int result;

                // 날짜 컬럼 비교
                if (DateKeyNames.Contains(col))
                {
                    DateTime? dx = DateUtil.ParseAnyYmdOrNull(xv);
                    DateTime? dy = DateUtil.ParseAnyYmdOrNull(yv);

                    if (dx is null && dy is null)
                        result = string.CompareOrdinal(xv, yv);
                    else
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

            // 모든 키값이 동일
            return 0;
        }


    }
}
