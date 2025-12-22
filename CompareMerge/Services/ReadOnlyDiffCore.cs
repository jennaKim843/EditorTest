using DiffPlex;
using DiffPlex.Chunkers;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using InnoPVManagementSystem.Common.Services;
using InnoPVManagementSystem.Modules.CompareMerge.Views;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace InnoPVManagementSystem.Modules.CompareMerge.Services
{
    /// <summary>
    /// ReadOnlyDiffView에서 사용하는
    /// - 비교
    /// - 키 기반 정렬
    /// - 인라인 토큰화
    /// 를 담당하는 순수 로직 모음(UI 의존 최소).
    /// </summary>
    internal static class ReadOnlyDiffCore
    {
        // ---------------- Public API ----------------

        /// <summary>
        /// fileKeyConfig 설정을 기반으로
        /// 키 기반 비교 컨텍스트를 생성한다.
        /// (키 설정이 없거나 파싱 불가 시 null)
        /// </summary>
        public static KeyDiffContext? TryCreateKeyContext(string? fileName, string fullText)
            => KeyDiffContext.TryCreate(fileName, fullText);

        /// <summary>
        /// 키 기준으로 좌/우 라인을 정렬/매칭하여
        /// DiffPiece 라인 목록을 생성한다.
        /// (헤더/구분선 유지)
        /// </summary>
        public static (IList<DiffPiece> leftLines, IList<DiffPiece> rightLines)
            BuildKeyAlignedLines(KeyDiffContext ctx, string leftText, string rightText)
        {
            static string[] SplitLines(string text) =>
                text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            var leftArr = SplitLines(leftText);
            var rightArr = SplitLines(rightText);

            var leftResult = new List<DiffPiece>();
            var rightResult = new List<DiffPiece>();

            // 1) 헤더 1행
            string leftHeader = leftArr.Length > 0 ? leftArr[0] : string.Empty;
            string rightHeader = rightArr.Length > 0 ? rightArr[0] : leftHeader;

            if (!string.IsNullOrEmpty(leftHeader) || !string.IsNullOrEmpty(rightHeader))
            {
                leftResult.Add(new DiffPiece(leftHeader, ChangeType.Unchanged));
                rightResult.Add(new DiffPiece(rightHeader, ChangeType.Unchanged));
            }

            int dataStart = 1;

            // 2) 구분선(2행)이 좌/우 동일하면 유지
            if (leftArr.Length > 1 || rightArr.Length > 1)
            {
                string l2 = leftArr.Length > 1 ? leftArr[1] : string.Empty;
                string r2 = rightArr.Length > 1 ? rightArr[1] : l2;

                if (string.Equals(l2, r2, StringComparison.Ordinal))
                {
                    leftResult.Add(new DiffPiece(l2, ChangeType.Unchanged));
                    rightResult.Add(new DiffPiece(r2, ChangeType.Unchanged));
                    dataStart = 2;
                }
            }

            // 3) 데이터 영역 키 기반 merge-join
            var leftData = ctx.BuildKeyedLines(leftArr, dataStart);
            var rightData = ctx.BuildKeyedLines(rightArr, dataStart);

            int i = 0, j = 0;
            while (i < leftData.Count || j < rightData.Count)
            {
                if (i >= leftData.Count)
                {
                    var r = rightData[j++];
                    leftResult.Add(new DiffPiece(string.Empty, ChangeType.Imaginary));
                    rightResult.Add(new DiffPiece(r.LineText, ChangeType.Inserted));
                    continue;
                }

                if (j >= rightData.Count)
                {
                    var l = leftData[i++];
                    leftResult.Add(new DiffPiece(l.LineText, ChangeType.Deleted));
                    rightResult.Add(new DiffPiece(string.Empty, ChangeType.Imaginary));
                    continue;
                }

                var lcur = leftData[i];
                var rcur = rightData[j];

                int cmp = string.Compare(lcur.Key, rcur.Key, StringComparison.Ordinal);
                if (cmp == 0)
                {
                    bool modified = ctx.HasNonKeyDiff(lcur.LineText, rcur.LineText);
                    var type = modified ? ChangeType.Modified : ChangeType.Unchanged;

                    leftResult.Add(new DiffPiece(lcur.LineText, type));
                    rightResult.Add(new DiffPiece(rcur.LineText, type));

                    i++;
                    j++;
                }
                else if (cmp < 0)
                {
                    var l = leftData[i++];
                    leftResult.Add(new DiffPiece(l.LineText, ChangeType.Deleted));
                    rightResult.Add(new DiffPiece(string.Empty, ChangeType.Imaginary));
                }
                else
                {
                    var r = rightData[j++];
                    leftResult.Add(new DiffPiece(string.Empty, ChangeType.Imaginary));
                    rightResult.Add(new DiffPiece(r.LineText, ChangeType.Inserted));
                }
            }

            return (leftResult, rightResult);
        }

        /// <summary>
        /// 라인 diff 결과를 기반으로
        /// 인라인(Word / Character / Pipe) DiffPiece를 생성한다.
        /// (None 모드일 경우 null 반환)
        /// </summary>
        public static (List<IList<DiffPiece>>? left, List<IList<DiffPiece>>? right)
            BuildInlineSubPieces(
                ReadOnlyDiffView.IntraLineMode mode,
                IList<DiffPiece> leftLines,
                IList<DiffPiece> rightLines,
                string leftDisplay,
                string rightDisplay,
                Differ differ)
        {
            if (mode == ReadOnlyDiffView.IntraLineMode.None)
                return (null, null);

            int count = Math.Max(leftLines.Count, rightLines.Count);

            var subsLeft = new List<IList<DiffPiece>>(count);
            var subsRight = new List<IList<DiffPiece>>(count);

            var leftArr = leftDisplay.Split('\n');
            var rightArr = rightDisplay.Split('\n');

            InlineDiffBuilder? inline = (mode == ReadOnlyDiffView.IntraLineMode.Word || mode == ReadOnlyDiffView.IntraLineMode.Character)
                ? new InlineDiffBuilder(differ)
                : null;

            for (int i = 0; i < count; i++)
            {
                subsLeft.Add(Array.Empty<DiffPiece>());
                subsRight.Add(Array.Empty<DiffPiece>());

                DiffPiece? lp = i < leftLines.Count ? leftLines[i] : null;
                DiffPiece? rp = i < rightLines.Count ? rightLines[i] : null;

                if (lp == null || rp == null) continue;
                if (!IsModifiedLine(lp, rp)) continue;

                var l = i < leftArr.Length ? TrimCR(leftArr[i]) : string.Empty;
                var r = i < rightArr.Length ? TrimCR(rightArr[i]) : string.Empty;

                switch (mode)
                {
                    case ReadOnlyDiffView.IntraLineMode.Pipe:
                        {
                            var (L, R) = BuildPipePiecesForSides(l, r);
                            subsLeft[i] = L;
                            subsRight[i] = R;
                            break;
                        }
                    case ReadOnlyDiffView.IntraLineMode.Word:
                    case ReadOnlyDiffView.IntraLineMode.Character:
                        {
                            IChunker chunker = (mode == ReadOnlyDiffView.IntraLineMode.Character)
                                ? CharacterChunker.Instance
                                : WordChunker.Instance;

                            var model = inline!.BuildDiffModel(l, r, ignoreWhitespace: false, ignoreCase: false, chunker);
                            if (model?.Lines == null) break;

                            subsLeft[i] = MapForLeft(model.Lines);
                            subsRight[i] = MapForRight(model.Lines);
                            break;
                        }
                }
            }

            return (subsLeft, subsRight);
        }

        /// <summary>
        /// DiffPiece 라인 목록을
        /// 에디터 표시용 문자열로 변환한다.
        /// (빈 줄은 공백 1개로 보정)
        /// </summary>
        public static string BuildAligned(IList<DiffPiece> lines, int reserve)
        {
            var sb = new StringBuilder(reserve + 64);

            for (int i = 0; i < lines.Count; i++)
            {
                var t = lines[i]?.Text;

                if (string.IsNullOrEmpty(t))
                    t = " ";

                sb.Append(t);

                if (i < lines.Count - 1)
                    sb.Append('\n');
            }

            return sb.ToString();
        }

        /// <summary>
        /// Diff 결과를 기준으로
        /// 원본 라인 번호 목록을 생성한다.
        /// (Imaginary 라인은 null)
        /// </summary>
        public static IList<int?> BuildOriginalLineNumbers(IList<DiffPiece> lines)
        {
            var list = new List<int?>(lines.Count);
            int current = 0;

            for (int i = 0; i < lines.Count; i++)
            {
                var p = lines[i];

                if (p.Type == ChangeType.Imaginary)
                    list.Add(null);
                else
                {
                    current++;
                    list.Add(current);
                }
            }

            return list;
        }

        /// <summary>
        /// 라인번호 목록에서
        /// 최대값 기준 자리수를 계산한다.
        /// (최소 2자리)
        /// </summary>
        public static int DigitCountFromMax(IList<int?> nums)
        {
            int max = 0;
            foreach (var n in nums)
                if (n.HasValue && n.Value > max) max = n.Value;

            if (max <= 0) return 2;

            int d = 0;
            while (max > 0) { d++; max /= 10; }
            return Math.Max(2, d);
        }

        /// <summary>
        /// DiffPiece 라인 목록에서
        /// 특정 ChangeType 개수를 계산한다.
        /// </summary>
        public static int CountType(IList<DiffPiece> lines, ChangeType type)
        {
            int n = 0;
            foreach (var line in lines)
                if (line.Type == type) n++;
            return n;
        }

        /// <summary>
        /// 좌/우 중 하나라도 Modified 타입이면
        /// 수정 라인으로 판단한다.
        /// </summary>
        public static bool IsModifiedLine(DiffPiece? left, DiffPiece? right)
        {
            var lt = left?.Type ?? ChangeType.Unchanged;
            var rt = right?.Type ?? ChangeType.Unchanged;
            return lt == ChangeType.Modified || rt == ChangeType.Modified;
        }


        // ---------------- Internal helpers ----------------

        /// <summary>
        /// CRLF 계열 줄바꿈에서
        /// 라인 끝 CR 문자만 제거한다.
        /// </summary>
        private static string TrimCR(string s)
            => (!string.IsNullOrEmpty(s) && s.EndsWith("\r")) ? s[..^1] : (s ?? string.Empty);

        // <summary>
        /// 파이프(|) 기준으로 라인을 토큰화하여
        /// 컬럼 단위 DiffPiece를 생성한다.
        /// (Modified / Unchanged만 사용)
        /// </summary>
        private static (IList<DiffPiece> leftPieces, IList<DiffPiece> rightPieces)
            BuildPipePiecesForSides(string leftLine, string rightLine)
        {
            var ltoks = leftLine.Split('|');
            var rtoks = rightLine.Split('|');

            int n = Math.Max(ltoks.Length, rtoks.Length);
            var lres = new List<DiffPiece>(n * 2);
            var rres = new List<DiffPiece>(n * 2);

            for (int idx = 0; idx < n; idx++)
            {
                string la = idx < ltoks.Length ? ltoks[idx] : string.Empty;
                string ra = idx < rtoks.Length ? rtoks[idx] : string.Empty;

                bool same = string.Equals(la, ra, StringComparison.Ordinal);

                lres.Add(new DiffPiece(la, same ? ChangeType.Unchanged : ChangeType.Modified));
                rres.Add(new DiffPiece(ra, same ? ChangeType.Unchanged : ChangeType.Modified));

                bool needSep = (idx < ltoks.Length - 1) || (idx < rtoks.Length - 1);
                if (needSep)
                {
                    lres.Add(new DiffPiece("|", ChangeType.Unchanged));
                    rres.Add(new DiffPiece("|", ChangeType.Unchanged));
                }
            }
            return (lres, rres);
        }

        /// <summary>
        /// 인라인 Diff 모델을
        /// 왼쪽 기준 DiffPiece 목록으로 변환한다.
        /// (Inserted → Unchanged 처리)
        /// </summary>
        private static IList<DiffPiece> MapForLeft(IList<DiffPiece> src)
        {
            if (src.Count == 0) return Array.Empty<DiffPiece>();
            var arr = new DiffPiece[src.Count];
            for (int i = 0; i < src.Count; i++)
            {
                var s = src[i];
                var t = (s.Type == ChangeType.Inserted) ? ChangeType.Unchanged : s.Type;
                arr[i] = new DiffPiece(s.Text ?? "", t, s.Position);
            }
            return arr;
        }

        /// <summary>
        /// 인라인 Diff 모델을
        /// 오른쪽 기준 DiffPiece 목록으로 변환한다.
        /// (Deleted → Unchanged 처리)
        /// </summary>
        private static IList<DiffPiece> MapForRight(IList<DiffPiece> src)
        {
            if (src.Count == 0) return Array.Empty<DiffPiece>();
            var arr = new DiffPiece[src.Count];
            for (int i = 0; i < src.Count; i++)
            {
                var s = src[i];
                var t = (s.Type == ChangeType.Deleted) ? ChangeType.Unchanged : s.Type;
                arr[i] = new DiffPiece(s.Text ?? "", t, s.Position);
            }
            return arr;
        }

        // ---------------- KeyDiffContext ----------------
        /// <summary>
        /// 키 기반 diff에 필요한 정보를 캡슐화한 컨텍스트.
        /// - 구분자
        /// - 키 컬럼 인덱스
        /// - 헤더 컬럼 맵
        /// </summary>
        internal sealed class KeyDiffContext
        {
            public string Delimiter { get; }
            public int[] KeyIndexes { get; }
            public HashSet<int> KeyIndexSet { get; }
            public Dictionary<string, int> HeaderIndexMap { get; }

            private KeyDiffContext(string delimiter, int[] keyIndexes, HashSet<int> keyIndexSet, Dictionary<string, int> headerIndexMap)
            {
                Delimiter = delimiter;
                KeyIndexes = keyIndexes;
                KeyIndexSet = keyIndexSet;
                HeaderIndexMap = headerIndexMap;
            }

            /// <summary>
            /// 파일명과 헤더 정보를 분석하여
            /// 키 기반 비교 컨텍스트를 생성한다.
            /// (키 설정이 없으면 null)
            /// </summary>
            public static KeyDiffContext? TryCreate(string? fileName, string fullText)
            {
                if (string.IsNullOrEmpty(fileName) || string.IsNullOrEmpty(fullText))
                    return null;

                var (delimiter, keys) = FileKeyManager.GetFileSettingsOrDefault(fileName, "|");
                var delim = string.IsNullOrEmpty(delimiter) ? "|" : delimiter;
                if (keys == null || keys.Count == 0) return null;

                int idx = fullText.IndexOfAny(new[] { '\r', '\n' });
                string headerLine = idx >= 0 ? fullText.Substring(0, idx) : fullText;
                if (string.IsNullOrEmpty(headerLine)) return null;

                var headers = SplitSafe(headerLine, delim);

                var headerMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < headers.Length; i++)
                {
                    var name = headers[i]?.Trim() ?? $"col{i}";
                    if (!headerMap.ContainsKey(name))
                        headerMap[name] = i;
                }

                var idxList = new List<int>();
                foreach (var col in keys)
                {
                    if (headerMap.TryGetValue(col, out int colIdx))
                        idxList.Add(colIdx);
                }

                if (idxList.Count == 0) return null;

                var idxArray = idxList.ToArray();
                return new KeyDiffContext(delim, idxArray, new HashSet<int>(idxArray), headerMap);
            }

            /// <summary>
            /// 헤더 이후 데이터 라인들을
            /// (Key, LineText) 형태로 변환한다.
            /// </summary>
            public List<(string Key, string LineText)> BuildKeyedLines(string[] lines, int startIndex)
            {
                var list = new List<(string, string)>();

                for (int i = startIndex; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (IsHeaderLine(line)) continue;

                    string key = GetKeyFromLine(line);
                    if (string.IsNullOrEmpty(key)) continue;

                    list.Add((key, line));
                }

                return list;
            }

            /// <summary>
            /// 키 컬럼을 제외한 값 중
            /// 하나라도 다르면 true를 반환한다.
            /// </summary>
            public bool HasNonKeyDiff(string leftText, string rightText)
            {
                var leftCols = SplitSafe(leftText, Delimiter);
                var rightCols = SplitSafe(rightText, Delimiter);
                int maxLen = Math.Max(leftCols.Length, rightCols.Length);

                for (int colIndex = 0; colIndex < maxLen; colIndex++)
                {
                    if (KeyIndexSet.Contains(colIndex))
                        continue;

                    string lv = colIndex < leftCols.Length ? (leftCols[colIndex] ?? "").Trim() : string.Empty;
                    string rv = colIndex < rightCols.Length ? (rightCols[colIndex] ?? "").Trim() : string.Empty;

                    if (!string.Equals(lv, rv, StringComparison.Ordinal))
                        return true;
                }

                return false;
            }

            /// <summary>
            /// 라인이 헤더처럼 보이는지 여부를 판단한다.
            /// (헤더 컬럼명이 일정 비율 이상 포함된 경우)
            /// </summary>
            private bool IsHeaderLine(string lineText)
            {
                var cols = SplitSafe(lineText, Delimiter);
                if (cols.Length == 0) return false;

                int headerLikeCount = 0;
                foreach (var c in cols)
                {
                    var name = (c ?? string.Empty).Trim();
                    if (name.Length == 0) continue;
                    if (HeaderIndexMap.ContainsKey(name))
                        headerLikeCount++;
                }

                return headerLikeCount >= Math.Max(1, cols.Length / 2);
            }

            /// <summary>
            /// 한 행에서 키 컬럼 값을 추출하여
            /// 비교용 Key 문자열을 생성한다.
            /// </summary>
            private string GetKeyFromLine(string line)
            {
                var cols = SplitSafe(line, Delimiter);
                if (cols.Length == 0) return string.Empty;

                var parts = new string[KeyIndexes.Length];
                for (int i = 0; i < KeyIndexes.Length; i++)
                {
                    int idx = KeyIndexes[i];
                    parts[i] = (idx >= 0 && idx < cols.Length) ? (cols[idx] ?? "").Trim() : string.Empty;
                }

                return string.Join(FileKeyManager.UnitSeparator, parts);
            }

            /// <summary>
            /// null/빈 구분자까지 고려한
            /// 안전한 Split 유틸 메서드.
            /// </summary>
            private static string[] SplitSafe(string line, string delimiter)
            {
                if (line == null) return Array.Empty<string>();
                if (string.IsNullOrEmpty(delimiter)) return line.Split('|');
                return line.Split(new[] { delimiter }, StringSplitOptions.None);
            }
        }
    }
}
