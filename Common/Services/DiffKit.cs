// Common/Services/DiffKit.cs
using InnoPVManagementSystem.Common.Constants;
using InnoPVManagementSystem.Common.Utils;
using InnoPVManagementSystem.Innolinc;
using System.Data;
using System.IO;
using System.Text;
using System.Windows;
namespace InnoPVManagementSystem.Common.Services
{
    /// <summary>
    /// Diff 동작 옵션.
    /// </summary>
    public sealed class DiffOptions
    {
        // 비교 대상 파일 패턴.세미콜론/쉼표 구분 여러 패턴 가능.예: "*.csv;*.txt"
        public string FilePattern { get; init; } = "*.csv";

        // 하위 폴더까지 재귀적으로 포함할지 여부.
        public bool IncludeSubfolders { get; init; }

        // 각 라인에서 이 문자열이 처음 등장한 위치부터 오른쪽을 잘라(TrimEnd 포함) 비교.
        // null/빈 값이면 원문 라인을 그대로 비교.
        public string? LiteralText { get; init; }

        // 폴더2에서 수집한 "고유 라인 수"가 이 값을 초과하면, 최적화 모드 활성화
        // (매칭된 라인을 폴더2 Set에서 제거하여 이후 조회 비용 절감).
        public int OptimizeThresholdUniqueLines { get; init; } = 1_000_000;
    }

    // DiffKit.cs (상단 또는 적절한 네임스페이스 위치)
    public sealed class DiffRowChange
    {
        public string Key { get; init; } = "";            // 복합키 (UnitSep 로 조인)
        public IReadOnlyList<string> LeftRow { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> RightRow { get; init; } = Array.Empty<string>();
    }

    public sealed class DiffResult
    {
        public int Added { get; set; }                    // compare에만 존재
        public int Deleted { get; set; }                  // base에만 존재
        public int Modified { get; set; }                 // 키 동일 + 비키컬럼 변화
        public int lDuplicateCount { get; set; }          // 왼쪽 중복키 갯수
        public int rDuplicateCount { get; set; }          // 오른쪽 중복키 갯수

        // “각각 수정 파일 명시” → 수정된 행들의 키/내용 목록
        public List<DiffRowChange> ModifiedRows { get; } = new();
        public List<DiffRowChange> AddedRows { get; } = new();
        public List<DiffRowChange> DeletedRows { get; } = new();

        public bool HasDuplicateKeys { get; set; }
        public string? DuplicateKeyMessage { get; set; }
    }

    /// <summary>
    /// 파일↔파일 또는 파일↔폴더 비교 결과(라인 단위).
    /// </summary>
    public sealed class FileCompareResult
    {
        public int File1Total { get; init; }    // 총 라인 수(원문 기준)
        public int File2Total { get; init; }    // 총 라인 수(파일-폴더의 경우 폴더2 포든 파일 라인 합)
        public int OnlyInFile1 { get; init; }   // File2에는 없고 File1에만 존재하는(전처리 후) 라인 수.
        public int OnlyInFile2 { get; init; }   // File1에는 없고 File2에만 존재하는(전처리 후) 라인 수.
        public bool IsIdentical => OnlyInFile1 == 0 && OnlyInFile2 == 0; // 양방향 모두 차이가 없는지 여부(완전 동일 판단).

        public string? File1OnlyPath { get; init; } // File1 전용 라인 저장 파일 경로(선택적으로 채움)
        public string? File2OnlyPath { get; init; } // File2 전용 라인 저장 파일 경로(선택적으로 채움)
        public string? CommonPath { get; init; }    // 공통 라인 저장 파일 경로(선택적으로 채움, 파일↔파일에서 주로 사용)

        public List<string> Folder2ProcessedFiles { get; init; } = new();
        public int Folder2UniqueLines { get; init; }
        public Dictionary<string, int> Folder2FileMatches { get; init; } = new();
    }

