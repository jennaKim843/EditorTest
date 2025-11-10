using System.IO;
using System.Data;
using System.Windows;
using System.Windows.Input;
using System.Collections.ObjectModel;
using InnoPVManagementSystem.Common.Constants;
using InnoPVManagementSystem.Common.Foundation;
using InnoPVManagementSystem.Common.ViewModels.Base;
using InnoPVManagementSystem.Common.Services;
using InnoPVManagementSystem.Modules.CompareMerge.Views;
using System.Text;

namespace InnoPVManagementSystem.Modules.CompareMerge.ViewModels
{
    /// <summary>
    /// [ViewModel] 보너스관리 ViewModel
    /// - CSV/IO 파일(LoyaltyMainBonusSet)을 로드하여 DataView로 그리드에 바인딩
    /// - 검색 조건 및 상태 코드 필터링 지원
    /// - MVVM 패턴 기반: ViewModel → View(DataGrid) 바인딩
    /// </summary>
    public class DataCompareMergeViewModel : ViewModelBase
    {

        private readonly DiffService _diff;               // 비교 로직 담당
        private readonly DiffOptions _options;            // 비교 옵션
        private CancellationTokenSource? _cts;            // 취소 토큰


        public class DiffGridItem
        {
            public int No { get; set; }                   // 순번
            public string FileName { get; set; } = "";    // 상대경로 or 파일명
            public int AddedCount { get; set; }           // 폴더2에만 존재(추가)
            public int DeletedCount { get; set; }         // 폴더1에만 존재(삭제)
            public int ModifiedCount { get; set; }        // 동일 Key 있으나 내용 달라서 변경으로 간주(아래 로직)

            // 실제 비교에 사용할 좌/우 전체 경로
            public string? File1Path { get; set; }   // 기준(폴더1) 파일 풀경로
            public string? File2Path { get; set; }   // 비교(폴더2) 파일 풀경로
        }

        private DiffGridItem? _selectedItem;
        public DiffGridItem? SelectedItem
        {
            get => _selectedItem;
            set => SetProperty(ref _selectedItem, value);
        }

        private sealed class VmProgress : IUiProgress
        {
            private readonly DataCompareMergeViewModel _vm;
            public VmProgress(DataCompareMergeViewModel vm) => _vm = vm;

            public void SetMessage(string message)
                => Application.Current.Dispatcher.Invoke(() => _vm.ProgressMessage = message);

            public void SetPercent(int value)
                => Application.Current.Dispatcher.Invoke(() => _vm.ProgressValue = value);

            public void SetStep(int current, int total)
                => Application.Current.Dispatcher.Invoke(() =>
                    _vm.ProgressMessage = $"[{current}/{total}] 파일 비교 중...");
        }

        private bool _isProgressVisible;
        public bool IsProgressVisible
        {
            get => _isProgressVisible;
            set { _isProgressVisible = value; OnPropertyChanged(); }
        }

        private int _progressValue;
        public int ProgressValue
        {
            get => _progressValue;
            set { _progressValue = value; OnPropertyChanged(); }
        }

        private string _progressMessage = "";
        public string ProgressMessage
        {
            get => _progressMessage;
            set { _progressMessage = value; OnPropertyChanged(); }
        }

        public ObservableCollection<DiffGridItem> GridItems { get; } = new();

