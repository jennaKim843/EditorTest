using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Threading.Tasks;
using InnoPVManagementSystem.Common.Foundation;
using System.Globalization;

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
        public static string UnitSeparator = "\u001F";

        public static void ConfigurePath(string? overridePath = null)
        {
            _configPath = overridePath;
        }

        private static string ResolvePath()
        {
            if (!string.IsNullOrWhiteSpace(_configPath) && File.Exists(_configPath))
                return _configPath!;

            var fileName = "fileKeyConfig.json";
#if DEBUG
            string fullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", fileName);
#else
            // 전체 경로 생성: User 폴더 + innolinc 설치 경로 + 파일명
            string fullPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), $"AppData\\Roaming\\InnoLinc\\{fileName}");
#endif
            return fullPath;
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
        /// (확장) 파일명 기준 설정 조회.
        /// - 기존 TryGetFileSettings 와는 별도(overload)라 기존 코드에 영향 없음.
        /// </summary>
        public static bool TryGetFileSettings(string fileName, out FileConfigItem? configItem)
        {
            configItem = null;

            var cfg = GetConfig();
            var name = Path.GetFileName(fileName);

            if (cfg.Files != null && cfg.Files.TryGetValue(name, out var item))
            {
                configItem = item;
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
        /// (확장) DupKeyColumns 을 포함한 설정 조회.
        /// - dupKeyColumns 가 비어 있으면 keyColumns 를 대신 사용.
        /// </summary>
        public static (string delimiter, List<string> keys, List<string> dupKeys) GetFileSettingsWithDupKeysOrDefault(
            string fileName,
            string defaultDelimiter = "|")
        {
            if (TryGetFileSettings(fileName, out FileConfigItem? item) && item != null)
            {
                var delimiter = string.IsNullOrEmpty(item.Delimiter) ? defaultDelimiter : item.Delimiter;
                var keys = item.KeyColumns ?? new List<string>();
                var dupKeys = (item.DupKeyColumns != null && item.DupKeyColumns.Count > 0)
                    ? item.DupKeyColumns
                    : keys;

                return (delimiter, keys, dupKeys);
            }

            return (defaultDelimiter, new List<string>(), new List<string>());
        }

        /// <summary>
        /// 중복 검증용 키 컬럼 목록 조회.
        /// - dupKeyColumns 정의가 있으면 그것을 사용.
        /// - 없으면 keyColumns 를 그대로 반환.
        /// </summary>
        public static List<string> GetDupKeyColumnsOrDefault(string fileName)
        {
            if (TryGetFileSettings(fileName, out FileConfigItem? item) && item != null)
            {
                if (item.DupKeyColumns != null && item.DupKeyColumns.Count > 0)
                    return new List<string>(item.DupKeyColumns);

                if (item.KeyColumns != null && item.KeyColumns.Count > 0)
                    return new List<string>(item.KeyColumns);
            }

            return new List<string>();
        }

        /// <summary>
        /// 날짜 컬럼 포맷 정보 조회.
        /// - config 의 dateColumns: { colName: { format: "yyyyMMdd" } } 를
        ///   Dictionary&lt;string, string&gt; (colName → format) 으로 변환해 반환.
        /// - 정의가 없으면 빈 Dictionary 반환.
        /// </summary>
        public static Dictionary<string, string> GetDateColumnFormatsOrEmpty(string fileName)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (TryGetFileSettings(fileName, out FileConfigItem? item) && item != null && item.DateColumns != null)
            {
                foreach (var kv in item.DateColumns)
                {
                    var colName = kv.Key;
                    var cfg = kv.Value;
                    if (string.IsNullOrWhiteSpace(colName) || cfg == null || string.IsNullOrWhiteSpace(cfg.Format))
                        continue;

                    result[colName] = cfg.Format!;
                }
            }

            return result;
        }

        /// <summary>
        /// 헤더 이름 배열과 데이터 배열(한 행)로부터 컴포지트 키 문자열 생성.
        /// 키 구분자는 Unit Separator(0x1F)를 사용하여 충돌 최소화.
        /// (기존 버전: 날짜 포맷 정보 없음)
        /// </summary>
        public static string BuildCompositeKey(string[] headers, string[] row, IEnumerable<string> keyColumns)
        {
            // 기존 동작 유지를 위해 dateColumnFormats = null 로 넘김
            return BuildCompositeKey(headers, row, keyColumns, dateColumnFormats: null);
        }

        /// <summary>
        /// (확장) 날짜 포맷 정보를 고려하여 컴포지트 키 문자열 생성.
        /// - dateColumnFormats: 컬럼명 → 입력 포맷(예: "yyyyMMdd", "yyyy,M,d")
        /// - 날짜 컬럼은 내부적으로 yyyyMMdd 로 정규화 후 키를 구성.
        /// - 포맷 파싱 실패 시에는 원본 값을 그대로 사용(기존 동작과의 호환성 유지).
        /// </summary>
        public static string BuildCompositeKey(
            string[] headers,
            string[] row,
            IEnumerable<string> keyColumns,
            IReadOnlyDictionary<string, string>? dateColumnFormats)
        {
            // 헤더 인덱스 맵
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < headers.Length; i++)
                map[headers[i]?.Trim() ?? $"col{i}"] = i;

            var parts = new List<string>();

            foreach (var key in keyColumns)
            {
                string colName = key;
                string value = string.Empty;

                if (map.TryGetValue(colName, out int idx) && idx >= 0 && idx < row.Length)
                    value = row[idx]?.Trim() ?? string.Empty;

                // 날짜 컬럼이면 포맷에 맞춰 정규화 (예: yyyy,M,d → yyyyMMdd)
                if (dateColumnFormats != null &&
                    dateColumnFormats.TryGetValue(colName, out var fmt) &&
                    !string.IsNullOrWhiteSpace(fmt) &&
                    !string.IsNullOrWhiteSpace(value))
                {
                    try
                    {
                        var dt = DateTime.ParseExact(
                            value,
                            fmt,
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.None);

                        // 키 비교/머지용은 yyyyMMdd 로 통일
                        value = dt.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                    }
                    catch
                    {
                        // 포맷 불일치 등 예외 발생 시 기존 값 그대로 사용(호환성 유지)
                    }
                }

                parts.Add(value);
            }

            // Unit Separator로 join (표현 안전성)
            return string.Join(UnitSeparator, parts);
        }
        /// <summary>
        /// config 에 정의된 기준 폴더명 목록 조회.
        /// JSON: "baseFolderName": [ "Standard", "K100", ... ]
        /// 값이 비었을 경우 "Standard" 하나를 기본으로 반환.
        /// </summary>
        public static IReadOnlyList<string> GetBaseFolders()
        {
            var cfg = GetConfig();
            if (cfg.BaseFolderNames != null && cfg.BaseFolderNames.Count > 0)
                return cfg.BaseFolderNames;

            // fallback 기본값
            return new List<string> { "Standard" };
        }

        /// <summary>
        /// config 에 정의된 관리자 사번 목록 조회.
        /// JSON: "adminEmpNo": [ "A123456", "B234567", ... ]
        /// </summary>
        public static IReadOnlyList<string> GetAdminEmpNos()
        {
            var cfg = GetConfig();
            return cfg.AdminEmpNos ?? new List<string>();
        }

        /// <summary>
        /// fileKeyConfig.json 상에 해당 파일명이 존재하고
        /// keyColumns 가 1개 이상 정의되어 있는지 여부.
        /// </summary>
        public static bool HasFileKeyConfig(string fileNameOrPath)
        {
            var name = Path.GetFileName(fileNameOrPath);
            return TryGetFileSettings(name, out _, out var keys)
                   && keys != null
                   && keys.Count > 0;
        }
    }

    #region DTOs
    public class FileKeyConfig
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = "1.0";

        [JsonPropertyName("lastUpdated")]
        public string? LastUpdated { get; set; } = null;

        [JsonPropertyName("baseFolderName")]
        public List<string> BaseFolderNames { get; set; } = new();

        [JsonPropertyName("adminEmpNo")]
        public List<string> AdminEmpNos { get; set; } = new();

        [JsonPropertyName("files")]
        public Dictionary<string, FileConfigItem>? Files { get; set; } = new();
    }

    public class FileConfigItem
    {
        [JsonPropertyName("delimiter")]
        public string Delimiter { get; set; } = "|";

        [JsonPropertyName("keyColumns")]
        public List<string> KeyColumns { get; set; } = new();

        /// <summary>
        /// 중복 검증용 키 컬럼.
        /// - 정의되어 있으면 중복검증 시 우선 사용.
        /// - 비어 있으면 keyColumns 를 대신 사용.
        /// </summary>
        [JsonPropertyName("dupKeyColumns")]
        public List<string>? DupKeyColumns { get; set; }

        /// <summary>
        /// 날짜 컬럼 포맷 정의 (컬럼명 → 포맷).
        /// 예: "valid_date": { "format": "yyyyMMdd" } 또는 "yyyy,M,d"
        /// </summary>
        [JsonPropertyName("dateColumns")]
        public Dictionary<string, DateColumnConfig>? DateColumns { get; set; }
    }

    public class DateColumnConfig
    {
        [JsonPropertyName("format")]
        public string? Format { get; set; }
    }

    #endregion
}
