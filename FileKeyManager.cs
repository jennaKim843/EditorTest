using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Threading.Tasks;

namespace InnoPVManagementSystem.Common.Services
{
    /// <summary>
    /// fileKeyConfig.json 로드/조회 서비스.
    /// 파일명 기준으로 구분자/키 컬럼을 제공한다.
    /// </summary>
    public static class FileKeyManager
    {
        private static readonly object _lock = new();
        private static volatile FileKeyConfig? _cache;
        private static DateTime _lastReadUtc;
        private static string? _configPath;

        public static void ConfigurePath(string? overridePath = null)
        {
            _configPath = overridePath;
        }

        private static string ResolvePath()
        {
            if (!string.IsNullOrWhiteSpace(_configPath) && File.Exists(_configPath))
                return _configPath!;
            // 기본: 실행 폴더 기준
            var baseDir = AppContext.BaseDirectory;
            var path = Path.Combine(baseDir, "Common", "Configs", "fileKeyConfig.json");
            if (File.Exists(path)) return path;

            // 개발 중 상대 경로 대비
            path = Path.Combine(baseDir, "Configs", "fileKeyConfig.json");
            return path;
        }

        private static FileKeyConfig LoadInternal()
        {
            var path = ResolvePath();
            if (!File.Exists(path))
                throw new FileNotFoundException($"fileKeyConfig.json not found: {path}");

            var json = File.ReadAllText(path);
            var cfg = JsonSerializer.Deserialize<FileKeyConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            }) ?? new FileKeyConfig();

            _lastReadUtc = File.GetLastWriteTimeUtc(path);
            return cfg;
        }

        private static FileKeyConfig GetConfig()
        {
            var path = ResolvePath();
            var lastWrite = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;

            // 캐시 초기화 또는 파일 변경 시 리로드
            if (_cache == null || lastWrite > _lastReadUtc)
            {
                lock (_lock)
                {
                    if (_cache == null || lastWrite > _lastReadUtc)
                        _cache = LoadInternal();
                }
            }
            return _cache!;
        }

        /// <summary>
        /// 파일명 기준 설정 조회 (정확히 파일명만 사용).
        /// </summary>
        public static bool TryGetFileSettings(string fileName, out string delimiter, out List<string> keys)
        {
            delimiter = "|";
            keys = new List<string>();

            var cfg = GetConfig();
            var name = Path.GetFileName(fileName);

            if (cfg.Files != null && cfg.Files.TryGetValue(name, out var item))
            {
                delimiter = string.IsNullOrEmpty(item.Delimiter) ? "|" : item.Delimiter;
                keys = item.KeyColumns ?? new List<string>();
                return true;
            }
            return false;
        }

        /// <summary>
        /// 기본값 포함 설정 조회.
        /// </summary>
        public static (string delimiter, List<string> keys) GetFileSettingsOrDefault(string fileName, string defaultDelimiter = "|")
        {
            return TryGetFileSettings(fileName, out var d, out var k)
                ? (d, k)
                : (defaultDelimiter, new List<string>());
        }

        /// <summary>
        /// 헤더 이름 배열과 데이터 배열(한 행)로부터 컴포지트 키 문자열 생성.
        /// 키 구분자는 Unit Separator(0x1F)를 사용하여 충돌 최소화.
        /// </summary>
        public static string BuildCompositeKey(string[] headers, string[] row, IEnumerable<string> keyColumns)
        {
            // 헤더 인덱스 맵
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < headers.Length; i++) map[headers[i]?.Trim() ?? $"col{i}"] = i;

            var parts = new List<string>();
            foreach (var key in keyColumns)
            {
                if (map.TryGetValue(key, out int idx) && idx >= 0 && idx < row.Length)
                    parts.Add(row[idx]?.Trim() ?? string.Empty);
                else
                    parts.Add(string.Empty);
            }

            // Unit Separator로 join (표현 안전성)
            return string.Join("\u001F", parts);
        }
    }

    #region DTOs
    public class FileKeyConfig
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = "1.0";

        [JsonPropertyName("lastUpdated")]
        public string? LastUpdated { get; set; } = null;

        [JsonPropertyName("files")]
        public Dictionary<string, FileConfigItem>? Files { get; set; } = new();
    }

    public class FileConfigItem
    {
        [JsonPropertyName("delimiter")]
        public string Delimiter { get; set; } = "|";

        [JsonPropertyName("keyColumns")]
        public List<string> KeyColumns { get; set; } = new();
    }
    #endregion
}
