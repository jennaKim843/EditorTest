using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace InnoPVManagementSystem.Modules.CompareMerge.Models
{
    /// <summary>
    /// 비교 결과 그리드에 표시되는 1행 모델
    /// - DuplicateKeyMessage가 있으면 요약 카운트를 "-"로 표시(상세 메시지 우선)
    /// - IsComparable=false 이면 "N/A" (키 설정 없음/한쪽에만 존재 등)
    /// </summary>
    public class DiffGridItem
    {

        public int No { get; set; }
        public string FileName { get; set; } = string.Empty;
        public int AddedCount { get; set; }
        public int DeletedCount { get; set; }
        public int ModifiedCount { get; set; }

        public string? File1Path { get; set; }
        public string? File2Path { get; set; }

        public string Message { get; set; } = string.Empty;

        public bool IsConfigMissing { get; set; }
        public bool IsComparable { get; set; } = true;

        public string? DuplicateKeyMessage { get; set; }

        public bool IsFileOnly
            => string.IsNullOrEmpty(File1Path) || string.IsNullOrEmpty(File2Path);

        public string AddedCountText
            => !string.IsNullOrEmpty(DuplicateKeyMessage)
                ? "-"
                : (!IsComparable ? "N/A" : AddedCount.ToString());

        public string DeletedCountText
            => !string.IsNullOrEmpty(DuplicateKeyMessage)
                ? "-"
                : (!IsComparable ? "N/A" : DeletedCount.ToString());

        public string ModifiedCountText
            => !string.IsNullOrEmpty(DuplicateKeyMessage)
                ? "-"
                : (!IsComparable ? "N/A" : ModifiedCount.ToString());

        public bool HasDiff
            => (AddedCount > 0) || (DeletedCount > 0) || (ModifiedCount > 0);
    }
}