    /// <summary>
    /// 폴더↔폴더 비교의 요약 결과.
    /// </summary>
    public sealed class FolderCompareSummary
    {
        public int TotalPairs { get; init; }        // 처리된 폴더1 파일 개수.
        public int TotalFolder1Lines { get; set; }  // 폴더1 총 라인 수(모든 파일 합)
        public int TotalMissingLines { get; set; }  // 폴더1에서 폴더2에 존재하지 않는 라인 수(총합)

        // <summary>전체 커버리지(= (총라인-누락)/총라인 * 100).</summary>
        public double CoveragePercent => TotalFolder1Lines == 0 ? 0
            : (double)(TotalFolder1Lines - TotalMissingLines) / TotalFolder1Lines * 100.0;

        public List<string> FullyCoveredFiles { get; } = new();      // 폴더2에 100% 커버된 파일 목록(파일명 및 라인 수 포함 문자열).
        public List<string> PartiallyCoveredFiles { get; } = new();  // 부분 커버(누락 존재) 파일 목록(파일명/총/누락 요약 문자열)

        public Dictionary<string, List<string>> Folder1OnlyLinesBySource { get; } = new(); // 폴더1 전용 라인 → 해당 라인이 발생한(출처) 파일 목록
        public List<string> SourceFileInfo { get; } = new(); // 출처별 요약 문자열("파일명: 누락/총(매칭)") 목록
        public bool UsedOptimizedMode { get; set; }          // 최적화 모드 사용 여부(폴더2 유니크 라인 임계 초과 시)
        public int Folder2UniqueLines { get; set; }          // 폴더2의 유니크 라인 수(전처리 후 중복 제거)
    }

    /// <summary>
    /// UI 진행 표시를 위한 얇은 인터페이스.
    /// ViewModel/Dispatcher를 직접 참조하지 않기 위해 호출자가 어댑터 구현.
    /// 없으면 <see cref="NullProgress"/> 사용.
    /// </summary>
    public interface IUiProgress
    {
        void SetMessage(string message);        // 상태 메시지 설정(예: "폴더2 라인 수집 중")
        void SetPercent(int value);             // 진행 퍼센트(0~100) 갱신
        void SetStep(int current, int total);   // 현재/총 스텝(예: N/M 파일 처리 중) 갱신
    }

    // === 서비스 본체 ===
    /// <summary>
    /// 라인 단위 집합 비교 서비스의 단일 파일 구현.
    /// - DI 없이도 기본 생성자로 즉시 사용 가능(내장 구현체 사용)
    /// - 필요 시 내부 인터페이스(ILineReader/ILiteralPreprocessor/IMemoryGuard)에 대한 DI 생성자 제공
    /// </summary>
    public sealed class DiffService
    {
        private readonly ILineReader _reader;
        private readonly ILiteralPreprocessor _pre;
        private readonly IMemoryGuard _mem;

        /// <summary>
        /// DI 없이 바로 사용하는 기본 생성자.
        /// - 라인 리더: 대용량(10MB 초과) 스트리밍 리딩
        /// - 리터럴 전처리: LiteralText 기준 꼬리 제거 + TrimEnd
        /// - 메모리 가드: 힙 사용량이 임계치 초과 시 강제 GC
        /// </summary>
        /// <param name="gcThresholdBytes">강제 GC 임계치(기본 100MB). 환경에 맞게 조정 가능.</param>
        public DiffService(long gcThresholdBytes = 100L * 1024 * 1024)
        {
            _reader = new StreamingLineReader();
            _pre = new LiteralPreprocessor();
            _mem = new MemoryGuard(gcThresholdBytes);
        }

        // 내부 구현 교체를 위한 인터페이스(단일 파일 유지 목적상 내부 선언)
        public interface ILineReader { Task<string[]> ReadAsync(string filePath, CancellationToken ct); }
        public interface ILiteralPreprocessor { string Transform(string line, string? literal); }
        public interface IMemoryGuard { void TryCollectIfNeeded(); void CollectNow(); }

        /// <summary>
        /// DI 환경에서 커스텀 구현체를 주입해 사용하고 싶은 경우의 생성자.
        /// </summary>
        public DiffService(ILineReader reader, ILiteralPreprocessor pre, IMemoryGuard mem)
        {
            _reader = reader; _pre = pre; _mem = mem;
        }

