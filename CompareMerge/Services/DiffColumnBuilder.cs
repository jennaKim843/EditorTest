using DiffPlex.DiffBuilder.Model;
using InnoPVManagementSystem.Modules.CompareMerge.Models;

namespace InnoPVManagementSystem.Modules.CompareMerge.Services
{
    internal class DiffColumnBuilder
    //public static class DiffColumnBuilder
    {
        public static List<(int start, int length)> BuildColumnSpans(string line, char delimiter = '|')
        {
            var spans = new List<(int, int)>();
            int start = 0;
            for (int i = 0; i <= line.Length; i++)
            {
                if (i == line.Length || line[i] == delimiter)
                {
                    spans.Add((start, i - start));
                    start = i + 1;
                }
            }
            return spans;
        }

        public static LineColumnDiff BuildLineColumnDiff(DiffPiece left, DiffPiece right, char delimiter = '|')
        {
            var lcd = new LineColumnDiff
            {
                LeftLineNumber = left.Position ?? 0,
                RightLineNumber = right.Position ?? 0
            };

            string l = left.Text ?? string.Empty;
            string r = right.Text ?? string.Empty;

            var lSpans = BuildColumnSpans(l, delimiter);
            var rSpans = BuildColumnSpans(r, delimiter);

            int maxCols = Math.Max(lSpans.Count, rSpans.Count);
            for (int c = 0; c < maxCols; c++)
            {
                (int start, int length)? lspan = c < lSpans.Count ? lSpans[c] : null;
                (int start, int length)? rspan = c < rSpans.Count ? rSpans[c] : null;

                string lc = (lspan is null) ? "" : l.Substring(lspan.Value.start, lspan.Value.length);
                string rc = (rspan is null) ? "" : r.Substring(rspan.Value.start, rspan.Value.length);

                var type =
                    (lspan is null && rspan is not null) ? ChangeType.Inserted :
                    (lspan is not null && rspan is null) ? ChangeType.Deleted :
                    (lc == rc) ? ChangeType.Unchanged : ChangeType.Modified;

                var col = new ColumnChange
                {
                    ColumnIndex = c,
                    ChangeType = type,
                    LeftSpanInLine = lspan,
                    RightSpanInLine = rspan
                };

                // (선택) 문자 단위 내부 변경 범위는 필요 시 채우세요.
                lcd.Columns.Add(col);
            }

            return lcd;
        }
    }
}