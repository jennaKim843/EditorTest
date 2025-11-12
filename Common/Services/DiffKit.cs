// Common/Services/DiffKit.cs
using InnoPVManagementSystem.Common.Utils;
using InnoPVManagementSystem.Innolinc;
using System.Data;
using System.IO;
using System.Text;
namespace InnoPVManagementSystem.Common.Services
{// ---------------------------------------------------------------------
    // DiffKit: 폴더/파일 간 라인 단위 집합 비교 유틸리티 (소규모 프로젝트용 단일 파일 구성)
    //
    // 핵심 아이디어
    //  - 모든 비교는 "라인 문자열"을 원자 단위로 보고 HashSet 교/차/합 연산으로 처리
    //  - LiteralText가 설정되면 각 라인에서 LiteralText 이후 꼬리를 제거(TrimEnd 포함)하여 비교 정규화
    //  - 폴더→폴더 비교 시, 폴더2 전체의 "고유 라인 집합"을 미리 구성 후 폴더1의 각 파일을 커버리지 관점으로 평가
    //  - 폴더2 고유 라인이 특정 임계값(기본 100만) 초과 시, 성능 최적화(매칭 라인을 폴더2 Set에서 제거) 선택
    //
    // 성능/메모리 설계 포인트
    //  - 대용량 파일(>10MB)은 스트리밍 라인 리딩(읽으면서 누적)으로 메모리 피크 감소
    //  - HashSet<string> 중심 집합 연산으로 O(1) 기대치의 조회 성능 확보
    //  - IMemoryGuard로 전체 힙 메모리 임계 감시, 필요 시 강제 GC (2-pass)
    //  - Parallel.ForEachAsync로 파일 단위 병렬 처리(디스크 IO/CPU 혼합형)
    //
    // 주의사항
    //  - "라인 단위" 비교만 수행(정렬, 컬럼 파싱, 공백 허용 등 고급 diff는 의도적으로 제외)
    //  - 인코딩은 UTF-8 전제 (필요 시 프로젝트 표준 인코딩으로 확장)
    //  - LiteralText는 "왼쪽 유효영역"만 비교할 때 사용(예: 로그 타임스탬프/주석 꼬리 제거)
    //  - HashSet 크기가 매우 커질 수 있음(수백만 라인). 충분한 메모리를 확보하거나 임계값/패턴을 조정하세요.
    // ---------------------------------------------------------------------

    // === 옵션/결과 DTO ===
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

        // “각각 수정 파일 명시” → 수정된 행들의 키/내용 목록
        public List<DiffRowChange> ModifiedRows { get; } = new();

