using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace InnoPVManagementSystem.Common.Utils
{
    public static class CsvTableFormatter
    {
        // 1) 단일 데이터 → 테이블 텍스트
        public static string ToTableText(List<List<string>> data)
        {
            if (data == null || data.Count == 0) return string.Empty;
            var widths = CalcColWidths(data);
            return BuildTable(data, widths);
        }

        // 2) 좌/우 데이터 함께 → 동일한 폭으로 테이블 텍스트 (Diff 노이즈 최소화)
        public static (string left, string right) ToTableTextPair(List<List<string>> left, List<List<string>> right)
        {
            var widths = MergeWidths(CalcColWidths(left), CalcColWidths(right));
            return (BuildTable(left, widths), BuildTable(right, widths));
        }

        private static int[] CalcColWidths(List<List<string>> data)
        {
            int colCount = data.Max(r => r?.Count ?? 0);
            var widths = new int[colCount];
            foreach (var row in data)
                for (int i = 0; i < (row?.Count ?? 0); i++)
                    widths[i] = Math.Max(widths[i], (row![i] ?? "").Length);
            return widths;
        }

        private static int[] MergeWidths(int[] a, int[] b)
        {
            int n = Math.Max(a.Length, b.Length);
            var w = new int[n];
            for (int i = 0; i < n; i++)
            {
                int la = i < a.Length ? a[i] : 0;
                int lb = i < b.Length ? b[i] : 0;
                w[i] = Math.Max(la, lb);
            }
            return w;
        }

        private static string BuildTable(List<List<string>> data, int[] widths)
        {
            var sb = new StringBuilder();
            if (data.Count == 0) return "";

            // 헤더
            sb.AppendLine(BuildRow(data[0], widths));
            sb.AppendLine(BuildSeparator(widths));

            // 바디
            for (int i = 1; i < data.Count; i++)
                sb.AppendLine(BuildRow(data[i], widths));

            return sb.ToString();
        }

        private static string BuildRow(List<string> row, int[] widths)
        {
            var parts = new List<string>(widths.Length);
            for (int i = 0; i < widths.Length; i++)
            {
                string v = (i < row.Count ? row[i] : "") ?? "";
                parts.Add(" " + v.PadRight(widths[i]) + " ");
            }
            return "| " + string.Join(" | ", parts) + " |";
        }

        private static string BuildSeparator(int[] widths)
        {
            var segs = widths.Select(w => new string('-', w + 2));
            return "|-" + string.Join("-|-", segs) + "-|";
        }
    }
}
