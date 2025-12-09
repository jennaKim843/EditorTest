using System.Collections.Generic;
using System.Data;
using System.IO;
using InnoPVManagementSystem.Common.Constants;
using InnoPVManagementSystem.Common.Utils;
using InnoPVManagementSystem.Innolinc;

namespace InnoPVManagementSystem.Common.Services
{
    /// <summary>
    /// 파일(.io/.csv/.dat)을 읽어 DataTable로 변환하는 서비스
    /// - 이 계층은 UI에 의존하지 않으며, 예외를 던집니다. (UI 알림은 상위 계층에서 처리)
    /// - ExtendedProperties:
    ///   - "SourceHeader" (Header) : IO 헤더 메타
    ///   - "SourcePath" (string)   : 원본 경로
    ///   - "ColumnTooltips" (Dictionary&lt;string,string&gt;) : 컬럼 툴팁
    /// </summary>
    public static class FileLoaderService
    {
        // 앱 전역에서 한 번 설정해두고 어디서든 사용 가능한 기본 Resolver
        // 논리 파일명(예: "PV_CODE_DB") → 실제 경로를 반환하는 함수
        public static Func<string, string>? DefaultResolver { get; set; }

        // 기본 Resolver가 설정되어 있을 때 간단 호출용 오버로드
        public static System.Data.DataTable LoadAsDataTable(string logicalFileName)
        {
            if (DefaultResolver == null)
                throw new InvalidOperationException("FileLoaderService.DefaultResolver가 설정되지 않았습니다. App.xaml.cs의 OnStartup에서 설정하세요.");
            return LoadAsDataTable(logicalFileName, DefaultResolver);
        }

        public static DataTable LoadAsDataTable(string logicalFileName, Func<string, string> filePathResolver)
        {
            if (string.IsNullOrWhiteSpace(logicalFileName))
                throw new ArgumentException("logicalFileName이 비어 있습니다.", nameof(logicalFileName));
            if (filePathResolver == null)
                throw new ArgumentNullException(nameof(filePathResolver));

            string filePath = filePathResolver(logicalFileName);
            return LoadFileToDataTable(filePath);
        }

        public static DataTable LoadFileToDataTable(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("filePath가 비어 있습니다.", nameof(filePath));
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"파일을 찾을 수 없습니다.({filePath})", filePath);

            string ext = Path.GetExtension(filePath)?.ToLowerInvariant() ?? "";
            if (string.IsNullOrEmpty(ext))
                throw new NotSupportedException("확장자를 확인할 수 없습니다. (.io/.csv/.dat만 지원)");

            List<List<string>> csvData;
            Header? header = null;
            Dictionary<string, string> tooltips = new(StringComparer.OrdinalIgnoreCase);

            switch (ext)
            {
                case FileConstants.Extensions.Io:
                    (csvData, header) = IIO.IOtoCsv(filePath);
                    tooltips = TooltipLoader.LoadColumnTooltips(filePath);
                    break;
                case FileConstants.Extensions.Csv:
                    csvData = ParserUtil.ParsePipeDelimitedCsv(filePath);
                    break;
                case FileConstants.Extensions.Dat:
                    csvData = ParserUtil.ParsePipeDelimitedDat(filePath);
                    break;
                default:
                    throw new NotSupportedException($"지원되지 않는 확장자입니다: {ext}");
            }

            if (csvData == null || csvData.Count < 1)
                throw new InvalidDataException("[CSV 파싱 오류] 데이터가 비어 있거나 잘못된 형식입니다.");

            var table = DataTableUtil.ConvertCsvDataToDataTable(csvData);

            if (header != null)
                table.ExtendedProperties["SourceHeader"] = header;

            table.ExtendedProperties["SourcePath"] = filePath;
            table.ExtendedProperties["ColumnTooltips"] = tooltips;

            return table;
        }
    }
}