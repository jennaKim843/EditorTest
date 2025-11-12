using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using InnoPVManagementSystem.Innolinc;
using InnoPVManagementSystem.Common.Utils;  // CsvTableFormatter
using InnoPVManagementSystem.Common.Services;

namespace InnoPVManagementSystem.Modules.CompareMerge.Views
{
    public partial class ReadOnlyDiffWindow : Window
    {
        // 테이블 모드 토글 (원하면 외부에서 주입/바인딩 가능)
        private readonly bool UseTableMode = true;

        public ReadOnlyDiffWindow(string leftPath, string rightPath)
        {
            InitializeComponent();

            // 고정폭 폰트(테이블 정렬 안정성)
            DiffView.LeftEditor.FontFamily = new FontFamily("Consolas");
            DiffView.RightEditor.FontFamily = new FontFamily("Consolas");

            if (UseTableMode && IsTabular(leftPath) && IsTabular(rightPath))
            {
                // 좌/우 둘 다 표 데이터면 -> 같은 폭으로 렌더 (노이즈 최소화)
                var leftData = LoadTabularData(leftPath);
                var rightData = LoadTabularData(rightPath);

                var (lText, rText) = CsvTableFormatter.ToTableTextPair(leftData, rightData);
                DiffView.LeftEditor.Text = lText;
                DiffView.RightEditor.Text = rText;
            }
            else
            {
                // 한쪽만 표이거나, 둘 다 일반 텍스트인 경우
                if (UseTableMode && IsTabular(leftPath))
                {
                    var leftData = LoadTabularData(leftPath);
                    DiffView.LeftEditor.Text = CsvTableFormatter.ToTableText(leftData);
                }
                else
                {
                    DiffView.LeftEditor.Text = LoadRawText(leftPath);
                }

                if (UseTableMode && IsTabular(rightPath))
                {
                    var rightData = LoadTabularData(rightPath);
                    DiffView.RightEditor.Text = CsvTableFormatter.ToTableText(rightData);
                }
                else
                {
                    DiffView.RightEditor.Text = LoadRawText(rightPath);
                }
            }

            DiffView.CompareNow();
            //Title = $"파일 비교 (읽기 전용)  —  L: {System.IO.Path.GetFileName(leftPath)}  |  R: {System.IO.Path.GetFileName(rightPath)}";
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

            if (ext == ".io")
            {
                // 수정 불가한 기존 변환기 그대로 사용
                var result = IIO.IOtoCsv(path);
                return result.csvData; // List<List<string>>
            }

            if (ext == ".csv")
            {
                // CSV 파싱 (따옴표/콤마/개행 처리)
                //var text = File.ReadAllText(path, Encoding.UTF8);
                //return DiffService.ParseCsvToListList(text);
                return ParserUtil.ParsePipeDelimitedCsv(path);
            }

            // 그 외는 빈 데이터
            return new List<List<string>>();
        }

        // 일반 텍스트 파일 로드 (UTF-8)
        private static string LoadRawText(string path)
        {
            return File.ReadAllText(path, Encoding.UTF8);
        }
    }
}
