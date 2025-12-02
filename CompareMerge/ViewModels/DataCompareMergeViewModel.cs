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

        private bool _isAdmin;
        public bool IsAdmin
        {
            get => _isAdmin;
            set => SetProperty(ref _isAdmin, value);
        }

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

            public string Message { get; set; } = "";    // 메세지
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
        /// 선택된 기준 폴더(SelectedStandardFolder)와 비교 폴더(SelectedTargetFolder)에 존재하는
        /// 동일 파일을 키컬럼 기반으로 비교하여 “추가 / 삭제 / 수정” 여부를 판단한 후 그리드(DiffGrid)에 표시
        ///
        /// 주요 동작:
        /// 1. 기준 폴더와 비교 폴더에서 동일한 파일명을 매칭
        /// 2. fileKeyConfig.json에 정의된 키 컬럼을 기준으로 각 파일을 로드 후 행 단위 비교
        /// 3. DiffKit.CompareByFileKeys()를 통해 Added / Deleted / Modified 계산
        /// 4. 계산된 결과를 DiffGridItem 리스트로 생성하고 UI에 표시
        /// 5. 비교 결과는 파일 전체 행 기준이 아닌 "키컬럼 기반"으로 정확하게 판단됩니다.
        ///
        /// 예외 처리:
        /// - 기준/비교 폴더가 선택되지 않았거나 파일이 없으면 MessageBox 안내
        /// - 처리 중 오류는 MessageBox로 표시
        ///
        /// 비고:
        /// - 기존 라인 단위 비교(logic)보다 정밀하며, 파일 별 KeyColumns가 선언되지 않은 경우
        ///   전체행 기반 기본 비교 방식으로 자동 폴백됩니다.
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
                // csv, io파일만비교(TODO 이관대상만 수정하게 수정)
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
                        File1Path = leftPath ?? "",
                        File2Path = rightPath ?? "",
                        AddedCount = 0,
                        DeletedCount = 0,
                        ModifiedCount = 0,
                        Message = ""
                    };

                    if (leftPath != null && rightPath != null)
                    {
                        try
                        {
                            // 키컬럼 기반 비교
                            var diff = await Task.Run(() => DiffService.CompareByFileKeys(leftPath, rightPath));

                            item.AddedCount = diff.Added;
                            item.DeletedCount = diff.Deleted;
                            item.ModifiedCount = diff.Modified;

                            totalRowAdded += diff.Added;
                            totalRowDeleted += diff.Deleted;
                            totalRowModified += diff.Modified;

                            if ((diff.Added + diff.Deleted + diff.Modified) == 0)
                                item.Message = "동일";
                            else
                            {
                                //item.Message = $"변경됨 (+{diff.Added} / -{diff.Deleted} / ⋯{diff.Modified})";
                                item.Message = $"변경";
                                changedFiles.Add(name);
                            }
                        }
                        catch (Exception ex)
                        {
                            item.Message = $"비교 실패: {ex.Message}";
                            failedFiles.Add(name);
                        }
                    }
                    else if (leftPath != null)
                    {
                        // 파일 자체 삭제
                        //item.DeletedCount = 1;
                        item.Message = "기준 폴더에만 파일존재";
                        totalFileDeleted++;
                        changedFiles.Add(name);
                    }
                    else
                    {
                        // 파일 자체 추가
                        //item.AddedCount = 1;
                        item.Message = "비교 폴더에만 파일존재";
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
                ProgressMessage = "";
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
        ///  - Standard vs 관리자 파일(S vs A)의 변경 키셋
        ///  - Standard vs 특정 사번 파일(S vs U)의 변경 키셋을 만든 후
        ///    두 키셋이 겹치면 충돌로 간주하고 머지를 중단.
        ///    겹치지 않으면, 특정 사번(U)에서 변경된 행들을
        ///    관리자 파일(A)에 키 기준으로 추가/덮어쓰기 후 정렬하여 저장한다.
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
                MessageBox.Show($"적용 중 오류: {ex.Message}", "오류",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 전체 적용
        /// - 그리드의 모든 행을 대상으로 MergeUserChangesToAdmin 로직을 수행한다.
        /// - 작업 시작 전, 대상 관리자 파일(.csv/.io)을 백업해 두고
        ///   처리 중 하나라도 오류가 발생하면 전체 변경을 백업본으로 롤백한다.
        /// - 모든 파일이 정상 처리되면 백업 파일은 삭제된다.
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
        /// 단일 파일에 대해
        ///  - Standard vs Admin(S vs A)
        ///  - Standard vs User (S vs U)
        /// 를 비교하여,
        /// 충돌이 없으면 User 쪽 변경분을 Admin 파일에 키 기준으로 머지/저장한다.
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

            // 1) Standard vs Admin (S vs A) 변경 키셋
            var diffStdAdmin = DiffService.CompareByFileKeys(standardFilePath, adminFilePath);
            var adminChangedKeys = BuildChangedKeySet(diffStdAdmin);

            // 2) Standard vs User (S vs U) 변경 키셋
            var diffStdUser = DiffService.CompareByFileKeys(standardFilePath, userFilePath);
            var userChangedKeys = BuildChangedKeySet(diffStdUser);

            // 적용할 변경이 없으면 파일 변경 없음으로 처리
            if (userChangedKeys.Count == 0)
                return (false, diffStdUser.Added, diffStdUser.Deleted, diffStdUser.Modified);

            // 3) 충돌 키셋 = 관리자 변경 키 ∩ 사용자 변경 키
            var conflictKeys = adminChangedKeys.Intersect(userChangedKeys).ToList();
            if (conflictKeys.Count > 0) 
            {
                var sbConflict = new StringBuilder();
                sbConflict.AppendLine("관리자 변경 영역과 특정 사번 변경 영역이 겹쳐서 적용할 수 없습니다.");
                sbConflict.AppendLine($"파일명: {fileName}");
                sbConflict.AppendLine();
                sbConflict.AppendLine($"충돌 Key 개수: {conflictKeys.Count}");
                sbConflict.AppendLine();

                // keyColumns 사용하여 컬럼 구분 출력
                sbConflict.AppendLine("충돌 Key 상세(최대 10개):");

                bool headerPrinted = false;

                foreach (var key in conflictKeys.Take(10))
                {
                    var parts = key.Split(FileKeyManager.UnitSeparator);

                    // 1) 첫 줄에는 컬럼명만 출력
                    if (!headerPrinted)
                    {
                        var header = string.Join(" / ", keyColumns);
                        sbConflict.AppendLine(" - " + header);
                        headerPrinted = true;
                    }

                    // 2) 이후에는 값만 출력
                    var values = new List<string>();
                    for (int i = 0; i < keyColumns.Count; i++)
                    {
                        string colVal = (i < parts.Length ? parts[i] : "");
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
            var mergedRows = adminMap
                .OrderBy(
                    kv => kv.Key,
                    Comparer<string>.Create((x, y) => CompareCompositeKey(x, y, keyColumns))
                )
                .Select(kv => kv.Value)
                .ToList();

            var finalTable = new List<List<string>> { headerRow };
            finalTable.AddRange(mergedRows);

            // CSV/IO 파일로 저장
            SaveCsvOrIoFile(adminFilePath, finalTable);

            return (true, diffStdUser.Added, diffStdUser.Deleted, diffStdUser.Modified);
        }
        // 날짜로 취급할 키 컬럼 이름
        private static readonly HashSet<string> DateKeyNames =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "valid_date",
                "expiration_date"
                // 추가
            };

        private static int CompareCompositeKey(string keyX, string keyY, IReadOnlyList<string> keyColumns)
        {
            var xParts = (keyX ?? string.Empty).Split('\u001F');
            var yParts = (keyY ?? string.Empty).Split('\u001F');

            int maxIndex = keyColumns.Count;

            for (int i = 0; i < maxIndex; i++)
            {
                string colName = keyColumns[i];

                string xv = i < xParts.Length ? xParts[i]?.Trim() ?? string.Empty : string.Empty;
                string yv = i < yParts.Length ? yParts[i]?.Trim() ?? string.Empty : string.Empty;

                int result;

                if (DateKeyNames.Contains(colName))
                {
                    DateTime? dx = DateUtil.ParseAnyYmdOrNull(xv);
                    DateTime? dy = DateUtil.ParseAnyYmdOrNull(yv);

                    // 날짜 파싱이 둘 다 실패했으면 문자열 비교로 fallback
                    if (dx is null && dy is null)
                    {
                        result = string.CompareOrdinal(xv, yv);
                    }
                    else
                    {
                        result = Nullable.Compare(dx, dy);
                    }
                }
                else
                {
                    result = string.CompareOrdinal(xv, yv);
                }

                if (result != 0)
                    return result;
            }

            return 0;
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
        /// IO/CSV 파일을 머지용 테이블(List&lt;List&lt;string&gt;&gt;)로 로드한다.
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
        ///  List<List<string>> 형태의 CSV 데이터를
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


        public ICommand RowActionCommand => new RelayCommand<DiffGridItem>(
            async row =>
            {
                if (row == null)
                {
                    MessageBox.Show("선택된 행이 없습니다.", "파일 비교", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            // CanExecute
            row => row != null
        );

    }
}