        // VM 내부: 폴더 비교 후 그리드 채우기
        public async Task BuildGridFromFoldersAsync(string folder1, string folder2, DiffOptions opt, int keyColumnIndex = 0, CancellationToken ct = default)
        {
            GridItems.Clear();

            // 1) 두 폴더의 파일 상대경로 매핑
            var searchOpt = opt.IncludeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var f1Files = EnumerateFilesLocal(folder1, opt.FilePattern, searchOpt).ToList();
            var f2Files = EnumerateFilesLocal(folder2, opt.FilePattern, searchOpt).ToList();

            // 상대경로 딕셔너리
            string ToRel(string baseDir, string full) =>
                Path.GetRelativePath(baseDir, full).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

            var map1 = f1Files.ToDictionary(p => ToRel(folder1, p), p => p, System.StringComparer.OrdinalIgnoreCase);
            var map2 = f2Files.ToDictionary(p => ToRel(folder2, p), p => p, System.StringComparer.OrdinalIgnoreCase);

            // 2) 상대경로 기준 전체 키 집합
            var allKeys = new HashSet<string>(map1.Keys, System.StringComparer.OrdinalIgnoreCase);
            allKeys.UnionWith(map2.Keys);

            var diff = new DiffService(); // DI 없이 바로

            foreach (var rel in allKeys.OrderBy(x => x))
            {
                ct.ThrowIfCancellationRequested();

                map1.TryGetValue(rel, out var f1);
                map2.TryGetValue(rel, out var f2);

                // 한쪽만 있으면 전부 추가/삭제로 처리
                if (string.IsNullOrEmpty(f1) && !string.IsNullOrEmpty(f2))
                {
                    // 폴더1엔 없음 → 전부 추가
                    var f2Lines = await File.ReadAllLinesAsync(f2, System.Text.Encoding.UTF8, ct);
                    GridItems.Add(new DiffGridItem { FileName = rel, AddedCount = f2Lines.Length, DeletedCount = 0, ModifiedCount = 0 });
                    continue;
                }
                if (!string.IsNullOrEmpty(f1) && string.IsNullOrEmpty(f2))
                {
                    // 폴더2엔 없음 → 전부 삭제
                    var f1Lines = await File.ReadAllLinesAsync(f1, System.Text.Encoding.UTF8, ct);
                    GridItems.Add(new DiffGridItem { FileName = rel, AddedCount = 0, DeletedCount = f1Lines.Length, ModifiedCount = 0 });
                    continue;
                }

                // 둘 다 존재 → 파일 대 파일 비교
                var basic = await diff.CompareFileToFileAsync(f1!, f2!, opt, ct);
                int deleted = basic.OnlyInFile1; // 폴더1에만 → 삭제
                int added = basic.OnlyInFile2; // 폴더2에만 → 추가
                int modified = 0;

                // ====== TODO CSV Key 기반 수정 계산 ======
                // 전처리(LiteralText) 적용 후, KeyColumnIndex 로 동일 Key인데 내용이 다른 라인 수를 "수정"으로 간주
                // - 키를 못 정하면 이 블록을 주석 처리하거나 modified=0 유지
                try
                {
                    // 파일 라인 로딩
                    var f1Raw = await File.ReadAllLinesAsync(f1!, System.Text.Encoding.UTF8, ct);
                    var f2Raw = await File.ReadAllLinesAsync(f2!, System.Text.Encoding.UTF8, ct);

                    // 전처리
                    string Pre(string s) => string.IsNullOrEmpty(opt.LiteralText) ? s : (s.IndexOf(opt.LiteralText) is int i && i >= 0 ? s[..i].TrimEnd() : s);

                    // Key→원문라인 딕셔너리(동일 키 중복 시 첫 라인 우선; 필요 시 더 견고하게 바꿀 수 있음)
                    static string GetKey(string line, int idx)
                    {
                        var parts = line.Split(',');
                        return (idx >= 0 && idx < parts.Length) ? parts[idx] : line; // 키 실패 시 전체 라인을 키로(안전)
                    }

                    var f1Map = new Dictionary<string, string>();
                    foreach (var line in f1Raw)
                    {
                        var norm = Pre(line);
                        var key = GetKey(norm, keyColumnIndex);
                        if (!f1Map.ContainsKey(key)) f1Map[key] = norm;
                    }

                    var f2Map = new Dictionary<string, string>();
                    foreach (var line in f2Raw)
                    {
                        var norm = Pre(line);
                        var key = GetKey(norm, keyColumnIndex);
                        if (!f2Map.ContainsKey(key)) f2Map[key] = norm;
                    }

                    // 수정 판단: 양쪽에 같은 Key가 존재하지만 문자열이 다른 경우
                    foreach (var key in f1Map.Keys)
                    {
                        if (f2Map.TryGetValue(key, out var v2))
                        {
                            if (!string.Equals(f1Map[key], v2, StringComparison.Ordinal))
                                modified++;
                        }
                    }

                    // 수정/추가/삭제 관계 정리(선택):
                    // 보통 "수정"은 추가/삭제와 별도로 표기하고, 추가/삭제 카운트는 집합 차집합 그대로 두는 편이 단순합니다.
                    // 만약 "수정은 추가/삭제에서 제외" 룰을 원하면 아래처럼 조정:
                    // deleted = Math.Max(0, deleted - modified);
                    // added   = Math.Max(0, added   - modified);

                }
                catch
                {
                    // CSV 형식이 아니거나 예외가 나도 modified=0으로 진행(안전)
                }

                // 한쪽만 있는 경우
                if (string.IsNullOrEmpty(f1) && !string.IsNullOrEmpty(f2))
                {
                    var f2Lines = await File.ReadAllLinesAsync(f2, Encoding.UTF8, ct);
                    GridItems.Add(new DiffGridItem
                    {
                        FileName = rel,
                        AddedCount = f2Lines.Length,
                        DeletedCount = 0,
                        ModifiedCount = 0,
                        File1Path = null,
                        File2Path = f2  // ⬅ 경로 세팅
                    });
                    continue;
                }
                if (!string.IsNullOrEmpty(f1) && string.IsNullOrEmpty(f2))
                {
                    var f1Lines = await File.ReadAllLinesAsync(f1, Encoding.UTF8, ct);
                    GridItems.Add(new DiffGridItem
                    {
                        FileName = rel,
                        AddedCount = 0,
                        DeletedCount = f1Lines.Length,
                        ModifiedCount = 0,
                        File1Path = f1,
                        File2Path = null
                    });
                    continue;
                }

                // 둘 다 있는 경우
                GridItems.Add(new DiffGridItem
                {
                    FileName = rel,
                    AddedCount = added,
                    DeletedCount = deleted,
                    ModifiedCount = modified,
                    File1Path = f1,          
                    File2Path = f2
                });

                for (int i = 0; i < GridItems.Count; i++)
                    GridItems[i].No = i + 1;
            }
        }