        /// <summary>
        /// (내장) 파일 라인 리더. 10MB 미만은 AllLines, 이상은 스트리밍으로 메모리 피크를 줄인다.
        /// </summary>
        private sealed class StreamingLineReader : ILineReader
        {
            private const long Threshold = 10L * 1024 * 1024;
            public async Task<string[]> ReadAsync(string filePath, CancellationToken ct)
            {
                var fi = new FileInfo(filePath);

                // 소용량: 간편/빠른 경로
                if (fi.Length < Threshold)
                    return await File.ReadAllLinesAsync(filePath, System.Text.Encoding.UTF8, ct).ConfigureAwait(false);

                // 대용량: 스트리밍으로 한 줄 씩 읽어 누적
                var list = new List<string>();
                using var reader = new StreamReader(filePath, System.Text.Encoding.UTF8);
                string? line; int cnt = 0;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    ct.ThrowIfCancellationRequested();
                    list.Add(line);

                    // 긴 파일에서 UI 프리징/스레드 독점 방지를 위한 양보 포인트
                    if (++cnt % 50_000 == 0) await Task.Yield();
                }
                return list.ToArray();
            }
        }

        /// <summary>
        /// (내장) 리터럴 전처리기. LiteralText가 있으면 그 이후 꼬리를 제거하고 우측 공백을 TrimEnd한다.
        /// </summary>
        private sealed class LiteralPreprocessor : ILiteralPreprocessor
        {
            public string Transform(string line, string? literal)
            {
                if (string.IsNullOrEmpty(line) || string.IsNullOrEmpty(literal)) return line ?? string.Empty;
                var idx = line.IndexOf(literal);
                return idx >= 0 ? line[..idx].TrimEnd() : line;
            }
        }

        /// <summary>
        /// (내장) 메모리 가드. 현재 관리 힙 사용량이 임계치를 넘으면 강제 GC(2-pass) 수행.
        /// </summary>
        private sealed class MemoryGuard : IMemoryGuard
        {
            private readonly long _threshold;
            public MemoryGuard(long thresholdBytes) => _threshold = thresholdBytes;
            public void TryCollectIfNeeded()
            {
                // false: GC 전 강제 컬렉터 호출 없이 현재 추정치
                if (GC.GetTotalMemory(false) > _threshold) CollectNow();
            }
            public void CollectNow()
            {
                // 2-pass 수집(파이널라이저 완료 대기 포함)로 릴리스 극대화
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
        }

