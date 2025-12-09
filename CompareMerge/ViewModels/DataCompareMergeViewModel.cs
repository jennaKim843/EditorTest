using System.IO;
using System.Data;
using System.Windows;
using System.Windows.Input;
using System.Collections.ObjectModel;
using System.Text;
using InnoPVManagementSystem.Common.Constants;
using InnoPVManagementSystem.Common.Foundation;
using InnoPVManagementSystem.Common.ViewModels.Base;
using InnoPVManagementSystem.Common.Services;
using InnoPVManagementSystem.Modules.CompareMerge.Views;
using InnoPVManagementSystem.Innolinc;
using InnoPVManagementSystem.Common.Utils;
using InnoPVManagementSystem.Modules.Common;
using InnoPVManagementSystem.Common.IO;

using static InnoPVManagementSystem.Common.Constants.CodeConstants;

namespace InnoPVManagementSystem.Modules.CompareMerge.ViewModels
{
    /// <summary>
    /// [ViewModel] 데이터 비교·머지(DataCompareMerge) 핵심
    /// </summary>
    public class DataCompareMergeViewModel : ViewModelBase
    {

        private readonly DiffService _diff;               // 비교 로직 담당
        private readonly DiffOptions _options;            // 비교 옵션
        private CancellationTokenSource? _cts;            // 취소 토큰

        private bool _isAdmin;
        public bool IsAdmin
        {
            get => _isAdmin;
            set => SetProperty(ref _isAdmin, value);
        }

        public class DiffGridItem
        {
            public int No { get; set; }                   // 순번
            public string FileName { get; set; } = string.Empty;    // 상대경로 or 파일명
            public int AddedCount { get; set; }           // 폴더2에만 존재(추가)
            public int DeletedCount { get; set; }         // 폴더1에만 존재(삭제)
            public int ModifiedCount { get; set; }        // 동일 Key 있으나 내용 달라서 변경으로 간주(아래 로직)

            // 실제 비교에 사용할 좌/우 전체 경로
            public string? File1Path { get; set; }   // 기준(폴더1) 파일 풀경로
            public string? File2Path { get; set; }   // 비교(폴더2) 파일 풀경로

            public string Message { get; set; } = string.Empty;    // 메세지

            // fileKeyConfig.json / prodFilePath.json 상에 키 설정이 없어 비교를 수행하지 못한 파일인지 여부
            public bool IsConfigMissing { get; set; }

            // 파일 비교가 가능한지 여부.
            // false → N/A 출력 (파일 단독 존재 / 키 설정 없음 / 파싱 실패 등)
            public bool IsComparable { get; set; } = true;

            // 중복키 상세 메시지 (있으면 더블클릭 시 사용)
            public string? DuplicateKeyMessage { get; set; }

            // 한쪽 폴더에만 존재하는 파일인지 여부
            public bool IsFileOnly
                => string.IsNullOrEmpty(File1Path) || string.IsNullOrEmpty(File2Path);

            // 중복키일 경우 "-" , 비교불가일 경우 "N/A"
            public string AddedCountText
                => !string.IsNullOrEmpty(DuplicateKeyMessage)
                    ? "-"                               
                    : (!IsComparable ? "N/A" : AddedCount.ToString());

            public string DeletedCountText
                => !string.IsNullOrEmpty(DuplicateKeyMessage)
                    ? "-"
                    : (!IsComparable ? "N/A" : DeletedCount.ToString());

            public string ModifiedCountText
                => !string.IsNullOrEmpty(DuplicateKeyMessage)
                    ? "-"
                    : (!IsComparable ? "N/A" : ModifiedCount.ToString());

            // 변경여부
            public bool HasDiff 
                => (AddedCount > 0) || (DeletedCount > 0) || (ModifiedCount > 0);
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

        private string _progressMessage = string.Empty;
        public string ProgressMessage
        {
            get => _progressMessage;
            set { _progressMessage = value; OnPropertyChanged(); }
        }

        public ObservableCollection<DiffGridItem> GridItems { get; } = new();
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