        // 선택적으로 Added/Deleted 행도 보고 싶다면 아래도 유지
        public List<DiffRowChange> AddedRows { get; } = new();
        public List<DiffRowChange> DeletedRows { get; } = new();
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
        /// 폴더1의 각 파일을 폴더2 전체에 대해 "존재 커버리지" 관점으로 비교한다.
        /// 폴더2의 유니크 라인을 먼저 수집해 두고, 폴더1 파일을 병렬로 스캔하며 누락 라인을 집계.
        /// 폴더2 유니크 라인이 임계치를 넘으면 "매칭 라인 제거" 최적화 모드 활성.
        /// </summary>
        /// <remarks>
        /// - 파일 내용은 "라인 단위"로만 비교한다.
        /// - 폴더2 유니크 라인이 매우 클 경우 메모리 사용량이 커질 수 있다(충분한 힙 필요).
        /// - 각 파일 읽기 오류는 개별적으로 무시(로그성 WriteLine만), 전체 작업은 지속.
        /// </remarks>
        public async Task<FolderCompareSummary> CompareFolderToFolderAsync(
            string folder1, string folder2, DiffOptions opt,
            IUiProgress? progress, CancellationToken ct)
        {
            progress?.SetMessage("폴더2 라인 수집 중...");
            var searchOpt = opt.IncludeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

            // 패턴 분해(*.csv;*.txt 등) 후 중복 제거
            var folder2Files = EnumerateFiles(folder2, opt.FilePattern, searchOpt)
                               .Where(f => !IsMergeFile(f)).ToArray();

            var folder2Set = new HashSet<string>();
            var lockSet = new object();

            await Parallel.ForEachAsync(folder2Files, ct, async (file2, token) =>
            {
                try
                {
                    // 파일 라인 읽기(대용량은 스트리밍)
                    var lines = await _reader.ReadAsync(file2, token).ConfigureAwait(false);

                    // 스레드 로컬 HashSet으로 변환 후 글로벌 Set에 병합
                    var local = new HashSet<string>();
                    foreach (var l in lines) local.Add(_pre.Transform(l, opt.LiteralText));
                    lock (lockSet) foreach (var s in local) folder2Set.Add(s);
                }
                catch { /* 개별 오류 무시 */ }
            });

            // 수집 후 메모리 상태 점검(임계 초과 시 강제 GC)
            _mem.TryCollectIfNeeded();
            progress?.SetMessage($"폴더2 라인 수집 완료 ({folder2Set.Count:N0}줄) - 개별 파일 체크 시작...");

            var folder1Files = EnumerateFiles(folder1, opt.FilePattern, searchOpt).ToArray();

            var sum = new FolderCompareSummary
            {
                TotalPairs = folder1Files.Length,
                Folder2UniqueLines = folder2Set.Count
            };

            // 폴더2 유니크 라인이 임계 초과면 최적화 모드: 매칭 라인을 폴더2 Set에서 제거(후속 조회 비용 감소)
            bool optimized = folder2Set.Count > opt.OptimizeThresholdUniqueLines;
            sum.UsedOptimizedMode = optimized;

            var missingBySource = sum.Folder1OnlyLinesBySource; // 라인→출처 파일 목록
            var srcInfo = sum.SourceFileInfo;                   // "파일: 누락/총(매칭)" 문자열
            var setLock = new object();                         // 최적화 모드에서 폴더2 Set 제거 연산 보호
            int done = 0;                                       // 진행 카운터(UI 업데이트용)

            int totalFolder1Lines = 0;
            int totalMissingLines = 0;

            await Parallel.ForEachAsync(folder1Files, ct, async (f1, token) =>
            {
                if (!File.Exists(f1)) return;

                var lines = await _reader.ReadAsync(f1, token).ConfigureAwait(false);
                var f1Set = new HashSet<string>(lines);// 원문 라인 중복 제거(속도 최적화)
                int total = lines.Length;              // 총 라인 수(원문 기준)
                int missingCount = 0;                  // 폴더2에 없는 라인 수
                int matched = 0;                       // 폴더2에 존재한 라인 수

                if (optimized)
                {
                    // 최적화: 폴더2에 매칭되면 폴더2 Set에서 즉시 제거하여 후속 조회량 감소
                    lock (setLock)
                    {
                        foreach (var raw in f1Set)
                        {
                            var line = _pre.Transform(raw, opt.LiteralText);
                            if (folder2Set.Contains(line)) { matched++; folder2Set.Remove(line); }
                            else { missingCount++; AddMissing(missingBySource, line, f1); }
                        }
                    }
                }
                else
                {
                    // 일반 모드: 폴더2 Set을 읽기 전용으로만 사용(제거하지 않음)
                    foreach (var raw in f1Set)
                    {
                        var line = _pre.Transform(raw, opt.LiteralText);
                        if (folder2Set.Contains(line)) matched++;
                        else { missingCount++; AddMissing(missingBySource, line, f1); }
                    }
                }

                // 합산(동시성 안전)
                Interlocked.Add(ref totalFolder1Lines, total);
                Interlocked.Add(ref totalMissingLines, missingCount);

                // 파일별 요약 분류(락 범위 최소화)
                if (missingCount == 0) lock (sum) sum.FullyCoveredFiles.Add($"{Path.GetFileName(f1)} ({total:N0}줄)");
                else lock (sum) sum.PartiallyCoveredFiles.Add($"{Path.GetFileName(f1)} ({total:N0}줄, {missingCount:N0}줄 누락)");

                lock (srcInfo) srcInfo.Add($"• {Path.GetFileName(f1)}: {missingCount:N0} / {total:N0}줄 (매칭: {matched:N0}줄)");

                // 진행률 갱신
                var cur = Interlocked.Increment(ref done);
                progress?.SetStep(cur, sum.TotalPairs);
                progress?.SetPercent((int)((double)cur / sum.TotalPairs * 100));
            });

            // 집계 결과 반영
            sum.TotalFolder1Lines = totalFolder1Lines;
            sum.TotalMissingLines = totalMissingLines;

            _mem.TryCollectIfNeeded();  // 마지막 메모리 정리(피크 해소)
            return sum;
        }

