namespace InnoPVManagementSystem.Common.Foundation
{
    public class AppConfig
    {
        public PathsSection Paths { get; set; }
        public SystemSection System { get; set; }
        public List<IoFileEntry> Datas { get; set; } = new();
        public List<CsvFileEntry> Csv { get; set; } = new();
        public List<PVFileEntry> PV { get; set; } = new();
        public List<JsonFileEntry> Json { get; set; } = new();
    }

    public class PathsSection
    {
        public string UserDriveRoot { get; set; }
        public string IOFolder { get; set; }
        public string LogFolder { get; set; }
    }

    public class IoFileEntry
    {
        public string IoName { get; set; } = string.Empty;
        public string IoPath { get; set; } = string.Empty;
    }

    public class CsvFileEntry
    {
        public string CsvName { get; set; } = string.Empty;
        public string CsvPath { get; set; } = string.Empty;
    }

    public class PVFileEntry
    {
        public string PVInfoName { get; set; } = string.Empty;
        public string PVInfoPath { get; set; } = string.Empty;
    }

    public class JsonFileEntry
    {
        public string JsonName { get; set; } = string.Empty;
        public string JsonPath { get; set; } = string.Empty;
    }

    public class SystemSection
    {
        public string Environment { get; set; }
        public bool EnableDebug { get; set; }
    }
}
