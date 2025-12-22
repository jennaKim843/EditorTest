using InnoPVManagementSystem.Common.Constants;
using InnoPVManagementSystem.Common.IO;
using InnoPVManagementSystem.Common.Services;
using InnoPVManagementSystem.Common.Utils;
using InnoPVManagementSystem.Innolinc;
using System.IO;
using System.Text;
using System.Threading;


namespace InnoPVManagementSystem.Modules.CompareMerge.Services
{
    /// <summary>
    /// 관리자 전체 적용(트랜잭션) 및 단일 파일 머지 로직 담당 서비스
    /// - ViewModel에는 UI/상태만 남기고, 실제 작업은 여기서 수행
    /// </summary>
    internal class CompareMergeApplyService
    {
        public sealed class ApplyResult
        {
            public bool Success { get; init; }
            public string? WarnMessage { get; init; }   // 중복키 등으로 중단
            public string? FailMessage { get; init; }   // 예외/롤백 사유
        }

        /// <summary>
        /// Standard 폴더와 하나/여러 사용자 폴더의 공통(.csv/.io) 파일에 대해,
        /// (S vs A) / (S vs U) 충돌 검증 후 사용자 변경분을 관리자 폴더에 일괄 머지한다.
        /// - 사전 중복키 검사 옵션 지원(1건이라도 있으면 전체 중단)
        /// - 작업 전 관리자 파일 백업, 실패 시 전체 롤백
        /// - 진행률은 progress로 보고한다.
        /// </summary>
        public async Task<ApplyResult> ApplyMergeTransactionAsync(
            string standardFolderPath,
            IEnumerable<string> userFolderPaths,
            string adminFolderPath,
            bool precheckDuplicateKeys,
            IUiProgress progress,
            CancellationToken ct = default)
        {
            // --- 입력 정리 ---
            var userFolders = userFolderPaths
                .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (userFolders.Count == 0)
            {
                return new ApplyResult { Success = false, WarnMessage = "적용할 대상 폴더가 없습니다." };
            }

            if (string.IsNullOrWhiteSpace(standardFolderPath) || !Directory.Exists(standardFolderPath))
            {
                return new ApplyResult { Success = false, FailMessage = "기준(Standard) 폴더가 올바르지 않습니다." };
            }

            Directory.CreateDirectory(adminFolderPath);

            var allowedExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                FileConstants.Extensions.Csv,
                FileConstants.Extensions.Io
            };

            // Standard 파일 맵
            var stdFiles = Directory.EnumerateFiles(standardFolderPath, "*.*", SearchOption.TopDirectoryOnly)
                .Where(p => allowedExt.Contains(Path.GetExtension(p)))
                .ToDictionary(p => Path.GetFileName(p), p => p, StringComparer.OrdinalIgnoreCase);

            if (stdFiles.Count == 0)
            {
                return new ApplyResult { Success = false, FailMessage = "기준(Standard) 폴더에 비교 가능한 파일(.csv/.io)이 없습니다." };
            }

            // 후보 파일명 집합: Standard에 있고 userFolders 중 하나라도 있는 파일
            var candidateNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var uf in userFolders)
            {
                foreach (var p in Directory.EnumerateFiles(uf, "*.*", SearchOption.TopDirectoryOnly)
                                           .Where(p => allowedExt.Contains(Path.GetExtension(p))))
                {
                    var name = Path.GetFileName(p);
                    if (stdFiles.ContainsKey(name))
                        candidateNames.Add(name);
                }
            }

            if (candidateNames.Count == 0)
            {
                return new ApplyResult
                {
                    Success = false,
                    WarnMessage = "적용할 대상 파일이 없습니다.\n(Standard와 대상 폴더에 공통으로 존재하는 .csv/.io 파일이 없음)"
                };
            }