        /// <summary>
        /// 파일↔파일 직접 비교. 두 파일의 "전처리된 고유 라인" 집합을 비교하여 교/차집합을 계산.
        /// </summary>
        public async Task<FileCompareResult> CompareFileToFileAsync(
            string file1, string file2, DiffOptions opt, CancellationToken ct)
        {
            // 두 파일 모두 메모리에 읽는 방식(>10MB는 내부에서 스트리밍 처리)
            var f1 = await _reader.ReadAsync(file1, ct).ConfigureAwait(false);
            var f2 = await _reader.ReadAsync(file2, ct).ConfigureAwait(false);

            // 전처리 후 집합 생성
            var f1Set = new HashSet<string>(f1.Select(x => _pre.Transform(x, opt.LiteralText)));
            var f2Set = new HashSet<string>(f2.Select(x => _pre.Transform(x, opt.LiteralText)));

            // 집합 연산(공통/차이)
            var common = f1Set.Intersect(f2Set).ToList();
            var only1 = f1Set.Except(f2Set).ToList();
            var only2 = f2Set.Except(f1Set).ToList();

            return new FileCompareResult
            {
                File1Total = f1.Length,
                File2Total = f2.Length,
                OnlyInFile1 = only1.Count,
                OnlyInFile2 = only2.Count,
                Folder2UniqueLines = f2Set.Count, // 참고 정보
                Folder2ProcessedFiles = new List<string> { Path.GetFileName(file2) },
                Folder2FileMatches = new Dictionary<string, int> { { Path.GetFileName(file2), common.Count } }
            };
        }

        /// <summary>
        /// 파일↔폴더 비교. 폴더2 전체(패턴/서브포더 옵션 적용)에 대해 File1 라인이 얼마나 커버되는지 평가.
        /// </summary>
        public async Task<FileCompareResult> CompareFileToFolderAsync(
            string file1, string folder2, DiffOptions opt, CancellationToken ct)
        {
            // File1 전처리 집합
            var f1 = await _reader.ReadAsync(file1, ct).ConfigureAwait(false);
            var f1Set = new HashSet<string>(f1.Select(x => _pre.Transform(x, opt.LiteralText)));

            var searchOpt = opt.IncludeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var folder2Files = EnumerateFiles(folder2, opt.FilePattern, searchOpt)
                               .Where(f => !IsMergeFile(f)).ToArray();

            var f2All = new HashSet<string>();                  // 폴더2 전체 전처리 라인 고유 집합
            var perFileMatches = new Dictionary<string, int>(); // 파일별 매칭 수(참고용)
            var processed = new List<string>();                 // 처리된 폴더2 파일 목록
            int f2Total = 0;                                    // 폴더2 전체 라인 수(원문 기준 총합)

            foreach (var f2 in folder2Files)
            {
                try
                {
                    var lines = await _reader.ReadAsync(f2, ct).ConfigureAwait(false);
                    var set = new HashSet<string>(lines.Select(x => _pre.Transform(x, opt.LiteralText)));
                    f2Total += lines.Length;

                    // 파일별로 File1과 몇 줄 매칭되었는지 집계(가시화용)
                    var matches = f1Set.Intersect(set).Count();
                    if (matches > 0) perFileMatches[Path.GetFileName(f2)] = matches;

                    foreach (var s in set) f2All.Add(s);
                    processed.Add(Path.GetFileName(f2));
                }
                catch { }
            }

            // File1에서 폴더2 전체에 없는 라인
            var only1 = f1Set.Except(f2All).ToList();

            return new FileCompareResult
            {
                File1Total = f1.Length,
                File2Total = f2Total,
                OnlyInFile1 = only1.Count,
                Folder2UniqueLines = f2All.Count,
                Folder2ProcessedFiles = processed,
                Folder2FileMatches = perFileMatches
            };
        }

        // === 내부 구현체(단일 파일 유지 목적상 private sealed로 동거) ============

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

            // 5) 추가
            foreach (var (key, r) in rDict)
            {
                if (!lDict.ContainsKey(key))
                {
                    result.Added++;
                    result.AddedRows.Add(new DiffRowChange { Key = key, LeftRow = Array.Empty<string>(), RightRow = r.Row });
                }
            }

