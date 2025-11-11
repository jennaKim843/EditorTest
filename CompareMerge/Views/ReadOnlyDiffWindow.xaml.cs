// ReadOnlyDiffWindow.xaml.cs
using InnoPVManagementSystem.Innolinc;
using InnoPVManagementSystem.Common.Utils;  // CsvTableFormatter
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;

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
                var text = File.ReadAllText(path, Encoding.UTF8);
                return ParseCsvToListList(text);
            }

            // 그 외는 빈 데이터
            return new List<List<string>>();
        }

        // 일반 텍스트 파일 로드 (UTF-8)
        private static string LoadRawText(string path)
        {
            return File.ReadAllText(path, Encoding.UTF8);
        }

        // 간단 CSV 파서 (따옴표 이스케이프 "" 지원)
        private static List<List<string>> ParseCsvToListList(string text)
        {
            if (string.IsNullOrEmpty(text)) return new();

            // --- 구분자 자동 감지 ---
            char delim = DetectDelimiter(text); // ',', '|', '\t' 중 결정

            var rows = new List<List<string>>();
            using var sr = new StringReader(text);

            string? line;
            bool inQuote = false;
            var record = new StringBuilder();

            while ((line = sr.ReadLine()) != null)
            {
                if (record.Length > 0) record.Append('\n'); // 멀티라인 필드 지원
                record.Append(line);

                if (EndsRecord(record.ToString(), delim))
                {
                    rows.Add(SplitCsvLine(record.ToString(), delim));
                    record.Clear();
                }
            }

            if (record.Length > 0)
                rows.Add(SplitCsvLine(record.ToString(), delim));

            return rows;

            static bool EndsRecord(string s, char d)
            {
                // 짝수 개의 따옴표면 레코드 종료로 간주
                int q = 0;
                for (int i = 0; i < s.Length; i++)
                    if (s[i] == '"') q += 1;
                return (q % 2) == 0;
            }

            static List<string> SplitCsvLine(string line, char d)
            {
                var fields = new List<string>();
                var f = new StringBuilder();
                bool q = false;

                for (int i = 0; i < line.Length; i++)
                {
                    char c = line[i];

                    if (q)
                    {
                        if (c == '"')
                        {
                            if (i + 1 < line.Length && line[i + 1] == '"')
                            {
                                f.Append('"'); // "" -> "
                                i++;
                            }
                            else q = false;
                        }
                        else f.Append(c);
                    }
                    else
                    {
                        if (c == d)
                        {
                            fields.Add(f.ToString());
                            f.Clear();
                        }
                        else if (c == '"') q = true;
                        else f.Append(c);
                    }
                }
                fields.Add(f.ToString());
                return fields;
            }

            //CSV 파서를 “구분자 자동감지”로 교체
            static char DetectDelimiter(string sample)
            {
                // 첫 5줄에서 후보별 카운트 비교
                var lines = sample.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                                  .Take(5).ToList();
                char[] cand = new[] { ',', '|', '\t' };
                var scores = new Dictionary<char, int>();

                foreach (var d in cand)
                {
                    // “한 줄 내 일관된 분리 수” + “라인 간 분리 수 분산 적음” 가산
                    var counts = lines.Select(l => l.Count(ch => ch == d)).ToList();
                    int sum = counts.Sum();
                    int varPenalty = counts.Max() - counts.Min();
                    scores[d] = sum - varPenalty; // 대충한 휴리스틱
                }

                return scores.OrderByDescending(kv => kv.Value).First().Key;
            }
        }

    }
}