        // === 파일/패턴 유틸 ==================================================
        public static DiffResult CompareByFileKeys(string baseFilePath, string compareFilePath)
        {
            var fileName = Path.GetFileName(baseFilePath);

            // 1) 키컬럼만 사용
            var (_, keyCols) = FileKeyManager.GetFileSettingsOrDefault(fileName, defaultDelimiter: "|");
            if (keyCols == null || keyCols.Count == 0)
            {
                // 키 미지정이면 행 전체 비교 폴백 (표 형태로만 로드)
                return CompareWholeRowAsAddDel(
                    LoadAsTable(baseFilePath),
                    LoadAsTable(compareFilePath)
                );
            }

            // 2) 표 데이터 로딩 (확장자에 맞춰 List<List<string>>로 통일)
            var left = LoadAsTable(baseFilePath);
            var right = LoadAsTable(compareFilePath);

            var result = new DiffResult();
            if (left.Count == 0 && right.Count == 0) return result;

            var leftHeader = left.Count > 0 ? (left[0] ?? new List<string>()) : new List<string>();
            var rightHeader = right.Count > 0 ? (right[0] ?? new List<string>()) : new List<string>();

            var leftMap = MapHeader(leftHeader);
            var rightMap = MapHeader(rightHeader);

            var lKeyIdx = GetKeyIndexes(leftMap, keyCols);
            var rKeyIdx = GetKeyIndexes(rightMap, keyCols);

            var lKeySet = new HashSet<int>(lKeyIdx.Where(i => i >= 0));
            var rKeySet = new HashSet<int>(rKeyIdx.Where(i => i >= 0));

            int colCount = Math.Max(leftHeader.Count, rightHeader.Count);

            // 키 → (시그니처, 원본행) 맵 생성
            var lDict = new Dictionary<string, (string Sig, List<string> Row)>();
            var rDict = new Dictionary<string, (string Sig, List<string> Row)>();
            var lDup = new HashSet<string>();
            var rDup = new HashSet<string>();

            // =======================
            // 3) Key → (Signature, Row) 딕셔너리 구성
            //    - 헤더(0행)를 제외하고 1행부터 실제 데이터만 순회
            //    - 키가 동일한 두 행이 등장하면 ‘중복키’로 처리하여 별도 리스트(lDup/rDup)에 기록
            //    - RowSignature()는 키컬럼을 제외한 “비키컬럼 전체 값”을 하나의 문자열로 묶어
            //      수정여부(Modified) 판단할 때 사용됨
            // =======================

            // -------- LEFT 파일 (기준 파일) --------
            for (int i = 1; i < left.Count; i++)

            {
                var row = left[i] ?? new List<string>();

                var key = RowToKey(row, lKeyIdx);

                // 이 시그니처가 동일하면 “내용 동일”, 다르면 “수정됨”으로 간주함.
                var sig = RowSignature(row, colCount, lKeySet);

                if (lDict.ContainsKey(key))
                {
                    // 동일한 키가 이미 존재하면 → 키 중복 발생
                    // (데이터 품질 문제. 수정 판단 시 중복도 "수정"으로 취급 가능)
                    lDup.Add(key);
                }
                else 
                {
                    // 새 키이므로 딕셔너리에 추가
                    lDict[key] = (sig, row);
                }
            }

            // -------- RIGHT 파일 (비교 대상 파일) -------
            for (int i = 1; i < right.Count; i++)
            {
                var row = right[i] ?? new List<string>();
                var key = RowToKey(row, rKeyIdx);
                var sig = RowSignature(row, colCount, rKeySet);
                if (rDict.ContainsKey(key)) rDup.Add(key);
                else rDict[key] = (sig, row);
            }

            // =======================
            // 4) 삭제 / 수정 판정
            //    lDict = 기준 파일(Base)에서 읽은 (키 → 행 정보)
            //    rDict = 비교 파일(Compare)에서 읽은 (키 → 행 정보)
            // =======================
            foreach (var (key, l) in lDict)
            {
                // [ 삭제(Deleted) ] 기준에는 있는데 비교대상에는 키가 없는 경우
                if (!rDict.TryGetValue(key, out var r))
                {
                    result.Deleted++;
                    result.DeletedRows.Add(new DiffRowChange { Key = key, LeftRow = l.Row, RightRow = Array.Empty<string>() });
                }
                else
                {
                    // [ 수정(Modified) ] 키는 존재하지만 "비키컬럼 내용"이 다른 경우
                    //
                    // RowSignature(): 키를 제외한 나머지 열을 하나의 문자열로 압축한 것
                    // 시그니처가 다르면 비키컬럼 값 중 하나라도 변경된 것 → 수정됨
                    if (!string.Equals(l.Sig, r.Sig, StringComparison.Ordinal))
                    {
                        result.Modified++;
                        result.ModifiedRows.Add(new DiffRowChange { Key = key, LeftRow = l.Row, RightRow = r.Row });
                        
                    }

                    //// ------------------------------------------------------------
                    //// [중복키 처리]
                    //// - 양쪽 파일 모두 동일 키가 여러 번 등장하는 "데이터 오류" 상황
                    //// - 품질 문제가 있으므로 수정된 것으로 간주
                    //// ------------------------------------------------------------
                    //if (lDup.Contains(key) || rDup.Contains(key))
                    //{
                    //    result.Modified++;
                    //    if (!result.ModifiedRows.Any(m => m.Key == key))
                    //        result.ModifiedRows.Add(new DiffRowChange { Key = key, LeftRow = l.Row, RightRow = r.Row });
                    //}
                }
            }

            // =======================
            // 4.5) 중복코드 존재시 알림창 띄우고 비교로직 중지
            //      - [파일명]
            //      - 키 컬럼 헤더
            //      - 왼쪽/오른쪽 각각 중복키 목록 (컬럼별로 분리)
            // =======================
            if (lDup.Count > 0 || rDup.Count > 0)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"[{fileName}] 중복 키가 존재하여 비교를 중단합니다.");
                sb.AppendLine();