            return result;
        }

        // 파일을 표 데이터(List<List<string>>)로 로드
        private static List<List<string>> LoadAsTable(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();

            // 1) IO는 기존 모듈 그대로
            if (ext == Constants.FileConstants.Extensions.Io)
                return IIO.IOtoCsv(path).csvData;

            // 2) CSV도 통일해서 List<List<string>>로 변환
            //    - 프로젝트에 별도 CSV 모듈이 있으면 그걸 호출
            //    - 없으면 내부 파서(구분자 자동감지) 사용
            if (ext == Constants.FileConstants.Extensions.Csv)
            {
                //var text = File.ReadAllText(path, Encoding.UTF8);
                //return ParseCsvToListList(text);     // 이미 DiffService에 있는 자동감지 파서
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
            return string.Join("\u001F", parts); // Unit Separator
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


        #region --------- CSV 파서(구분자 1글자 지원) ---------
        private static List<List<string>> ParseDelimitedToListList(string text, char delimiter)
        {
            var rows = new List<List<string>>();
            if (string.IsNullOrEmpty(text)) return rows;

            using var sr = new StringReader(text);
            string? line;
            bool inQuote = false;
            var record = new StringBuilder();

            while ((line = sr.ReadLine()) != null)
            {
                if (record.Length > 0) record.Append('\n');   // 멀티라인 필드 지원
                record.Append(line);

                if (EndsRecord(record.ToString()))
                {
                    rows.Add(SplitCsvLine(record.ToString(), delimiter));
                    record.Clear();
                }
            }

            if (record.Length > 0)
                rows.Add(SplitCsvLine(record.ToString(), delimiter));

            return rows;

            static bool EndsRecord(string s)
            {
                // 따옴표 개수가 짝수면 레코드 종료로 간주
                int q = 0;
                for (int i = 0; i < s.Length; i++) if (s[i] == '"') q++;
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
                            if (i + 1 < line.Length && line[i + 1] == '"') { f.Append('"'); i++; }
                            else q = false;
                        }
                        else f.Append(c);
                    }
                    else
                    {
                        if (c == d) { fields.Add(f.ToString()); f.Clear(); }
                        else if (c == '"') q = true;
                        else f.Append(c);
                    }
                }
                fields.Add(f.ToString());
                return fields;
            }
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

        /// <summary>
        /// 패턴(세미콜론/쉼표 구분)별로 재귀/비재귀 검색 후 중복 제거하여 파일 목록을 반환.
        /// </summary>
        private static IEnumerable<string> EnumerateFiles(string root, string pattern, SearchOption opt)
        {
            var patterns = pattern.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                                  .Select(s => s.Trim()).DefaultIfEmpty("*.*");
            return patterns.SelectMany(p => Directory.GetFiles(root, p, opt)).Distinct();
        }

        /// <summary>
        /// 도메인에서 생성되는 중간/결과 파일을 폴더 비교에서 제외하기 위한 규칙.
        /// </summary>
        private static bool IsMergeFile(string filePath)
        {
            var name = Path.GetFileName(filePath);
            if (name.Equals("merged_all_files.csv", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.StartsWith("backup_", StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith("_merged_all_files.csv", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.EndsWith("_not_in_folder2.csv", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.Contains("_only_by_", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.Contains("_only_lines_consolidated_", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// 누락 라인을 출처 파일 기준으로 누적(라인→파일목록).
        /// </summary>
        private static void AddMissing(Dictionary<string, List<string>> sink, string line, string sourceFile)
        {
            if (!sink.TryGetValue(line, out var list)) sink[line] = list = new List<string>();
            list.Add(sourceFile);
        }

        //// 간단 CSV 파서 (따옴표 이스케이프 "" 지원)
        //public static List<List<string>> ParseCsvToListList(string text)
        //{
        //    if (string.IsNullOrEmpty(text)) return new();

        //    // --- 구분자 자동 감지 ---
        //    //char delim = DetectDelimiter(text); // ',', '|', '\t' 중 결정
        //    char delim = Constants.FileConstants.Delimiters.PipeCharacter;

        //    var rows = new List<List<string>>();
        //    using var sr = new StringReader(text);

        //    string? line;
        //    bool inQuote = false;
        //    var record = new StringBuilder();

        //    while ((line = sr.ReadLine()) != null)
        //    {
        //        if (record.Length > 0) record.Append('\n'); // 멀티라인 필드 지원
        //        record.Append(line);

        //        if (EndsRecord(record.ToString(), delim))
        //        {
        //            rows.Add(SplitCsvLine(record.ToString(), delim));
        //            record.Clear();
        //        }
        //    }

        //    if (record.Length > 0)
        //        rows.Add(SplitCsvLine(record.ToString(), delim));

        //    return rows;

        //    static bool EndsRecord(string s, char d)
        //    {
        //        // 짝수 개의 따옴표면 레코드 종료로 간주
        //        int q = 0;
        //        for (int i = 0; i < s.Length; i++)
        //            if (s[i] == '"') q += 1;
        //        return (q % 2) == 0;
        //    }

        //    static List<string> SplitCsvLine(string line, char d)
        //    {
        //        var fields = new List<string>();
        //        var f = new StringBuilder();
        //        bool q = false;

        //        for (int i = 0; i < line.Length; i++)
        //        {
        //            char c = line[i];

        //            if (q)
        //            {
        //                if (c == '"')
        //                {
        //                    if (i + 1 < line.Length && line[i + 1] == '"')
        //                    {
        //                        f.Append('"'); // "" -> "
        //                        i++;
        //                    }
        //                    else q = false;
        //                }
        //                else f.Append(c);
        //            }
        //            else
        //            {
        //                if (c == d)
        //                {
        //                    fields.Add(f.ToString());
        //                    f.Clear();
        //                }
        //                else if (c == '"') q = true;
        //                else f.Append(c);
        //            }
        //        }
        //        fields.Add(f.ToString());
        //        return fields;
        //    }
        //}
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