            // 관리자 여부 판별
            try
            {
                var currentUser = Util.GfnGetUserName();                // 현재 로그인/사용자 사번
                var adminList = FileKeyManager.GetAdminEmpNos();        // fileKeyConfig.json의 adminEmpNo 배열

                IsAdmin = adminList.Any(x =>
                    string.Equals(x, currentUser, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                IsAdmin = false; // 문제 생기면 그냥 관리자 아님으로
            }

            SelectedStandardFolder = Status.All;
            SelectedTargetFolder = Status.All;

            /// 커맨드 초기화(Busy 상태에 따라 실행 가능 여부 제어) //TODO 수정
            SelectFolderCommand = new RelayCommand(SelectFolder, () => !IsBusy);
            InitCommand = new RelayCommand(Init, () => !IsBusy);
            ApplyCommand = new RelayCommand(ApplySelected, () => !IsBusy && IsAdmin);
            ApplyAllCommand = new RelayCommand(ApplyAll, () => !IsBusy && IsAdmin);
            CompareCommand = new RelayCommand(async () => await CompareFile(), () => !IsBusy);

        }

        // 바인딩용 프로퍼티
        public string ResultsText { get; set; } = string.Empty;
        public bool IsComparing { get; set; }
        public string LiteralText { get; set; } = string.Empty;

        // =========================================================
        // [검색조건 프로퍼티] - 사용자 입력값을 바인딩받는 프로퍼티들
        // =========================================================
        private string? _baseFilePath;      // 기준경로
        public string? BaseFilePath 
        { 
            get => _baseFilePath; 
            set => SetProperty(ref _baseFilePath, value);
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
        private string _selectedStandardFolder = Status.All; // TODO 수정
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
        private string _selectedTargetFolder = Status.All;
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

        // === 상태 플래그: 수정 중 여부 ===
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
                StandardFolderList.Clear();
                TargetFolderList.Clear();

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

                // 설정: baseFolderName 배열 로드 (Standard 폴더 기준)
                var baseFolderSet = new HashSet<string>(
                    FileKeyManager.GetBaseFolders(),
                    StringComparer.OrdinalIgnoreCase
                );

                // 내부 폴더 콤보리스트 생성
                foreach (var folder in subFolders)
                {
                    var folderName = Path.GetFileName(folder);

                    // ===========================
                    // 1) StandardFolderList
                    //    → baseFolderName 에 포함된 폴더만 추가
                    // ===========================
                    if (baseFolderSet.Contains(folderName))
                    {
                        if (!StandardFolderList.Any(x => x.Code.Equals(folder, StringComparison.OrdinalIgnoreCase)))
                        {
                            StandardFolderList.Add(new OptionItem(
                                Code: folder,
                                Name: folderName
                            ));
                        }
                    }

                    // ===========================
                    // 2) TargetFolderList
                    //    → baseFolderName 에 포함되지 않은 폴더만 추가
                    // ===========================
                    if (!baseFolderSet.Contains(folderName))
                    {
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

        }

        /// <summary>
        /// 기준 폴더와 비교 폴더의 CSV/IO 파일을 파일명 기준으로 매칭하여,
        /// fileKeyConfig.json 설정(키컬럼/dupKeyColumns/날짜 컬럼)에 따라
        /// 행 단위 차이를 분석하여 그리드에 표시한다.
        ///
        /// 주요 기능:
        /// - 키 기반 정밀 비교(Added / Deleted / Modified 계산)
        /// - 중복 키 발생 시 비교 중단 및 메시지 표시
        /// - 키 정보가 없는 파일은 비교 불가로 처리
        /// - 한쪽 폴더에만 존재하는 파일은 추가/삭제로 분류
        /// - 더블클릭 시 상세 Diff 창(ReadOnlyDiffWindow) 제공
        ///
        /// 비고:
        /// - 단순 라인 비교가 아닌 “키 기반 비교” 방식이며,
        ///   설정 오류 또는 중복 키가 있으면 정상 비교가 제한될 수 있다.
        /// </summary>
        public async Task CompareFile()
        {
            // 기준경로 선택여부
            if (string.IsNullOrWhiteSpace(BaseFilePath))
            {
                MessageBox.Show("기준경로 폴더 선택하세요.", "안내",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            //폴더 검증
            if (string.IsNullOrWhiteSpace(SelectedStandardFolder) ||
                string.IsNullOrWhiteSpace(SelectedTargetFolder))
            {
                MessageBox.Show("기준/비교 폴더를 먼저 선택하세요.", "안내",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!Directory.Exists(SelectedStandardFolder) || !Directory.Exists(SelectedTargetFolder))
            {
                MessageBox.Show("선택한 폴더 경로가 존재하지 않습니다.", "오류",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            IsBusy = true;
            IsProgressVisible = true;
            ProgressValue = 0;
            ProgressMessage = "파일 목록을 스캔하는 중...";

            GridItems.Clear();

            try
            {
                // csv, io파일만비교
                var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { FileConstants.Extensions.Csv, FileConstants.Extensions.Io};

                // 1) 좌/우 파일 스캔
                var leftFiles = Directory.EnumerateFiles(SelectedStandardFolder, "*.*", SearchOption.TopDirectoryOnly)
                                         .Where(p => allowed.Contains(Path.GetExtension(p)));
                var rightFiles = Directory.EnumerateFiles(SelectedTargetFolder, "*.*", SearchOption.TopDirectoryOnly)
                                          .Where(p => allowed.Contains(Path.GetExtension(p)));

                // 2) 파일명 기준 맵
                var leftMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var rightMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var p in leftFiles) leftMap[Path.GetFileName(p)] = p;
                foreach (var p in rightFiles) rightMap[Path.GetFileName(p)] = p;

                // 3) 합집합
                var allNames = new SortedSet<string>(leftMap.Keys, StringComparer.OrdinalIgnoreCase);
                foreach (var n in rightMap.Keys) allNames.Add(n);

                int total = allNames.Count;
                int done = 0;

                int no = (GridItems?.Count ?? 0) == 0 ? 1 : GridItems.Count + 1;

                // 누적 통계
                int totalFileAdded = 0;
                int totalFileDeleted = 0;
                int totalRowAdded = 0;
                int totalRowDeleted = 0;
                int totalRowModified = 0;

                var changedFiles = new List<string>();
                var failedFiles = new List<string>();

                ProgressMessage = $"총 {total}개 파일 비교 시작";

                // 4) 파일별 비교
                foreach (var name in allNames)
                {
                    var leftPath = leftMap.TryGetValue(name, out var lp) ? lp : null;
                    var rightPath = rightMap.TryGetValue(name, out var rp) ? rp : null;

                    var item = new DiffGridItem
                    {
                        No = no++,
                        FileName = name,
                        File1Path = leftPath ?? string.Empty,
                        File2Path = rightPath ?? string.Empty,
                        AddedCount = 0,
                        DeletedCount = 0,
                        ModifiedCount = 0,
                        Message = string.Empty,
                        IsComparable = true,
                        DuplicateKeyMessage = string.Empty
                    };

                    // ─────────────────────────────────────────────
                    // fileKeyConfig.json 키 설정이 없는 파일 처리
                    // ─────────────────────────────────────────────
                    var (delimiter, keys) = FileKeyManager.GetFileSettingsOrDefault(name);

                    if (keys == null || keys.Count == 0)
                    {
                        // 키 정보가 없어서 "행 단위 비교" 자체를 할 수 없는 경우
                        item.IsComparable = false;   // → Added/Deleted/Modified = N/A
                        item.Message = "\"fileKeyConfig.json\", \"prodFilePath.json\" 에 목록 추가 필요";

                        GridItems.Add(item);

                        done++;
                        ProgressValue = (int)(done * 100.0 / Math.Max(1, total));
                        ProgressMessage = $"{done} / {total} 파일 처리 중...";
                        continue; // 이 파일은 비교 스킵
                    }

                    if (leftPath != null && rightPath != null)
                    {
                        // 키컬럼 기반 비교
                        var diff = await Task.Run(() => DiffService.CompareByFileKeys(leftPath, rightPath));
                        // 중복키가 있는 경우: "중복코드 존재"만 표시하고 요약 통계에는 반영 안 함
                        if (diff.HasDuplicateKeys)
                        {
                            item.Message = $"중복된 Key값이 존재함(기준파일:{diff.lDuplicateCount}건, 비교파일:{diff.rDuplicateCount}건)";
                            item.DuplicateKeyMessage = diff.DuplicateKeyMessage;

                            // 비교는 의미 없으니 카운트는 0
                            item.AddedCount = 0;
                            item.DeletedCount = 0;
                            item.ModifiedCount = 0;

                            // IsComparable 은 true 로 둬야 더블클릭 가능
                        }
                        else
                        { 
                            item.AddedCount = diff.Added;
                            item.DeletedCount = diff.Deleted;
                            item.ModifiedCount = diff.Modified;

                            totalRowAdded += diff.Added;
                            totalRowDeleted += diff.Deleted;
                            totalRowModified += diff.Modified;

                            if ((diff.Added + diff.Deleted + diff.Modified) == 0)
                                item.Message = "변경 없음";
                            else
                            {
                                //item.Message = $"변경됨 (+{diff.Added} / -{diff.Deleted} / ⋯{diff.Modified})";
                                item.Message = "차이 있음";
                                changedFiles.Add(name);
                            }
                        }
                    }
                    else if (leftPath != null)
                    {
                        // 파일만 기준에 존재 → 행 비교 불가 → N/A
                        item.IsComparable = false;
                        item.Message = "기준에서만 존재";
                        totalFileDeleted++;
                        changedFiles.Add(name);
                    }
                    else
                    {
                        // 파일만 비교에 존재 → 행 비교 불가 → N/A
                        item.IsComparable = false;
                        item.Message = "비교에서만 존재";
                        totalFileAdded++;
                        changedFiles.Add(name);
                    }

                    GridItems.Add(item);

                    // ========= 진행률 =========
                    done++;
                    ProgressValue = (int)(done * 100.0 / Math.Max(1, total));
                    ProgressMessage = $"{done} / {total} 파일 처리 중...";
                }

                IsProgressVisible = false;

                // 5) MessageBox로 완료 요약
                var sb = new StringBuilder();
                sb.AppendLine($"총 {total}개 파일 비교 완료");
                //sb.AppendLine();
                //sb.AppendLine($"파일 추가: {totalFileAdded}");
                //sb.AppendLine($"파일 삭제: {totalFileDeleted}");
                //sb.AppendLine($"행 추가:   {totalRowAdded}");
                //sb.AppendLine($"행 삭제:   {totalRowDeleted}");
                //sb.AppendLine($"행 수정:   {totalRowModified}");

                if (changedFiles.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("[변경된 파일]");
                    foreach (var x in changedFiles.Take(10))
                        sb.AppendLine(" - " + x);
                    if (changedFiles.Count > 10)
                        sb.AppendLine($"…외 {changedFiles.Count - 10}건");
                }

                if (failedFiles.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("[비교 실패 파일]");
                    foreach (var x in failedFiles.Take(10))
                        sb.AppendLine(" - " + x);
                    if (failedFiles.Count > 10)
                        sb.AppendLine($"…외 {failedFiles.Count - 10}건");
                }

                MessageBox.Show(sb.ToString(), "비교 완료", MessageBoxButton.OK,
                    failedFiles.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"비교 중 오류가 발생했습니다.\n\n{ex.Message}", "오류",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
                IsProgressVisible = false;
                ProgressMessage = string.Empty;
            }
        }

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
            SelectedStandardFolder = Status.All;
            SelectedTargetFolder = Status.All;
            GridItems.Clear();
        }

        /// <summary>
        /// 선택 건 적용
        /// Standard 기준으로
        ///  - Standard vs 관리자 파일(S vs A)의 변경 상품 목록
        ///  - Standard vs 특정 사번 파일(S vs U)의 변경 상품 목록
        ///    두 변경목록이 겹치면 충돌로 간주하고 머지를 중단.
        ///    겹치지 않으면, 특정 사번(U)에서 변경된 행들을
        ///    관리자 파일(A)에 키 기준으로 추가/덮어쓰기 후 정렬하여 저장한다.
        ///
        /// 제약 사항:
        /// - 선택된 행에 중복 키가 존재하는 경우(중복코드 존재 파일)는 적용 대상에서 제외되며,
        ///   안내 메시지를 표시한 뒤 작업을 중단한다.
        /// </summary>
        private void ApplySelected()
        {
            try
            {
                if (SelectedItem == null)
                {
                    MessageBox.Show("적용할 행을 선택해 주세요.", "안내",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // 중복키 파일은 적용 불가
                if (!string.IsNullOrEmpty(SelectedItem.DuplicateKeyMessage))
                {
                    MessageBox.Show(
                        "중복 키가 존재하는 파일은 적용할 수 없습니다.\n",
                        "적용 불가",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                var standardFilePath = SelectedItem.File1Path;  // 기준폴더경로
                var userFilePath = SelectedItem.File2Path;      // 사번폴더

                if (string.IsNullOrWhiteSpace(standardFilePath) ||
                    string.IsNullOrWhiteSpace(userFilePath))
                {
                    MessageBox.Show("기준/비교 파일이 모두 존재해야 합니다.", "오류",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                if (!File.Exists(standardFilePath) || !File.Exists(userFilePath))
                {
                    MessageBox.Show("기준/비교 파일이 모두 존재해야 합니다.", "오류",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var fileName = Path.GetFileName(standardFilePath);
                var adminFolderPath = Path.Combine(BaseFilePath, Util.GfnGetUserName());
                Directory.CreateDirectory(adminFolderPath);

                // 충돌 비교 후 머지
                var (hasChanges, added, deleted, modified)
                    = MergeUserChangesToAdmin(standardFilePath, userFilePath, adminFolderPath);

                if (!hasChanges)
                {
                    MessageBox.Show("특정 사번 파일 기준으로 적용할 변경 내역이 없습니다.", "안내",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // 요약 메시지
                var sb = new StringBuilder();
                sb.AppendLine("선택된 파일의 변경 사항을 관리자 파일에 적용했습니다.");
                sb.AppendLine();
                sb.AppendLine($"파일명: {fileName}");
                sb.AppendLine($"사용자 변경 Key 개수: {added + deleted + modified}");
                sb.AppendLine($" - 추가:   {added}");
                sb.AppendLine($" - 삭제:   {deleted}");
                sb.AppendLine($" - 수정:   {modified}");

                MessageBox.Show(sb.ToString(), "적용 완료",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"적용 중 오류\n:{ex.Message}", "오류",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 전체 적용
        /// - 그리드의 모든 행을 대상으로 MergeUserChangesToAdmin 로직을 수행한다.
        /// - 작업 시작 전, 대상 관리자 파일(.csv/.io)을 백업해 두고
        ///   처리 중 하나라도 오류가 발생하면 전체 변경을 백업본으로 롤백한다.
        /// - 모든 파일이 정상 처리되면 백업 파일은 삭제된다.
        /// - 단, 그리드에 중복 키(DuplicateKeyMessage)가 있는 파일이 하나라도 있으면
        ///   전체 적용은 수행되지 않으며 경고 메시지를 띄운 후 작업을 중단한다.
        /// </summary>
        private void ApplyAll()
        {
            // 0) 기본 검증
            if (GridItems == null || GridItems.Count == 0)
            {
                MessageBox.Show("적용할 대상이 없습니다.", "안내",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (string.IsNullOrWhiteSpace(BaseFilePath))
            {
                MessageBox.Show("기준 경로가 설정되어 있지 않습니다.", "오류",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // 중복키가 있는 파일이 하나라도 있으면 전체 적용 불가
            var dupItems = GridItems
                .Where(x => !string.IsNullOrEmpty(x.DuplicateKeyMessage))
                .ToList();

            if (dupItems.Count > 0)
            {
                var sbDup = new StringBuilder();
                sbDup.AppendLine("중복 키가 존재하는 파일이 있어 전체 적용을 수행할 수 없습니다.");
                sbDup.AppendLine();
                sbDup.AppendLine("[중복 키 존재 파일]");

                foreach (var it in dupItems.Take(10))
                    sbDup.AppendLine(" - " + it.FileName);

                if (dupItems.Count > 10)
                    sbDup.AppendLine($"…외 {dupItems.Count - 10}건");

                sbDup.AppendLine();

                MessageBox.Show(sbDup.ToString(), "전체 적용 불가",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var confirm = MessageBox.Show(
                "그리드에 표시된 전체 파일에 대해 변경 사항을 관리자 파일에 적용하시겠습니까?",
                "전체 적용 확인",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes)
                return;

            var adminFolderPath = Path.Combine(BaseFilePath, Util.GfnGetUserName());
            Directory.CreateDirectory(adminFolderPath);

            // 스냅샷 (중간에 GridItems 변경되어도 안전하게)
            var items = GridItems
                .Where(x =>
                !string.IsNullOrWhiteSpace(x.File1Path) &&
                !string.IsNullOrWhiteSpace(x.File2Path))
                .ToList();

            // adminFilePath -> backupFilePath
            var backupMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                // 1) 백업 생성 (관리자 파일이 존재하는 경우만)
                foreach (var item in items)
                {
                    var standardFilePath = item.File1Path;
                    if (string.IsNullOrWhiteSpace(standardFilePath))
                        continue;

                    var fileName = Path.GetFileName(standardFilePath);
                    var adminFilePath = Path.Combine(adminFolderPath, fileName);

                    if (File.Exists(adminFilePath) && !backupMap.ContainsKey(adminFilePath))
                    {
                        var backupPath = adminFilePath + ".bak_applyall";
                        File.Copy(adminFilePath, backupPath, overwrite: true);
                        backupMap[adminFilePath] = backupPath;
                    }
                }

                int totalAdded = 0;
                int totalDeleted = 0;
                int totalModified = 0;

                // 2) 실제 머지 수행
                foreach (var item in items)
                {
                    var standardFilePath = item.File1Path;  // 기준폴더
                    var userFilePath = item.File2Path;  // 사번폴더

                    if (string.IsNullOrWhiteSpace(standardFilePath) ||
                        string.IsNullOrWhiteSpace(userFilePath))
                    {
                        throw new InvalidOperationException(
                            $"파일 '{item.FileName}'의 기준/비교 파일 경로가 올바르지 않습니다.");
                    }

                    if (!File.Exists(standardFilePath) || !File.Exists(userFilePath))
                    {
                        throw new FileNotFoundException(
                            $"파일 '{item.FileName}'의 기준/비교 파일이 모두 존재해야 합니다.");
                    }

                    // 충돌 비교 후 머지
                    var (hasChanges, added, deleted, modified)
                        = MergeUserChangesToAdmin(standardFilePath, userFilePath, adminFolderPath);

                    if (!hasChanges)
                        continue;

                    totalAdded += added;
                    totalDeleted += deleted;
                    totalModified += modified;
                }

                // 3) 모든 파일 성공 처리 → 백업 삭제
                foreach (var kv in backupMap)
                {
                    var backupPath = kv.Value;
                    if (File.Exists(backupPath))
                        File.Delete(backupPath);
                }

                var msg = new StringBuilder();
                msg.AppendLine("전체 적용이 완료되었습니다.");
                //msg.AppendLine();
                //msg.AppendLine($"총 추가:   {totalAdded}");
                //msg.AppendLine($"총 삭제:   {totalDeleted}");
                //msg.AppendLine($"총 수정:   {totalModified}");

                MessageBox.Show(msg.ToString(), "전체 적용",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                // 롤백: 관리자 파일을 백업본으로 되돌리고, 백업파일 삭제
                foreach (var kv in backupMap)
                {
                    var adminFilePath = kv.Key;
                    var backupPath = kv.Value;

                    try
                    {
                        if (File.Exists(adminFilePath))
                            File.Delete(adminFilePath);

                        if (File.Exists(backupPath))
                            File.Move(backupPath, adminFilePath, overwrite: true);
                    }
                    catch
                    {
                        // 롤백 중 추가 오류는 여기서는 무시하고 최종 메시지로 안내
                    }
                }

                MessageBox.Show(
                    $"전체 적용 중 오류가 발생하여 모든 변경을 롤백했습니다.\n\n{ex.Message}",
                    "전체 적용 실패",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        /// <summary>
        ///  - Standard vs Admin(S vs A)
        ///  - Standard vs User(S vs U)
        /// 비교를 수행한 뒤, 충돌이 없을 경우 User 변경분을 Admin 파일에 머지한다.
        /// 충돌 여부는 fileKeyConfig.json 의 dupKeyColumns(상품 단위 기준)으로 판단한다.
        /// </summary>
        /// <param name="standardFilePath">기준(Standard) 파일 경로</param>
        /// <param name="userFilePath">사용자(User) 파일 경로</param>
        /// <param name="adminFolderPath">관리자 파일이 위치할 폴더 경로(사번 폴더)</param>
        /// <returns>
        /// hasChanges : 사용자 기준 실제 변경 키가 있는지 여부  
        /// added / deleted / modified : diffStdUser 기준 변경 건수
        /// </returns>
        private (bool hasChanges, int added, int deleted, int modified)
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

            // 1) Standard vs Admin (S vs A) 변경 키셋 (full key 기준)
            var diffStdAdmin = DiffService.CompareByFileKeys(standardFilePath, adminFilePath);
            var adminChangedKeys = BuildChangedKeySet(diffStdAdmin);   // HashSet<string> (full key)

            // 2) Standard vs User (S vs U) 변경 키셋 (full key 기준)
            var diffStdUser = DiffService.CompareByFileKeys(standardFilePath, userFilePath);
            var userChangedKeys = BuildChangedKeySet(diffStdUser);     // HashSet<string> (full key)

            // 적용할 변경이 없으면 파일 변경 없음으로 처리
            if (userChangedKeys.Count == 0)
                return (false, diffStdUser.Added, diffStdUser.Deleted, diffStdUser.Modified);

            // 3) 충돌 상품 키셋 = 관리자 변경 상품 키 ∩ 사용자 변경 상품 키 (dupKeyColumns 기준)
            var adminProductKeys = DiffService.ProjectKeysToDupKeys(adminChangedKeys, keyColumns, dupKeyColumns);
            var userProductKeys = DiffService.ProjectKeysToDupKeys(userChangedKeys, keyColumns, dupKeyColumns);

            var conflictKeys = adminProductKeys.Intersect(userProductKeys).ToList();
            if (conflictKeys.Count > 0)
            {
                var sbConflict = new StringBuilder();
                sbConflict.AppendLine("다른 사번이 수정한 동일 특약에 대해 중복 수정은 허용되지 않습니다.(상품단위 충돌)");
                sbConflict.AppendLine($"파일명: {fileName}");
                sbConflict.AppendLine();
                sbConflict.AppendLine($"충돌 상품 Key 개수: {conflictKeys.Count}");
                sbConflict.AppendLine();

                // dupKeyColumns 사용하여 컬럼 구분 출력
                sbConflict.AppendLine("충돌 상품 Key 상세(최대 10개):");

                bool headerPrinted = false;

                foreach (var key in conflictKeys.Take(10))
                {
                    var parts = key.Split(FileKeyManager.UnitSeparator);

                    // 1) 첫 줄에는 dupKeyColumns 컬럼명만 출력
                    if (!headerPrinted)
                    {
                        var header = string.Join(" / ", dupKeyColumns);
                        sbConflict.AppendLine(" - " + header);
                        headerPrinted = true;
                    }

                    // 2) 이후에는 값만 출력
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

            // 4) 머지 수행:
            // 관리자 파일(adminFilePath)을 테이블로 로드
            var adminTable = LoadTabularForMerge(adminFilePath);
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

            // 삭제: 사용자 측에서 삭제된 키는 관리자 맵에서도 제거
            foreach (var del in diffStdUser.DeletedRows)
            {
                if (!string.IsNullOrEmpty(del.Key))
                    adminMap.Remove(del.Key);
            }

            // 추가: 사용자 기준 최신 행(RightRow)을 그대로 덮어씀
            foreach (var add in diffStdUser.AddedRows)
            {
                var row = (add.RightRow ?? Array.Empty<string>()).ToList();
                adminMap[add.Key] = row;
            }

            // 수정: 사용자 기준 최신 행(RightRow)을 그대로 덮어씀
            foreach (var mod in diffStdUser.ModifiedRows)
            {
                var row = (mod.RightRow ?? Array.Empty<string>()).ToList();
                adminMap[mod.Key] = row;
            }

            // 키 기준 정렬 후 테이블 재구성
            // 중복 키가 있으면 → 전체 라인 문자열(full line) 기준으로 2차 정렬
            var mergedRows = adminMap
                .OrderBy(
                    kv => kv,   // 이제 composite comparer가 (key, fullLine) 둘 다 비교
                    Comparer<KeyValuePair<string, List<string>>>.Create((a, b) =>
                        CompareCompositeKey(
                            a.Key,                    // keyX
                            b.Key,                    // keyY
                            keyColumns,
                            string.Join("|", a.Value), // fullLineX
                            string.Join("|", b.Value)  // fullLineY
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
        /// 복합 키를 비교하여 정렬 순서를 결정한다.
        /// - keyColumns 순서대로 값을 비교하며, 날짜 형식은 날짜로 우선 비교한다.
        /// - 키값이 모두 동일하면 full line 문자열을 2차 비교하여 안정적으로 정렬한다.
        /// </summary>
        private static int CompareCompositeKey(string keyX, string keyY, IReadOnlyList<string> keyColumns, string fullLineX, string fullLineY)
        {
            // keyParts: keyColumns 순서대로 분리된 키 값 배열
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
                    // 일반 문자열 비교금
                    result = string.CompareOrdinal(xv, yv);
                }

                // 키 값이 다르면 즉시 반환안되며
                if (result != 0)
                    return result;
            }

            // 2) 모든 키값이 동일한 경우 >> full line 전체 비교로 2차정렬
            //     (중복 키 발생 시 정렬 안정성 확보 목적)
            return string.CompareOrdinal(fullLineX ?? string.Empty, fullLineY ?? string.Empty);
        }

        /// <summary>
        /// DiffResult에서 Added/Deleted/Modified에 등장한 모든 Key 집합을 만든다.
        /// (충돌 검사용)
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
        /// IO/CSV 파일을 머지용 테이블(List<List<string>>)로 로드한다.
        /// - .io : IIO.IOtoCsv(path).csvData 사용
        /// - .csv: ParserUtil.ParsePipeDelimitedCsv 사용
        /// </summary>
        private static List<List<string>> LoadTabularForMerge(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();

            if (ext == FileConstants.Extensions.Io)
            {
                // IO → CSV 테이블 변환
                return IIO.IOtoCsv(path).csvData;
            }

            // CSV 파서 사용
            return ParserUtil.ParsePipeDelimitedCsv(path);
        }

        /// <summary>
        /// List<List<string>> 형태의 CSV 데이터를
        /// 파일 확장자에 따라 CSV(.csv) 또는 IO(.io) 파일로 저장한다.
        /// </summary>
        /// <param name="path">저장할 대상 파일 경로(.csv 또는 .io)</param>
        /// <param name="csvData">첫 행을 헤더로 갖는 CSV 형식 데이터</param>
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

        /// <summary>
        /// 행 더블클릭 이벤트
        /// </summary>
        public ICommand RowActionCommand => new RelayCommand<DiffGridItem>(
            async row =>
            {
                if (row == null)
                {
                    MessageBox.Show("선택된 행이 없습니다.", "파일 비교", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 중복키 파일인 경우: Diff창 대신 중복 상세 메시지 표시
                if (!string.IsNullOrEmpty(row.DuplicateKeyMessage))
                {
                    MessageBox.Show(
                        row.DuplicateKeyMessage,
                        "중복 키 존재 - 비교 중단",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);

                    //return;  //메시지 보여주고 DIff창 띄우지 않을시에 주석해제
                }

                // 비교 불가 파일(N/A 파일)은 Diff창 띄우지 않음
                if (!row.IsComparable)
                {
                    MessageBox.Show(
                        "선택한 파일은 상세 비교를 지원하지 않습니다.\n\n" +
                        "가능한 원인:\n" +
                        "- fileKeyConfig.json / prodFilePath.json 에 키 설정이 없습니다.\n" +
                        "- 기준 폴더 또는 비교 폴더 중 한쪽에만 파일이 존재합니다.\n\n" +
                        "해당 설정을 확인한 후 다시 시도하세요.",
                        "상세 비교 불가",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                var leftFilePath = row.File1Path;
                var rightFilePath = row.File2Path;

                if (string.IsNullOrWhiteSpace(leftFilePath) ||
                    string.IsNullOrWhiteSpace(rightFilePath))
                {
                    MessageBox.Show("좌/우 파일이 모두 존재해야 합니다.", "파일 비교", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!File.Exists(leftFilePath) || !File.Exists(rightFilePath))
                {
                    MessageBox.Show("좌/우 파일이 모두 존재해야 합니다.", "파일 비교", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 읽기 전용 Diff 창 띄우기
                var win = new ReadOnlyDiffWindow(leftFilePath, rightFilePath)
                {
                    Owner = Application.Current?.MainWindow,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };
                win.ShowDialog();
            },

            // CanExecute: 선택된 행이 있고 AND 비교 가능한 상태일 때만 허용
            row => row != null
        );

    }
}