        // VM 안에만 쓰는 작은 유틸 (DiffService의 EnumerateFiles와 동등)
        private static IEnumerable<string> EnumerateFilesLocal(string root, string pattern, SearchOption opt)
        {
            var patterns = pattern.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                                  .Select(s => s.Trim()).DefaultIfEmpty("*.*");
            return patterns.SelectMany(p => Directory.GetFiles(root, p, opt)).Distinct();
        }

        //private DataTable ConvertToDataTable(IEnumerable<DiffGridItem> items)
        //{
        //    var table = new DataTable();
        //    table.Columns.Add("NO", typeof(int));
        //    table.Columns.Add("파일명", typeof(string));
        //    table.Columns.Add("삭제(건)", typeof(int));
        //    table.Columns.Add("추가(건)", typeof(int));
        //    table.Columns.Add("수정(건)", typeof(int));

        //    int cnt = 0;
        //    foreach (var i in items)
        //    { 
        //        cnt ++;
        //        table.Rows.Add(cnt, i.FileName, i.DeletedCount, i.AddedCount, i.ModifiedCount);
        //    }
                

        //    return table;
        //}

        // =========================================================
        // [생성자]
        // =========================================================
        public DataCompareMergeViewModel()
        {
            _diff = new DiffService(); // DI 없이 직접 생성
            _options = new DiffOptions
            {
                FilePattern = "*.csv;*.io",
                IncludeSubfolders = true,
                LiteralText = "_NOTE_",  // 예: NOTE 이후 꼬리 자르기
                OptimizeThresholdUniqueLines = 1_000_000
            };

            SelectedStandardFolder = CodeConstants.Status.All;
            SelectedTargetFolder = CodeConstants.Status.All;

            /// 커맨드 초기화(Busy 상태에 따라 실행 가능 여부 제어) //TODO 수정
            SelectFolderCommand = new RelayCommand(SelectFolder, () => !IsBusy);
            CompareCommand = new RelayCommand(CompareFile, () => !IsBusy);
            InitCommand = new RelayCommand(Init, () => !IsBusy);
            ApplyCommand = new RelayCommand(ApplySelected, () => !IsBusy);
            ApplyAllCommand = new RelayCommand(ApplyAll, () => !IsBusy);

            //InitCommand = new RelayCommand(async () => await CompareFile(),
            //                                 () => !IsBusy && !IsModifying);

        }