                // ---- 키 컬럼명 표시 ----
                sb.AppendLine("[키 컬럼]");
                sb.AppendLine(" - " + string.Join(", ", keyCols));
                sb.AppendLine();

                // Helper: 키 문자열 → 컬럼별 값 분리
                string FormatKey(string key)
                {
                    var parts = key.Split(FileKeyManager.UnitSeparator);
                    var padded = new List<string>();

                    for (int i = 0; i < keyCols.Count; i++)
                    {
                        string val = (i < parts.Length ? parts[i] : "").Trim();
                        padded.Add(val);
                    }

                    // "값1 | 값2 | 값3" 형식으로 출력
                    return string.Join(" | ", padded);
                }

                // 왼쪽 중복 키
                if (lDup.Count > 0)
                {
                    sb.AppendLine("[기준 파일 중복 키]");
                    sb.AppendLine(string.Join(" | ", keyCols)); // 헤더 1줄 출력

                    foreach (var key in lDup.OrderBy(k => k))
                        sb.AppendLine(FormatKey(key));
                }

                // 오른쪽 중복 키
                if (rDup.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("[비교 파일 중복 키]");
                    sb.AppendLine(string.Join(" | ", keyCols)); // 헤더 1줄 출력

                    foreach (var key in rDup.OrderBy(k => k))
                        sb.AppendLine(FormatKey(key));
                }

                //MessageBox.Show(
                //    sb.ToString(),
                //    "중복 키 존재 - 비교 중단",
                //    MessageBoxButton.OK,
                //    MessageBoxImage.Warning);

                result.HasDuplicateKeys = true;
                result.lDuplicateCount = lDup.Count;
                result.rDuplicateCount = rDup.Count;
                result.DuplicateKeyMessage = sb.ToString();

                // 결과값 초기화 
                // result.Added = 0;
                // result.Deleted = 0;
                // result.Modified = 0;
            }

            // 5) 추가
            foreach (var (key, r) in rDict)
            {
                if (!lDict.ContainsKey(key))
                {
                    result.Added++;
                    result.AddedRows.Add(new DiffRowChange
                    {
                        Key = key,
                        LeftRow = Array.Empty<string>(),
                        RightRow = r.Row
                    });
                }
            }

            return result;
        }

        /// <summary>
        /// full key 문자열 집합을 dupKeyColumns 기준의 축약 키 집합으로 변환한다.
        /// - fullKeys     : keyColumns 순서로 UnitSeparator로 조인된 키 문자열들
        /// - keyColumns   : fileKeyConfig.json 의 keyColumns
        /// - dupKeyColumns: fileKeyConfig.json 의 dupKeyColumns (없으면 keyColumns 와 동일하게 설정)
        /// 
        /// 반환:
        /// - dupKeyColumns 순서대로 UnitSeparator 로 join 된 키 문자열들의 집합
        ///   (예: base_prd_code + rider_prd_code 기준 "상품 키")
        /// </summary>
        public static IReadOnlyCollection<string> ProjectKeysToDupKeys(
            IEnumerable<string> fullKeys,
            IReadOnlyList<string> keyColumns,
            IReadOnlyList<string> dupKeyColumns)
        {
            var result = new HashSet<string>();
            if (fullKeys == null)
                return result;

            // keyColumns 컬럼명 → index 매핑
            var colIndexMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < keyColumns.Count; i++)
                colIndexMap[keyColumns[i]] = i;

            // dupKeyColumns 를 keyColumns 상의 index 로 변환
            var dupIndexes = dupKeyColumns
                .Select(col => colIndexMap.TryGetValue(col, out var idx) ? idx : -1)
                .ToArray();

