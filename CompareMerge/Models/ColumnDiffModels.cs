using DiffPlex.DiffBuilder.Model;

namespace InnoPVManagementSystem.Modules.CompareMerge.Models
{
    public sealed class ColumnChange
    {
        public int ColumnIndex { get; init; }
        public ChangeType ChangeType { get; init; } // Unchanged/Modified/Inserted/Deleted
        public (int start, int length)? LeftSpanInLine { get; init; }
        public (int start, int length)? RightSpanInLine { get; init; }

        // (선택) 문자 단위 내부 변경
        public List<(int start, int length)> LeftInlineRanges { get; } = new();
        public List<(int start, int length)> RightInlineRanges { get; } = new();
    }

    public sealed class LineColumnDiff
    {
        // AvalonEdit: 1-based line number
        public int LeftLineNumber { get; init; }
        public int RightLineNumber { get; init; }
        public List<ColumnChange> Columns { get; } = new();
    }
}