        // 바인딩용 프로퍼티
        //public string Folder1Path { get; set; } = "";
        //public string Folder2Path { get; set; } = "";
        public string ResultsText { get; set; } = "";
        public bool IsComparing { get; set; }
        public string LiteralText { get; set; } = "";

        // =========================================================
        // [검색조건 프로퍼티] - 사용자 입력값을 바인딩받는 프로퍼티들
        // =========================================================
        private string? _baseFilePath;      // 기준경로
        public string? BaseFilePath 
        { 
            get => _baseFilePath; 
            set => SetProperty(ref _baseFilePath, value);
            //set
            //{
            //    if (SetProperty(ref _baseFilePath, value))
            //    {
            //        폴더 경로가 변경되면 매칭 명령의 실행 가능 상태 업데이트
            //       ((RelayCommand)MatchFilesCommand).RaiseCanExecuteChanged();
            //    }
            //}
        }

        // =========================================================
        // [기준폴더] - 비교기준폴더 목록
        // =========================================================
        public ObservableCollection<OptionItem> StandardFolderList { get; } = new ObservableCollection<OptionItem>();
        // =========================================================
        // [대상폴더] - 비교대상폴더 목록
        // =========================================================
        public ObservableCollection<OptionItem> TargetFolderList { get; } = new ObservableCollection<OptionItem>();

        // =========================================================
        // [DataTable] - 실제 DataGrid에 표시될 비교요약 데이터kyo
        // =========================================================
        private DataTable? _table;
        public DataTable? Table
        {
            get => _table;
            private set => SetProperty(ref _table, value);
        }

        // =========================================================
        // [기준폴더]
        // =========================================================
        ///// <summary>
        ///// 사용자 폴더리스트
        ///// </summary>
        //private ObservableCollection<FolderItem> _folderList = new();
        //public ObservableCollection<FolderItem> FolderList { get; } = new();

        private string _selectedStandardFolder = CodeConstants.Status.All; // TODO 수정
        /// <summary>
        /// 현재 선택된 기준폴더
        /// </summary>
        public string SelectedStandardFolder
        {
            get => _selectedStandardFolder;
            set => SetProperty(ref _selectedStandardFolder, value);
        }

        // =========================================================
        // [비교폴더]
        // =========================================================
        private string _selectedTargetFolder = CodeConstants.Status.All;
        /// <summary>
        /// 현재 선택된 비교대상폴더
        /// </summary>
        public string SelectedTargetFolder
        {
            get => _selectedTargetFolder;
            set => SetProperty(ref _selectedTargetFolder, value);
        }

        // =========================================================
        // [커맨드: 버튼 바인딩] - 선택, 비교, 초기화
        // =========================================================
        public ICommand SelectFolderCommand { get; }
        public ICommand CompareCommand { get; }
        public ICommand InitCommand { get; }
        public ICommand ApplyCommand { get; }
        public ICommand ApplyAllCommand { get; }

