// Common/Services/DiffKit.cs
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace InnoPVManagementSystem.Common.Services
{
    /// <summary>
    /// 대용량 친화 파일 비교 유틸리티:
    /// - ReadLinesAsync: 스트리밍 라인 열거
    /// - CountLinesAsync: 스트리밍 라인 카운트(배열 미생성)
    /// - CompareFilesExternalAsync: 외부 정렬+머지로 공통/차집합 카운트(메모리 절약)
    /// - CompareFilesInMemoryAsync: 경량 파일용 인메모리 비교
    /// </summary>
    public static class DiffKit
    {
        // ========== 1) 스트리밍 라인 리더 ==========
        public static async IAsyncEnumerable<string> ReadLinesAsync(
            string path,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            const int BUFSIZE = 1 << 16; // 64KB
            using var fs = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                BUFSIZE, FileOptions.SequentialScan | FileOptions.Asynchronous);

            using var sr = new StreamReader(fs, Encoding.UTF8, true, BUFSIZE, leaveOpen: false);
            while (!sr.EndOfStream)
            {
                ct.ThrowIfCancellationRequested();
                var line = await sr.ReadLineAsync();
                if (line is not null)
                    yield return line;
            }
        }

        // ========== 2) 스트리밍 라인 카운터 ==========
        public static async Task<long> CountLinesAsync(string path, CancellationToken ct = default)
        {
            const int BUFSIZE = 1 << 16;
            var buffer = ArrayPool<byte>.Shared.Rent(BUFSIZE);
            try
            {
                long lines = 0;
                using var fs = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    BUFSIZE, FileOptions.SequentialScan | FileOptions.Asynchronous);

                int read;
                while ((read = await fs.ReadAsync(buffer.AsMemory(0, BUFSIZE), ct)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    for (int i = 0; i < read; i++)
                        if (buffer[i] == (byte)'\n') lines++;
                }
                return lines; // 개행 없는 마지막 줄을 별도 +1 하지 않는 정책
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // ========== 3) 외부 정렬 + 머지 기반 비교 ==========
        public sealed class ExternalCompareResult
        {
            public long CommonCount { get; init; }
            public long OnlyLeftCount { get; init; }
            public long OnlyRightCount { get; init; }

            public string? LeftOnlySamplePath { get; init; }
            public string? RightOnlySamplePath { get; init; }
            public string? CommonSamplePath { get; init; }
        }

        public static async Task<ExternalCompareResult> CompareFilesExternalAsync(
            string leftPath,
            string rightPath,
            int chunkLineLimit = 500_000,
            int sampleKeep = 2_000,
            CancellationToken ct = default)
        {
            var tempDir = CreateTempWorkDir();
            var leftChunks = await SortToChunksAsync(leftPath, tempDir, "L", chunkLineLimit, ct);
            var rightChunks = await SortToChunksAsync(rightPath, tempDir, "R", chunkLineLimit, ct);

            var leftSortedPath = Path.Combine(tempDir, "left.sorted.txt");
            var rightSortedPath = Path.Combine(tempDir, "right.sorted.txt");
            await MergeChunksAsync(leftChunks, leftSortedPath, ct);
            await MergeChunksAsync(rightChunks, rightSortedPath, ct);

            var commonSample = Path.Combine(tempDir, "common.sample.txt");
            var onlyLsample = Path.Combine(tempDir, "leftonly.sample.txt");
            var onlyRsample = Path.Combine(tempDir, "rightonly.sample.txt");

            long common = 0, onlyL = 0, onlyR = 0;
            int commonW = 0, onlyLW = 0, onlyRW = 0;

            await using var lfs = new FileStream(leftSortedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            await using var rfs = new FileStream(rightSortedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var lr = new StreamReader(lfs, Encoding.UTF8);
            using var rr = new StreamReader(rfs, Encoding.UTF8);

            await using var cw = new StreamWriter(commonSample, false, Encoding.UTF8);
            await using var lw = new StreamWriter(onlyLsample, false, Encoding.UTF8);
            await using var rw = new StreamWriter(onlyRsample, false, Encoding.UTF8);

            string? l = await lr.ReadLineAsync();
            string? r = await rr.ReadLineAsync();

            while (l is not null && r is not null)
            {
                ct.ThrowIfCancellationRequested();
                var cmp = string.CompareOrdinal(l, r);
                if (cmp == 0)
                {
                    common++;
                    if (commonW < sampleKeep) { await cw.WriteLineAsync(l); commonW++; }
                    l = await lr.ReadLineAsync();
                    r = await rr.ReadLineAsync();
                }
                else if (cmp < 0)
                {
                    onlyL++;
                    if (onlyLW < sampleKeep) { await lw.WriteLineAsync(l); onlyLW++; }
                    l = await lr.ReadLineAsync();
                }
                else
                {
                    onlyR++;
                    if (onlyRW < sampleKeep) { await rw.WriteLineAsync(r); onlyRW++; }
                    r = await rr.ReadLineAsync();
                }
            }
            while (l is not null)
            {
                ct.ThrowIfCancellationRequested();
                onlyL++;
                if (onlyLW < sampleKeep) { await lw.WriteLineAsync(l); onlyLW++; }
                l = await lr.ReadLineAsync();
            }
            while (r is not null)
            {
                ct.ThrowIfCancellationRequested();
                onlyR++;
                if (onlyRW < sampleKeep) { await rw.WriteLineAsync(r); onlyRW++; }
                r = await rr.ReadLineAsync();
            }

            return new ExternalCompareResult
            {
                CommonCount = common,
                OnlyLeftCount = onlyL,
                OnlyRightCount = onlyR,
                CommonSamplePath = commonSample,
                LeftOnlySamplePath = onlyLsample,
                RightOnlySamplePath = onlyRsample
            };

            static string CreateTempWorkDir()
            {
                var dir = Path.Combine(Path.GetTempPath(), "InnoPV_Diff_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        private static async Task<List<string>> SortToChunksAsync(
            string path, string tempDir, string prefix, int chunkLineLimit, CancellationToken ct)
        {
            var list = new List<string>(chunkLineLimit);
            var chunkFiles = new List<string>();
            int chunkNo = 0;

            await foreach (var line in ReadLinesAsync(path, ct))
            {
                ct.ThrowIfCancellationRequested();
                list.Add(line);
                if (list.Count >= chunkLineLimit)
                    await FlushChunkAsync(list, tempDir, prefix, ++chunkNo, chunkFiles, ct);
            }
            if (list.Count > 0)
                await FlushChunkAsync(list, tempDir, prefix, ++chunkNo, chunkFiles, ct);

            return chunkFiles;

            static async Task FlushChunkAsync(
                List<string> buffer, string dir, string prefix, int no, List<string> sinks, CancellationToken ct)
            {
                buffer.Sort(StringComparer.Ordinal);
                var chunkPath = Path.Combine(dir, $"{prefix}.chunk.{no:0000}.txt");
                await File.WriteAllLinesAsync(chunkPath, buffer, Encoding.UTF8, ct);
                sinks.Add(chunkPath);
                buffer.Clear();
            }
        }

        private static async Task MergeChunksAsync(List<string> chunkFiles, string outputPath, CancellationToken ct)
        {
            if (chunkFiles.Count == 0)
            {
                using var _ = File.Create(outputPath);
                return;
            }

            var queue = new Queue<string>(chunkFiles);
            while (queue.Count > 1)
            {
                ct.ThrowIfCancellationRequested();
                var a = queue.Dequeue();
                var b = queue.Dequeue();

                var merged = Path.Combine(Path.GetDirectoryName(outputPath)!, $"merge_{Guid.NewGuid():N}.txt");
                await MergeTwoAsync(a, b, merged, ct);

                TryDelete(a);
                TryDelete(b);

                queue.Enqueue(merged);
            }

            var last = queue.Dequeue();
            if (File.Exists(outputPath)) TryDelete(outputPath);
            File.Move(last, outputPath);

            static async Task MergeTwoAsync(string f1, string f2, string dst, CancellationToken ct)
            {
                await using var ofs = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None);
                await using var ow = new StreamWriter(ofs, Encoding.UTF8);

                await using var aFs = new FileStream(f1, FileMode.Open, FileAccess.Read, FileShare.Read);
                await using var bFs = new FileStream(f2, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var ar = new StreamReader(aFs, Encoding.UTF8);
                using var br = new StreamReader(bFs, Encoding.UTF8);

                string? la = await ar.ReadLineAsync();
                string? lb = await br.ReadLineAsync();

                while (la is not null && lb is not null)
                {
                    ct.ThrowIfCancellationRequested();
                    var cmp = string.CompareOrdinal(la, lb);
                    if (cmp <= 0)
                    {
                        await ow.WriteLineAsync(la);
                        la = await ar.ReadLineAsync();
                    }
                    else
                    {
                        await ow.WriteLineAsync(lb);
                        lb = await br.ReadLineAsync();
                    }
                }
                while (la is not null) { await ow.WriteLineAsync(la); la = await ar.ReadLineAsync(); }
                while (lb is not null) { await ow.WriteLineAsync(lb); lb = await br.ReadLineAsync(); }
            }

            static void TryDelete(string path)
            {
                try { File.Delete(path); } catch { /* ignore */ }
            }
        }

        // ========== 4) 인메모리 비교(경량 전용) ==========
        public sealed class InMemoryCompareResult
        {
            public long CommonCount { get; init; }
            public long OnlyLeftCount { get; init; }
            public long OnlyRightCount { get; init; }
        }

        public static async Task<InMemoryCompareResult> CompareFilesInMemoryAsync(
            string leftPath, string rightPath, CancellationToken ct = default)
        {
            var left = await File.ReadAllLinesAsync(leftPath, ct);
            var right = await File.ReadAllLinesAsync(rightPath, ct);

            var lset = new HashSet<string>(left, StringComparer.Ordinal);
            var rset = new HashSet<string>(right, StringComparer.Ordinal);

            long common = 0;
            foreach (var s in lset) if (rset.Contains(s)) common++;

            long onlyL = lset.Count - common;
            long onlyR = rset.Count - common;

            return new InMemoryCompareResult
            {
                CommonCount = common,
                OnlyLeftCount = onlyL,
                OnlyRightCount = onlyR
            };
        }
    }
}