            // --- 실제 작업은 백그라운드에서 ---
            return await Task.Run(() =>
            {
                var backupMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                try
                {
                    ct.ThrowIfCancellationRequested();

                    var candidateList = candidateNames.ToList();
                    int candTotal = candidateList.Count;

                    progress.SetPercent(0);
                    progress.SetMessage("전체 적용 준비 중...");

                    // 1) 사전 중복키 검사 (0~40%)
                    if (precheckDuplicateKeys)
                    {
                        var dupList = new List<string>();

                        int folderTotal = userFolders.Count;
                        int folderIndex = 0;

                        foreach (var uf in userFolders)
                        {
                            ct.ThrowIfCancellationRequested();

                            folderIndex++;
                            int basePct = (int)((folderIndex - 1) * 40.0 / Math.Max(1, folderTotal));
                            progress.SetPercent(basePct);
                            progress.SetMessage($"[사전검사] 폴더 확인 중... ({folderIndex}/{folderTotal}) {Path.GetFileName(uf)}");

                            var userMap = Directory.EnumerateFiles(uf, "*.*", SearchOption.TopDirectoryOnly)
                                .Where(p => allowedExt.Contains(Path.GetExtension(p)))
                                .ToDictionary(p => Path.GetFileName(p), p => p, StringComparer.OrdinalIgnoreCase);

                            int fileTotal = candidateList.Count;
                            int fileIndex = 0;

                            foreach (var name in candidateList)
                            {
                                ct.ThrowIfCancellationRequested();

                                if (!userMap.TryGetValue(name, out var userPath))
                                    continue;

                                var (_, keys) = FileKeyManager.GetFileSettingsOrDefault(name);
                                if (keys == null || keys.Count == 0)
                                    continue;

                                fileIndex++;
                                int pct = basePct + (int)(40.0 / Math.Max(1, folderTotal) * (fileIndex * 1.0 / Math.Max(1, fileTotal)));
                                progress.SetPercent(pct);
                                progress.SetMessage($"[사전검사] {Path.GetFileName(uf)} ({folderIndex}/{folderTotal}) - {name} ({fileIndex}/{fileTotal})");

                                var diff = DiffService.CompareByFileKeys(stdFiles[name], userPath);
                                if (diff.HasDuplicateKeys)
                                {
                                    dupList.Add($"{Path.GetFileName(uf)} / {name}");
                                    if (dupList.Count >= 50) break;
                                }
                            }

                            if (dupList.Count >= 50) break;
                        }

                        if (dupList.Count > 0)
                        {
                            var sb = new StringBuilder();
                            sb.AppendLine("중복 키가 존재하는 파일이 있어 전체 적용을 수행할 수 없습니다.");
                            sb.AppendLine();
                            sb.AppendLine("[중복 키 존재 (폴더 / 파일)]");
                            foreach (var x in dupList.Take(10))
                                sb.AppendLine(" - " + x);
                            if (dupList.Count > 10)
                                sb.AppendLine($"…외 {dupList.Count - 10}건");

                            return new ApplyResult { Success = false, WarnMessage = sb.ToString() };
                        }
                    }

                    // 2) 백업 (40~55%)
                    progress.SetPercent(40);
                    progress.SetMessage("백업 생성 중...");

                    for (int i = 0; i < candTotal; i++)
                    {
                        ct.ThrowIfCancellationRequested();

                        var name = candidateList[i];
                        int pct = 40 + (int)((i + 1) * 15.0 / Math.Max(1, candTotal));
                        progress.SetPercent(pct);
                        progress.SetMessage($"[백업] {name} ({i + 1}/{candTotal})");

                        var adminFilePath = Path.Combine(adminFolderPath, name);
                        if (!File.Exists(adminFilePath))
                            continue;

                        if (backupMap.ContainsKey(adminFilePath))
                            continue;

                        var backupPath = adminFilePath + ".bak_applytx";
                        File.Copy(adminFilePath, backupPath, overwrite: true);
                        backupMap[adminFilePath] = backupPath;
                    }

                    // 3) 머지 (55~95%)
                    int folderTotal2 = userFolders.Count;
                    int folderIndex2 = 0;

                    foreach (var uf in userFolders)
                    {
                        ct.ThrowIfCancellationRequested();

                        folderIndex2++;
                        progress.SetPercent(55);
                        progress.SetMessage($"[적용] 폴더 처리 시작 ({folderIndex2}/{folderTotal2}) {Path.GetFileName(uf)}");

                        var userMap = Directory.EnumerateFiles(uf, "*.*", SearchOption.TopDirectoryOnly)
                            .Where(p => allowedExt.Contains(Path.GetExtension(p)))
                            .ToDictionary(p => Path.GetFileName(p), p => p, StringComparer.OrdinalIgnoreCase);

                        int fileTotal = candidateList.Count;
                        for (int fileIndex = 1; fileIndex <= fileTotal; fileIndex++)
                        {
                            ct.ThrowIfCancellationRequested();

                            var name = candidateList[fileIndex - 1];

                            double folderSpan = 40.0 / Math.Max(1, folderTotal2); // 55~95 = 40%
                            double folderBase = 55.0 + (folderIndex2 - 1) * folderSpan;
                            int pct = (int)(folderBase + folderSpan * (fileIndex * 1.0 / Math.Max(1, fileTotal)));

                            progress.SetPercent(pct);
                            progress.SetMessage($"[적용] {Path.GetFileName(uf)} ({folderIndex2}/{folderTotal2}) - {name} ({fileIndex}/{fileTotal})");

                            if (!stdFiles.TryGetValue(name, out var stdPath))
                                continue;

                            if (!userMap.TryGetValue(name, out var userPath))
                                continue;

                            // 단일 파일 머지(충돌 시 예외)
                            MergeUserChangesToAdmin(stdPath, userPath, adminFolderPath);
                        }
                    }

                    // 4) 성공 → 백업 삭제 (95~100%)
                    progress.SetPercent(95);
                    progress.SetMessage("마무리 중(백업 삭제)...");

                    int bakTotal = backupMap.Count;
                    int bakIndex = 0;

                    foreach (var kv in backupMap)
                    {
                        ct.ThrowIfCancellationRequested();

                        bakIndex++;
                        int pct = 95 + (int)(bakIndex * 5.0 / Math.Max(1, bakTotal));
                        progress.SetPercent(pct);
                        progress.SetMessage($"[정리] 백업 삭제 ({bakIndex}/{bakTotal})");

                        if (File.Exists(kv.Value))
                            File.Delete(kv.Value);
                    }

                    return new ApplyResult { Success = true };
                }
                catch (OperationCanceledException)
                {
                    // 취소도 "실패"로 보고하되, 롤백은 동일하게 수행
                    Rollback(backupMap, progress);
                    return new ApplyResult { Success = false, FailMessage = "작업이 취소되었습니다. (롤백 완료)" };
                }
                catch (Exception ex)
                {
                    Rollback(backupMap, progress);
                    return new ApplyResult { Success = false, FailMessage = ex.Message };
                }
            }, ct);
        }

        private static void Rollback(Dictionary<string, string> backupMap, IUiProgress progress)
        {
            progress.SetPercent(95);
            progress.SetMessage("오류 발생 - 롤백 중...");

            int bakTotal = backupMap.Count;
            int bakIndex = 0;

            foreach (var kv in backupMap)
            {
                bakIndex++;
                progress.SetMessage($"[롤백] ({bakIndex}/{bakTotal})");

                var adminPath = kv.Key;
                var bakPath = kv.Value;

                try
                {
                    if (File.Exists(adminPath))
                        File.Delete(adminPath);

                    if (File.Exists(bakPath))
                        File.Move(bakPath, adminPath, overwrite: true);
                }
                catch
                {
                    // 롤백 실패는 무시
                }
            }
        }

        /// <summary>
        /// S vs A, S vs U를 비교해 충돌(상품단위)이 없으면 U 변경분을 A에 반영한다.
        /// (충돌 시: 예외 메시지에 "충돌 발생 폴더명" 포함)
        /// </summary>
        public (bool hasChanges, int added, int deleted, int modified)
            MergeUserChangesToAdmin(string standardFilePath, string userFilePath, string adminFolderPath)
        {
            var fileName = Path.GetFileName(standardFilePath);
            var adminFilePath = Path.Combine(adminFolderPath, fileName);

            // fileKeyConfig.json 에 정의된 파일만 머지 대상
            if (!FileKeyManager.TryGetFileSettings(fileName, out var delimiter, out var keyColumns) ||
                keyColumns == null || keyColumns.Count == 0)
            {
                throw new InvalidOperationException(
                    $"fileKeyConfig.json에 키 컬럼이 정의되지 않은 파일입니다.\n파일명: {fileName}");
            }

            // 상품 단위 충돌 체크용 dupKeyColumns (없으면 keyColumns 사용)
            var dupKeyColumns = FileKeyManager.GetDupKeyColumnsOrDefault(fileName);
            if (dupKeyColumns == null || dupKeyColumns.Count == 0)
                dupKeyColumns = new List<string>(keyColumns);

            // 1) Standard vs Admin (S vs A) 변경 키셋
            var diffStdAdmin = DiffService.CompareByFileKeys(standardFilePath, adminFilePath);
            var adminChangedKeys = BuildChangedKeySet(diffStdAdmin);

            // 2) Standard vs User (S vs U) 변경 키셋
            var diffStdUser = DiffService.CompareByFileKeys(standardFilePath, userFilePath);
            var userChangedKeys = BuildChangedKeySet(diffStdUser);

            // 적용할 변경이 없으면 파일 변경 없음으로 처리
            if (userChangedKeys.Count == 0)
                return (false, diffStdUser.Added, diffStdUser.Deleted, diffStdUser.Modified);

            // 3) 충돌 상품 키셋 = 관리자 변경 상품 키 ∩ 사용자 변경 상품 키 (dupKeyColumns 기준)
            var adminProductKeys = DiffService.ProjectKeysToDupKeys(adminChangedKeys, keyColumns, dupKeyColumns);
            var userProductKeys = DiffService.ProjectKeysToDupKeys(userChangedKeys, keyColumns, dupKeyColumns);

            var conflictKeys = adminProductKeys.Intersect(userProductKeys).ToList();
            if (conflictKeys.Count > 0)
            {
                // 충돌 발생한 "사용자 폴더명" (예: 12345678 또는 12345678_OK)
                var userFolderName = Path.GetFileName(Path.GetDirectoryName(userFilePath) ?? string.Empty);
                if (string.IsNullOrWhiteSpace(userFolderName))
                    userFolderName = "(알 수 없음)";

                var sbConflict = new StringBuilder();
                sbConflict.AppendLine("다른 사번이 수정한 동일 특약에 대해 중복 수정은 허용되지 않습니다.(상품단위 충돌)");
                sbConflict.AppendLine();
                sbConflict.AppendLine($"충돌 발생 폴더: {userFolderName}");
                sbConflict.AppendLine($"파일명: {fileName}");
                sbConflict.AppendLine();
                sbConflict.AppendLine($"충돌 상품 Key 개수: {conflictKeys.Count}");
                sbConflict.AppendLine();
                sbConflict.AppendLine("충돌 상품 Key 상세(최대 10개):");

                bool headerPrinted = false;

                foreach (var key in conflictKeys.Take(10))
                {
                    var parts = key.Split(FileKeyManager.UnitSeparator);

                    if (!headerPrinted)
                    {
                        var header = string.Join(" / ", dupKeyColumns);
                        sbConflict.AppendLine(" - " + header);
                        headerPrinted = true;
                    }

                    var values = new List<string>();
                    for (int i = 0; i < dupKeyColumns.Count; i++)
                    {
                        string colVal = (i < parts.Length ? parts[i] : string.Empty);
                        values.Add(colVal);
                    }

                    sbConflict.AppendLine(" - " + string.Join(" / ", values));
                }

                if (conflictKeys.Count > 10)
                    sbConflict.AppendLine($"…외 {conflictKeys.Count - 10}건");

                throw new InvalidOperationException(sbConflict.ToString());
            }

            // 4) 머지 수행: 관리자 파일을 테이블로 로드
            var adminTable = DiffService.LoadAsTable(adminFilePath);
            if (adminTable.Count == 0)
            {
                throw new InvalidOperationException(
                    $"관리자 파일 데이터가 비어 있어 적용할 수 없습니다. 파일명: {fileName}");
            }

            // 첫 행은 헤더로 간주
            var headerRow = adminTable[0] ?? new List<string>();
            var headerArr = headerRow.Select(s => s ?? string.Empty).ToArray();

            // 관리자 테이블 → Key → Row 맵 구성
            var adminMap = new Dictionary<string, List<string>>();
            for (int i = 1; i < adminTable.Count; i++)
            {
                var row = adminTable[i] ?? new List<string>();
                var rowArr = row.Select(s => s ?? string.Empty).ToArray();

                var key = FileKeyManager.BuildCompositeKey(headerArr, rowArr, keyColumns);
                adminMap[key] = row;  // 마지막 행 우선
            }

            // 삭제
            foreach (var del in diffStdUser.DeletedRows)
            {
                if (!string.IsNullOrEmpty(del.Key))
                    adminMap.Remove(del.Key);
            }

            // 추가
            foreach (var add in diffStdUser.AddedRows)
            {
                var row = (add.RightRow ?? Array.Empty<string>()).ToList();
                adminMap[add.Key] = row;
            }

            // 수정
            foreach (var mod in diffStdUser.ModifiedRows)
            {
                var row = (mod.RightRow ?? Array.Empty<string>()).ToList();
                adminMap[mod.Key] = row;
            }

            // 키 기준 정렬 + full line 2차 정렬
            var mergedRows = adminMap
                .OrderBy(
                    kv => kv,
                    Comparer<KeyValuePair<string, List<string>>>.Create((a, b) =>
                        CompareCompositeKey(
                            a.Key,
                            b.Key,
                            keyColumns,
                            string.Join("|", a.Value),
                            string.Join("|", b.Value)
                        )
                    )
                )
                .Select(kv => kv.Value)
                .ToList();

            var finalTable = new List<List<string>> { headerRow };
            finalTable.AddRange(mergedRows);

            // CSV/IO 파일로 저장
            SaveCsvOrIoFile(adminFilePath, finalTable);

            return (true, diffStdUser.Added, diffStdUser.Deleted, diffStdUser.Modified);
        }

        /// <summary>
        /// diff 결과(Added/Deleted/Modified)에 등장한 모든 Key 집합(충돌 검사용)
        /// </summary>
        private static HashSet<string> BuildChangedKeySet(DiffResult diff)
        {
            var set = new HashSet<string>();

            foreach (var r in diff.AddedRows)
                if (!string.IsNullOrEmpty(r.Key)) set.Add(r.Key);

            foreach (var r in diff.DeletedRows)
                if (!string.IsNullOrEmpty(r.Key)) set.Add(r.Key);

            foreach (var r in diff.ModifiedRows)
                if (!string.IsNullOrEmpty(r.Key)) set.Add(r.Key);

            return set;
        }

        /// <summary>
        /// 키 정렬: keyColumns 순서대로 비교(날짜는 날짜 우선), 동률이면 full line 2차 비교
        /// </summary>
        private static int CompareCompositeKey(
            string keyX,
            string keyY,
            IReadOnlyList<string> keyColumns,
            string fullLineX,
            string fullLineY)
        {
            var xParts = (keyX ?? string.Empty).Split(FileKeyManager.UnitSeparator);
            var yParts = (keyY ?? string.Empty).Split(FileKeyManager.UnitSeparator);

            // 1) keyColumns 순서대로 키 비교
            for (int i = 0; i < keyColumns.Count; i++)
            {
                string xv = i < xParts.Length ? xParts[i] ?? string.Empty : string.Empty;
                string yv = i < yParts.Length ? yParts[i] ?? string.Empty : string.Empty;

                int result;

                // 1-1 날짜 형태일 경우 날짜로 비교
                var dx = DateUtil.ParseAnyYmdOrNull(xv);
                var dy = DateUtil.ParseAnyYmdOrNull(yv);

                if (dx != null || dy != null)
                {
                    // 둘 중 하나라도 날짜로 해석되면 날짜 우선 비교
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

            // 2) 모든 키값이 동일한 경우 >> full line 전체 비교로 2차정렬
            return string.CompareOrdinal(fullLineX ?? string.Empty, fullLineY ?? string.Empty);
        }

        /// <summary>
        /// 파일 확장자에 따라 저장(io/csv)
        /// </summary>
        private static void SaveCsvOrIoFile(string path, List<List<string>> csvData)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();

            if (ext == FileConstants.Extensions.Io)
            {
                // IO 파일: CSV 테이블을 IO 포맷으로 변환하여 저장
                Header header = IIO.IOHeaderInfo(path);
                IIO.SaveCsvtoIO(path, header, csvData);
            }
            else
            {
                // 그 외: CSV 파일로 저장 (공통 유틸 사용)
                FileUtil.WriteCsvLines(csvData, path);
            }
        }
    }
}