        // Busy 상태 플래그 (조회중/초기화중 UI 차단 등)
        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                if (SetProperty(ref _isBusy, value))
                {
                    // Busy 상태에 따라 버튼 활성화/비활성 갱신
                    (SelectFolderCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (CompareCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (InitCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (ApplyCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (ApplyAllCommand as RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        // === 상태 플래그: 수정 중 여부 === // TODO 수정
        private bool _isModifying;
        public bool IsModifying
        {
            get => _isModifying;
            set
            {
                if (SetProperty(ref _isModifying, value))
                {
                    (SelectFolderCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (CompareCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (InitCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (ApplyCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (ApplyAllCommand as RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        // =========================================================
        // [버튼이벤트]
        // =========================================================
        /// <summary>
        /// 폴더 선택 다이얼로그 호출
        /// </summary>
        private void SelectFolder()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "폴더 선택 (아무 파일이나 선택하면 해당 폴더가 선택됩니다)",
                CheckFileExists = false,
                CheckPathExists = true,
                ValidateNames = false,
                FileName = "Folder Selection.",
                DefaultExt = "folder",
                Filter = "폴더 선택|*.folder"
            };

            // 현재 경로가 있으면 초기 디렉토리로 설정
            if (!string.IsNullOrEmpty(BaseFilePath) && Directory.Exists(BaseFilePath))
            {
                dialog.InitialDirectory = BaseFilePath;
            }

            if (dialog.ShowDialog() == true)
            {
                var selectedPath = Path.GetDirectoryName(dialog.FileName);
                if (string.IsNullOrEmpty(selectedPath))
                    return;

                // 선택된 폴더(루트)
                BaseFilePath = selectedPath;

                // 선택한 폴더 안의 서브폴더 가져오기
                string[] subFolders;

                try
                {
                    subFolders = Directory.GetDirectories(selectedPath);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"폴더 조회 중 오류: {ex.Message}");
                    return;
                }

                // 내부 폴더 콤보리스트 생성
                foreach (var folder in subFolders)
                {
                    var folderName = Path.GetFileName(folder);

                    // Standard 중복방지
                    if (!StandardFolderList.Any(x => x.Code.Equals(folder, StringComparison.OrdinalIgnoreCase)))
                    {
                        StandardFolderList.Add(new OptionItem(
                            Code: folder,
                            Name: folderName
                        ));
                    }

                    // Target 중복방지
                    if (!TargetFolderList.Any(x => x.Code.Equals(folder, StringComparison.OrdinalIgnoreCase)))
                    {
                        TargetFolderList.Add(new OptionItem(
                            Code: folder,
                            Name: folderName
                        ));
                    }
                    
                }


            }

        }

        /// <summary>
        /// CSV/IO 로드 → 비교 → Table = 결과 DataTable
        /// </summary>
        private async void CompareFile()
        {
            if (IsComparing)
            {
                // 중복 실행 시, 잠깐 오버레이로 안내만 보여주고 종료
                IsProgressVisible = true;
                ProgressMessage = "이전 비교 작업이 아직 진행 중입니다...";
                ProgressValue = 50;
                await Task.Delay(1000);
                IsProgressVisible = false;
                return;
            }

            try
            {
                IsComparing = true;
                IsProgressVisible = true;
                ProgressValue = 0;
                ProgressMessage = "비교 준비 중...";

                _cts = new CancellationTokenSource();

                var diff = new DiffService(); // DI 없이 바로 사용
                var options = new DiffOptions
                {
                    FilePattern = "*.csv; *.io",                     // 필요 시 *.txt;*.csv
                    IncludeSubfolders = true,
                    LiteralText = string.IsNullOrWhiteSpace(LiteralText) ? null : LiteralText,
                    OptimizeThresholdUniqueLines = 1_000_000
                };

                // 폴더-폴더 비교의 경우: 진행률 전달
                var progress = new VmProgress(this);
                var summary = await diff.CompareFolderToFolderAsync(
                    SelectedStandardFolder, SelectedTargetFolder, options, progress, _cts.Token);

                // 혹은 “그리드(파일명|추가|삭제|수정)”을 바로 만들고 싶다면:
                await BuildGridFromFoldersAsync(
                    SelectedStandardFolder,
                    SelectedTargetFolder,
                    options,
                    keyColumnIndex: 0,
                    ct: _cts.Token);

                // DataTable 바인딩까지 쓰는 경우
                //Table = ConvertToDataTable(GridItems);

                ProgressMessage = $"비교 완료: {GridItems.Count}개 파일";
                ProgressValue = 100;
            }
            catch (OperationCanceledException)
            {
                ProgressMessage = "작업이 취소되었습니다.";
            }
            catch (Exception ex)
            {
                ProgressMessage = $"오류: {ex.Message}";
                MessageBox.Show(ProgressMessage, "비교 실패", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsComparing = false;
                // 약간의 지연 후 오버레이 닫기(완료 UX)
                await Task.Delay(300);
                IsProgressVisible = false;
                _cts?.Dispose();
                _cts = null;
            }
        }

        //private async void CompareFile()
        //{
        //    try
        //    {
        //        // 1️ 비교 중복 방지
        //        if (IsComparing)
        //        {
        //            //MessageBox.Show("비교가 이미 진행 중입니다.");
        //            //return;
        //        }

        //        // 2️ 폴더 유효성 체크
        //        if (string.IsNullOrWhiteSpace(SelectedStandardFolder) || string.IsNullOrWhiteSpace(SelectedTargetFolder))
        //        {
        //            MessageBox.Show("두 폴더 경로를 모두 지정해 주세요.");
        //            return;
        //        }

        //        // 3️ 상태 초기화
        //        IsComparing = true;
        //        ProgressMessage = "비교 준비 중...";
        //        ProgressValue = 0;
        //        GridItems.Clear();

        //        // 4️ DiffOptions 생성
        //        var literal = string.IsNullOrWhiteSpace(this.LiteralText) ? null : this.LiteralText;

        //        var opt = new DiffOptions
        //        {
        //            FilePattern = "*.csv",
        //            IncludeSubfolders = true,
        //            LiteralText = literal,
        //            OptimizeThresholdUniqueLines = 1_000_000
        //        };

        //        // 5️ 실제 비교 수행 (폴더1↔폴더2)
        //        await BuildGridFromFoldersAsync(
        //            SelectedStandardFolder,
        //            SelectedTargetFolder,
        //            opt,
        //            keyColumnIndex: 0,
        //            ct: CancellationToken.None
        //        );

        //        // 6️ DataGrid 바인딩용 DataTable 구성 (선택)
        //        Table = ConvertToDataTable(GridItems);

        //        ProgressMessage = $"비교 완료 ({GridItems.Count}개 파일)";
        //    }
        //    catch (Exception ex)
        //    {
        //        MessageBox.Show($"오류: {ex.Message}", "비교 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        //    }
        //    finally
        //    {
        //        IsComparing = false;
        //        ProgressValue = 0;
        //    }
        //}

        /// <summary>
        /// 초기화 로직
        /// </summary>
        private void Init()
        {
            // 사용자 확인
            if (MessageBox.Show("초기화를 진행하시겠습니까?", "초기화 확인",
                                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            // 모든 조건 초기화
            BaseFilePath = null;
            SelectedStandardFolder = CodeConstants.Status.All;
            SelectedTargetFolder = CodeConstants.Status.All;

        }

        /// <summary>
        /// 선택 건 적용
        /// </summary>
        private void ApplySelected()
        {
            // TODO: 선택 건 적용
            try
            {
                //if (SelectedItem is null)
                //{
                //    MessageBox.Show("그리드에서 비교할 행을 선택해 주세요.", "안내",
                //        MessageBoxButton.OK, MessageBoxImage.Information);
                //    return;
                //}

                //var left = SelectedItem.File1Path;
                //var right = SelectedItem.File2Path;

                //var left = "C:\\Users\\admin\\source\\repos\\dosa510\\InnoPVManagementSystem\\PV_input\\PV_PROD_INFO_ALL.csv";
                //var right = "C:\\Users\\admin\\source\\repos\\dosa510\\InnoPVManagementSystem\\PV_input\\PV_PROD_INFO_ALL_back.csv";
                var left = "C:\\Users\\user\\Desktop\\InnoPVManagementSystem\\PV_input\\PV_PROD_INFO_ALL.csv";
                var right = "C:\\Users\\user\\Desktop\\InnoPVManagementSystem\\PV_input\\PV_PROD_INFO_ALL_back.csv";


                //if (string.IsNullOrWhiteSpace(left) || !File.Exists(left))
                //{
                //    MessageBox.Show("좌측(기준) 파일이 없습니다.", "오류",
                //        MessageBoxButton.OK, MessageBoxImage.Error);
                //    return;
                //}
                //if (string.IsNullOrWhiteSpace(right) || !File.Exists(right))
                //{
                //    MessageBox.Show("우측(비교) 파일이 없습니다.", "오류",
                //        MessageBoxButton.OK, MessageBoxImage.Error);
                //    return;
                //}

                // 읽기 전용 Diff 창 띄우기
                var win = new ReadOnlyDiffWindow(left, right);
                win.Owner = Application.Current?.MainWindow;
                win.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                win.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"적용 중 오류: {ex.Message}", "오류",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 전체 적용
        /// </summary>
        private void ApplyAll()
        {
            // TODO: 전체 적용
        }




    }
}