            foreach (var key in fullKeys)
            {
                var parts = (key ?? string.Empty).Split(FileKeyManager.UnitSeparator);

                var projected = new string[dupIndexes.Length];
                for (int i = 0; i < dupIndexes.Length; i++)
                {
                    int idx = dupIndexes[i];
                    projected[i] = (idx >= 0 && idx < parts.Length)
                        ? parts[idx]
                        : string.Empty;
                }

                var dupKey = string.Join(FileKeyManager.UnitSeparator, projected);
                result.Add(dupKey);
            }

            return result;
        }

        // 파일을 표 데이터(List<List<string>>)로 로드
        private static List<List<string>> LoadAsTable(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();

            // 1) IO는 기존 모듈 그대로
            if (ext == FileConstants.Extensions.Io)
                return IIO.IOtoCsv(path).csvData;

            // 2) CSV도 통일해서 List<List<string>>로 변환
            if (ext == FileConstants.Extensions.Csv)
            {
                return ParserUtil.ParsePipeDelimitedCsv(path);
            }

            // 3) 그 외 확장자는 빈 표
            return new List<List<string>>();
        }

        #region --- 로더 & 유틸 — 구분자/키 인덱스/시그니처 ------
        private static Dictionary<string, int> MapHeader(List<string> header)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < header.Count; i++)
            {
                var name = header[i]?.Trim() ?? $"col{i}";
                if (!map.ContainsKey(name)) map[name] = i;
            }
            return map;
        }

        /// <summary>
        /// 키 컬럼명 목록을 인덱스 배열로 변환
        /// </summary>
        private static int[] GetKeyIndexes(Dictionary<string, int> headerMap, IEnumerable<string> keyCols)
        {
            return keyCols.Select(k => headerMap.TryGetValue(k, out var idx) ? idx : -1).ToArray();
        }

        /// <summary>
        /// 행에서 키 컬럼값들을 결합해 고유 키 문자열 생성
        /// </summary>
        private static string RowToKey(IReadOnlyList<string> row, int[] keyIdx)
        {
            var parts = new string[keyIdx.Length];
            for (int i = 0; i < keyIdx.Length; i++)
            {
                int ix = keyIdx[i];
                parts[i] = (ix >= 0 && ix < row.Count) ? (row[ix] ?? "").Trim() : "";
            }
            return string.Join(FileKeyManager.UnitSeparator, parts); // Unit Separator
        }

        /// <summary>
        /// 키 컬럼 제외 나머지 컬럼값으로 행 시그니처 문자열 생성
        /// </summary>
        private static string RowSignature(IReadOnlyList<string> row, int colCount, HashSet<int> keyIdxSet)
        {
            var sb = new StringBuilder(256);
            for (int i = 0; i < colCount; i++)
            {
                if (keyIdxSet.Contains(i)) continue;
                var v = (i < row.Count ? row[i] : "") ?? "";
                v = v.Trim();

                if (i > 0) sb.Append('\u001E'); // Record Separator
                sb.Append(v);
            }
            return sb.ToString();
        }
        #endregion

        #region ------------- 키 미지정 파일 폴백(이전 동작 유지) ---------
        private static DiffResult CompareWholeRowAsAddDel(List<List<string>> left, List<List<string>> right)
        {
            var L = new HashSet<string>(RowsToLines(left));
            var R = new HashSet<string>(RowsToLines(right));

            return new DiffResult
            {
                Deleted = L.Count(x => !R.Contains(x)),
                Added = R.Count(x => !L.Contains(x)),
                Modified = 0
            };

            static IEnumerable<string> RowsToLines(List<List<string>> rows)
            {
                foreach (var row in rows)
                    yield return string.Join("\u001E", row.Select(s => (s ?? "").Trim()));
            }
        }
        #endregion

    }

    /// <summary>
    /// 진행률 전달이 필요 없을 때 사용하는 더미 구현.
    /// </summary>
    public sealed class NullProgress : IUiProgress
    {
        public void SetMessage(string message) { }
        public void SetPercent(int value) { }
        public void SetStep(int current, int total) { }
    }
}