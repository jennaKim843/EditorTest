using System;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace InnoPVManagementSystem.Common.Foundation
{
    /// <summary>
    /// 앱 설정 파일(appsettings.json)을 로드하여 AppConfig 객체로 제공하는 유틸리티 클래스
    ///   #if DEBUG  => *.csproj 있는 "프로젝트 폴더"를 루트
    ///   #else      => 실행 폴더(AppContext.BaseDirectory)를 루트
    /// - ResolvePath(): 상대/절대 경로를 루트 기준 절대경로로 반환
    /// </summary>
    internal static class ConfigLoader
    {
        private static AppConfig _config;
        private const string ConfigFolder = "Resources"; // 설정 파일이 위치한 폴더
        private static readonly string _projectRoot = InitProjectRoot();

        /// <summary>현재 인식된 루트(디버그: 프로젝트 폴더, 운영: 실행 폴더)</summary>
        public static string ProjectRoot => _projectRoot;

        /// <summary>상대/절대 경로를 루트 기준으로 절대경로화</summary>
        public static string ResolvePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path ?? string.Empty;

            if (Path.IsPathRooted(path))
                return Path.GetFullPath(path);

            // 상대경로 → 루트와 결합
            var combined = Path.Combine(ProjectRoot, path);
            return Path.GetFullPath(combined);
        }

        /// <summary>
        /// 설정 파일을 로드하여 AppConfig 객체를 반환합니다.
        /// </summary>
        /// <param name="fileName">설정 파일 이름 (기본값: appsettings.json)</param>
        /// <returns>역직렬화된 AppConfig 객체</returns>
        public static AppConfig LoadConfig(string fileName = "prodFilePath.json")
        {
            // 이미 로드된 설정이 있으면 재사용
            if (_config != null)
                return _config;

            // 전체 경로 생성: 실행 폴더 + Resources + 파일
            // 디버그 모드일떄와 일반 모드 일때 경로가 다름
#if DEBUG   
            string fullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ConfigFolder, fileName);
#else
            // 전체 경로 생성: User 폴더 + innolinc 설치 경로 + 파일명
            string fullPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), $"AppData\\Roaming\\InnoLinc\\{fileName}");
#endif
            // 파일 존재 여부 확인
            if (!File.Exists(fullPath))
                throw new FileNotFoundException($"설정 파일이 존재하지 않습니다: {fullPath}");

            try
            {
                string json = File.ReadAllText(fullPath);
                _config = JsonSerializer.Deserialize<AppConfig>(json); // 대소문자 구분

                if (_config == null)
                    throw new InvalidOperationException("설정 파일을 읽었으나 AppConfig 변환에 실패했습니다.");
                
                // _config의 path는  working directory와 json의 세팅 설정을 따라감.
                // project의 이름에 따라 계속 변화됨
                _config.Paths.IOFolder = Path.Combine(Directory.GetCurrentDirectory(), _config.Paths.IOFolder);


                return _config;
            }
            catch (JsonException je)
            {
                throw new InvalidOperationException($"설정 파일 JSON 형식 오류: {je.Message}");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"설정 파일 로딩 중 예외 발생: {ex.Message}");
            }
        }

        /// <summary>
        /// 디버그: *.csproj가 있는 디렉터리를 "프로젝트 루트"로, 없으면 실행 폴더.
        /// 운영: 실행 폴더.
        /// </summary>
        private static string InitProjectRoot()
        {
#if DEBUG
            var found = FindProjectDir(AppContext.BaseDirectory);
            return Path.GetFullPath(found ?? AppContext.BaseDirectory);
#else
            return Path.GetFullPath(System.IO.Directory.GetCurrentDirectory());
#endif
        }

        /// <summary>startDir에서 상위로 올라가며 *.csproj가 있는 폴더를 찾음</summary>
        private static string? FindProjectDir(string startDir)
        {
            var cur = new DirectoryInfo(startDir);
            while (cur != null)
            {
                if (cur.GetFiles("*.csproj", SearchOption.TopDirectoryOnly).Any())
                    return cur.FullName;
                cur = cur.Parent;
            }
            return null;
        }
    }
}
