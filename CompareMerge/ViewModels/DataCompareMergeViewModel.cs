using InnoPVManagementSystem.Common.Constants;
using InnoPVManagementSystem.Common.Foundation;
using InnoPVManagementSystem.Common.Services;
using InnoPVManagementSystem.Common.ViewModels.Base;
using InnoPVManagementSystem.Modules.Common;
using InnoPVManagementSystem.Modules.CompareMerge.Views;
using System.Collections.ObjectModel;
using System.Data;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using InnoPVManagementSystem.Modules.CompareMerge.Models;
using InnoPVManagementSystem.Modules.CompareMerge.Services;
using System.Threading;

namespace InnoPVManagementSystem.Modules.CompareMerge.ViewModels
{
    /// <summary>
    /// 데이터 비교/머지 화면의 핵심 ViewModel
    /// - 폴더 간 파일 매칭 후 키 기반 diff 요약을 그리드에 표시
    /// - 선택/전체 적용(관리자 머지) 기능 제공
    /// </summary>
    public class DataCompareMergeViewModel : ViewModelBase
    {
        // ===========================
        // 1) Types
        // ===========================

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



        // ===========================
        // 2) Fields
        // ===========================

        private readonly DiffOptions _options;            // 비교 옵션
        private readonly CompareMergeApplyService _applyService;

        private bool _isAdmin;
        private bool _isBusy;
        private bool _isModifying;

        private bool _isProgressVisible;
        private int _progressValue;
        private string _progressMessage = string.Empty;

        private string _baseFilePath = string.Empty;
        private string _selectedStandardFolder = string.Empty;
        private string _selectedTargetFolder = string.Empty;

        private DiffGridItem? _selectedItem;



        // ===========================
        // 3) Bindable Properties
        // ===========================

        public bool IsAdmin
        {
            get => _isAdmin;
            set => SetProperty(ref _isAdmin, value);
        }

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
                    (ApplyAllFilesCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (ApplyAllCommand as RelayCommand)?.RaiseCanExecuteChanged();

                    (ConfirmOkCommand as RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

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
                    (ApplyAllFilesCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (ApplyAllCommand as RelayCommand)?.RaiseCanExecuteChanged();

                    (ConfirmOkCommand as RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public bool IsProgressVisible
        {
            get => _isProgressVisible;
            set { _isProgressVisible = value; OnPropertyChanged(); }
        }


        public int ProgressValue
        {
            get => _progressValue;
            set { _progressValue = value; OnPropertyChanged(); }
        }


        public string ProgressMessage
        {
            get => _progressMessage;
            set { _progressMessage = value; OnPropertyChanged(); }
        }

        public string BaseFilePath
        {
            get => _baseFilePath;
            set => SetProperty(ref _baseFilePath, value);
        }

        public string SelectedStandardFolder
        {
            get => _selectedStandardFolder;
            set => SetProperty(ref _selectedStandardFolder, value);
        }


        public string SelectedTargetFolder
        {
            get => _selectedTargetFolder;
            set => SetProperty(ref _selectedTargetFolder, value);
        }

        public ObservableCollection<OptionItem> StandardFolderList { get; } = new ObservableCollection<OptionItem>();
        public ObservableCollection<OptionItem> TargetFolderList { get; } = new ObservableCollection<OptionItem>();

        public ObservableCollection<DiffGridItem> GridItems { get; } = new();
        public DiffGridItem? SelectedItem
        {
            get => _selectedItem;
            set => SetProperty(ref _selectedItem, value);
        }

        public string ResultsText { get; set; } = string.Empty;
        public bool IsComparing { get; set; }
        public string LiteralText { get; set; } = string.Empty;

        private DataTable? _table;
        public DataTable? Table
        {
            get => _table;
            private set => SetProperty(ref _table, value);
        }



        // ===========================
        // 4) Commands
        // ===========================

        public ICommand SelectFolderCommand { get; }
        public ICommand CompareCommand { get; }
        public ICommand InitCommand { get; }

        // 관리자 전용
        public ICommand ApplyCommand { get; }
        public ICommand ApplyAllFilesCommand { get; }
        public ICommand ApplyAllCommand { get; }

        // 일반유저
        public ICommand ConfirmOkCommand { get; }

        /// <summary>
        /// 더블클릭: 비교 가능 시 ReadOnlyDiffWindow, 중복키면 상세 메시지 우선 표시
        /// </summary>
        public ICommand RowActionCommand => new RelayCommand<DiffGridItem>(
            row =>
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

                    //return;  // 메시지 보여주고 DIff창 띄우지 않을시에 주석해제
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



        // ===========================
        // 5) Ctor
        // ===========================

        /// <summary>
        /// DataCompareMergeViewModel 생성자.
        /// - 비교 옵션 및 서비스 초기화
        /// - 관리자 여부 판별
        /// - 기본 상태값 및 커맨드 바인딩 설정
        /// </summary>
        public DataCompareMergeViewModel()
        {
            _applyService = new CompareMergeApplyService();

            _options = new DiffOptions
            {
                FilePattern = "*.csv;*.io",
                IncludeSubfolders = true,
                LiteralText = "_NOTE_",  // 예: NOTE 이후 꼬리 자르기
                OptimizeThresholdUniqueLines = 1_000_000
            };

            // 관리자 여부는 fileKeyConfig.json의 adminEmpNo 기준
            try
            {
                var currentUser = Util.GfnGetUserName();
                var adminList = FileKeyManager.GetAdminEmpNos();

                IsAdmin = adminList.Any(x =>
                    string.Equals(x, currentUser, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                IsAdmin = false;
            }

            SelectedStandardFolder = string.Empty;
            SelectedTargetFolder = string.Empty;

            SelectFolderCommand = new RelayCommand(SelectFolder, () => !IsBusy);
            CompareCommand = new RelayCommand(async () => await CompareFile(), () => !IsBusy);
            InitCommand = new RelayCommand(Init, () => !IsBusy);

            // 관리자전용
            ApplyCommand = new RelayCommand(ApplySelected, () => !IsBusy && IsAdmin);
            ApplyAllFilesCommand = new RelayCommand(async () => await ApplyAllFilesAsync(), () => !IsBusy && IsAdmin);
            ApplyAllCommand = new RelayCommand(async () => await ApplyAllAsync(), () => !IsBusy && IsAdmin);

            // 일반유저
            ConfirmOkCommand = new RelayCommand(async () => await ConfirmOk(), () => !IsBusy && !IsAdmin);
        }



        // ===========================
        // 6) Public async actions
        // ===========================

        /// <summary>
        /// 기준 폴더/비교 폴더를 파일명 기준으로 매칭 후,
        /// fileKeyConfig.json의 키 설정에 따라 (Added/Deleted/Modified) 요약을 만든다.
        /// - 키 미설정: N/A 처리
        /// - 중복키: 비교 중단(상세 메시지 제공)
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
                var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    FileConstants.Extensions.Csv,
                    FileConstants.Extensions.Io
                };

                var leftFiles = Directory.EnumerateFiles(SelectedStandardFolder, "*.*", SearchOption.TopDirectoryOnly)
                                         .Where(p => allowed.Contains(Path.GetExtension(p)));
                var rightFiles = Directory.EnumerateFiles(SelectedTargetFolder, "*.*", SearchOption.TopDirectoryOnly)
                                          .Where(p => allowed.Contains(Path.GetExtension(p)));

                var leftMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var rightMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var p in leftFiles) leftMap[Path.GetFileName(p)] = p;
                foreach (var p in rightFiles) rightMap[Path.GetFileName(p)] = p;

                var allNames = new SortedSet<string>(leftMap.Keys, StringComparer.OrdinalIgnoreCase);
                foreach (var n in rightMap.Keys) allNames.Add(n);

                int total = allNames.Count;
                int done = 0;

                int no = GridItems.Count + 1;

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
                                item.Message = "취합 가능";
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
        /// 현재 선택된 비교(Target) 폴더를 확인 완료 처리한다.
        /// - 본인 사번으로 시작하는 폴더만 처리 가능
        /// - 폴더명 뒤에 "_OK"를 붙여 이름을 변경한다.
        /// </summary>
        public async Task ConfirmOk()
        {
            try
            {
                // 1) 기본 검증
                if (string.IsNullOrWhiteSpace(SelectedTargetFolder) ||
                    !Directory.Exists(SelectedTargetFolder))
                {
                    MessageBox.Show("확인 완료 처리할 폴더가 선택되지 않았습니다.",
                        "안내", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var currentFolderPath = SelectedTargetFolder;
                var folderName = Path.GetFileName(currentFolderPath);

                if (string.IsNullOrWhiteSpace(folderName))
                    return;

                // 2) 이미 _OK 인 경우
                if (folderName.EndsWith("_OK", StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show("이미 확인 완료된 폴더입니다.",
                        "안내", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // 3) 내 사번 폴더인지 확인
                var myEmpNo = Util.GfnGetUserName(); // 예: 12345678

                if (string.IsNullOrWhiteSpace(myEmpNo) ||
                    !folderName.StartsWith(myEmpNo, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show("본인 사번 폴더만 확인 완료 처리할 수 있습니다.",
                        "작업 불가", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 4) 변경될 폴더 경로
                var parentPath = Path.GetDirectoryName(currentFolderPath);
                if (string.IsNullOrWhiteSpace(parentPath))
                    return;

                var newFolderPath = Path.Combine(parentPath, folderName + "_OK");

                if (Directory.Exists(newFolderPath))
                {
                    MessageBox.Show("이미 동일한 이름의 _OK 폴더가 존재합니다.",
                        "작업 불가", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 5) 사용자 확인
                var confirm = MessageBox.Show(
                    $"선택한 폴더를 확인 완료 처리하시겠습니까?\n\n{folderName} → {folderName}_OK",
                    "확인 완료",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (confirm != MessageBoxResult.Yes)
                    return;

                // 6) 폴더명 변경 (실제 작업)
                await Task.Run(() =>
                {
                    Directory.Move(currentFolderPath, newFolderPath);
                });

                // 7) UI 갱신
                SelectedTargetFolder = newFolderPath;
                ReloadFolderLists(BaseFilePath);
                ApplyDefaultFolderSelection();

                MessageBox.Show("확인 완료 처리되었습니다.",
                    "완료", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"확인 완료 처리 중 오류가 발생했습니다.\n\n{ex.Message}",
                    "오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }



        // ===========================
        // 7) Command handlers
        // ===========================

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

            if (!string.IsNullOrEmpty(BaseFilePath) && Directory.Exists(BaseFilePath))
                dialog.InitialDirectory = BaseFilePath;

            if (dialog.ShowDialog() != true)
                return;

            var selectedPath = Path.GetDirectoryName(dialog.FileName);
            if (string.IsNullOrEmpty(selectedPath) || !Directory.Exists(selectedPath))
                return;

            BaseFilePath = selectedPath;

            // 폴더 리스트 생성/갱신은 분리 메서드로
            ReloadFolderLists(selectedPath);

            // 기본 선택: Standard는 첫 항목, Target은 내 사번 폴더 우선(없으면 첫 항목)
            ApplyDefaultFolderSelection();

        }

        private void Init()
        {
            // 사용자 확인
            if (MessageBox.Show("초기화를 진행하시겠습니까?", "초기화 확인",
                                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            BaseFilePath = string.Empty;

            // 리스트/선택도 같이 초기화
            StandardFolderList.Clear();
            TargetFolderList.Clear();

            SelectedStandardFolder = string.Empty;
            SelectedTargetFolder = string.Empty;

            GridItems.Clear();
        }

        /// <summary>
        /// 선택 파일의 사용자 변경분을 관리자 파일에 머지한다.
        /// - 중복키 파일은 적용 불가
        /// - 상품 단위 충돌(dupKeyColumns) 발생 시 중단
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

                // 적용 전 사용자 확인 메시지
                var confirm = MessageBox.Show(
                    "해당 변경 내용을 관리자 파일에 적용하시겠습니까?",
                    "적용 확인",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (confirm != MessageBoxResult.Yes)
                {
                    return; // 사용자가 취소함
                }

                var fileName = Path.GetFileName(standardFilePath);
                var adminFolderPath = Path.Combine(BaseFilePath, Util.GfnGetUserName());
                Directory.CreateDirectory(adminFolderPath);

                // 충돌 비교 후 머지
                var (hasChanges, added, deleted, modified)
                    = _applyService.MergeUserChangesToAdmin(standardFilePath, userFilePath, adminFolderPath);

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
        /// 그리드 전체 파일을 대상으로 일괄 머지한다.
        /// - 중간 실패 시 전체 롤백(백업본 복구)
        /// - 중복키 파일이 하나라도 있으면 시작 자체를 막는다
        /// </summary>
        private async Task ApplyAllFilesAsync()
        {
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

            // UI 상태
            IsBusy = true;
            IsProgressVisible = true;
            ProgressValue = 0;
            ProgressMessage = "전체 적용 준비 중...";

            try
            {
                var result = await _applyService.ApplyMergeTransactionAsync(
                    standardFolderPath: SelectedStandardFolder,
                    userFolderPaths: new[] { SelectedTargetFolder },
                    adminFolderPath: adminFolderPath,
                    precheckDuplicateKeys: true,
                    progress: new VmProgress(this),
                    ct: CancellationToken.None
                );

                // UI 마무리
                IsProgressVisible = false;
                ProgressMessage = string.Empty;

                if (!string.IsNullOrEmpty(result.WarnMessage))
                {
                    MessageBox.Show(result.WarnMessage, "전체 적용 불가",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!string.IsNullOrEmpty(result.FailMessage))
                {
                    MessageBox.Show(
                        $"전체 적용 중 오류가 발생하여 모든 변경을 롤백했습니다.\n\n{result.FailMessage}",
                        "전체 적용 실패",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }

                if (result.Success)
                {
                    MessageBox.Show("전체 적용이 완료되었습니다.", "전체 적용",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            finally
            {
                IsBusy = false;
                IsProgressVisible = false;
                ProgressMessage = string.Empty;
            }
        }

        private async Task ApplyAllAsync()
        {
            if (string.IsNullOrWhiteSpace(BaseFilePath) || !Directory.Exists(BaseFilePath))
            {
                MessageBox.Show("기준 경로가 설정되어 있지 않습니다.", "오류",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var okFolders = Directory.GetDirectories(BaseFilePath)
                .Where(p => IsOkFolderName(Path.GetFileName(p)))
                .ToList();

            if (okFolders.Count == 0)
            {
                MessageBox.Show("_OK 폴더가 없습니다.", "안내",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirm = MessageBox.Show(
                $"_OK 폴더의 변경 사항을 관리자 파일에 적용하시겠습니까?\n\n대상폴더 총 {okFolders.Count}개",
                "전체(OK) 적용 확인",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes)
                return;

            var adminFolderPath = Path.Combine(BaseFilePath, Util.GfnGetUserName());

            // UI 상태
            IsBusy = true;
            IsProgressVisible = true;
            ProgressValue = 0;
            ProgressMessage = "전체 적용 준비 중...";

            try
            {
                var result = await _applyService.ApplyMergeTransactionAsync(
                    standardFolderPath: SelectedStandardFolder,
                    userFolderPaths: okFolders,
                    adminFolderPath: adminFolderPath,
                    precheckDuplicateKeys: true,
                    progress: new VmProgress(this),
                    ct: CancellationToken.None
                );

                IsProgressVisible = false;
                ProgressMessage = string.Empty;

                if (!string.IsNullOrEmpty(result.WarnMessage))
                {
                    MessageBox.Show(result.WarnMessage, "전체 적용 불가",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!string.IsNullOrEmpty(result.FailMessage))
                {
                    MessageBox.Show(
                        $"전체 적용 중 오류가 발생하여 모든 변경을 롤백했습니다.\n\n{result.FailMessage}",
                        "전체 적용 실패",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }

                if (result.Success)
                {
                    MessageBox.Show("전체 적용이 완료되었습니다.", "전체 적용",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            finally
            {
                IsBusy = false;
                IsProgressVisible = false;
                ProgressMessage = string.Empty;
            }
        }

        // ===========================
        // 8) Helpers
        // ===========================
        /// <summary>
        /// 기준 경로(BaseFilePath) 하위의 폴더를 스캔하여
        /// - 기준(Standard) 폴더 목록
        /// - 비교(Target) 폴더 목록을 생성/갱신한다.
        /// 
        /// Target 폴더 규칙:
        /// - 관리자(IsAdmin=true): 8자리 사번으로 시작하는 폴더 모두 노출
        /// - 비관리자(IsAdmin=false): 내 사번으로 시작하는 폴더만 노출
        /// </summary>
        private void ReloadFolderLists(string selectedPath)
        {
            StandardFolderList.Clear();
            TargetFolderList.Clear();

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

            var baseFolderSet = new HashSet<string>(
                FileKeyManager.GetBaseFolders(),
                StringComparer.OrdinalIgnoreCase
            );

            // 비관리자일 때는 내 사번으로 시작하는 폴더만 Target 후보
            var myEmpNo = Util.GfnGetUserName(); // 예: "12345678"

            foreach (var folder in subFolders)
            {
                var folderName = Path.GetFileName(folder);

                if (string.IsNullOrWhiteSpace(folderName))
                    continue;

                // Standard 후보: baseFolderSet 포함
                if (baseFolderSet.Contains(folderName))
                {
                    if (!StandardFolderList.Any(x => x.Code.Equals(folder, StringComparison.OrdinalIgnoreCase)))
                    {
                        StandardFolderList.Add(new OptionItem(Code: folder, Name: folderName));
                    }
                    continue;
                }

                // Target 후보: baseFolderSet 미포함 + 8자리 사번 시작
                bool is8DigitNumberFolder =
                    folderName.Length >= 8 &&
                    folderName.Substring(0, 8).All(char.IsDigit);

                if (!is8DigitNumberFolder)
                    continue;

                // 비관리자면 "내 사번으로 시작"하는 폴더만 허용
                if (!IsAdmin)
                {
                    if (string.IsNullOrWhiteSpace(myEmpNo) ||
                        !folderName.StartsWith(myEmpNo, StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                if (!TargetFolderList.Any(x => x.Code.Equals(folder, StringComparison.OrdinalIgnoreCase)))
                {
                    TargetFolderList.Add(new OptionItem(Code: folder, Name: folderName));
                }
            }
        }

        /// <summary>
        /// 기준/비교 폴더의 기본 선택값을 적용한다.
        /// </summary>
        private void ApplyDefaultFolderSelection()
        {
            SelectedStandardFolder = GetDefaultStandardFolderPath();
            SelectedTargetFolder = GetDefaultTargetFolderPath();
        }

        /// <summary>
        /// 기준(Standard) 폴더 기본 선택 경로를 반환한다.
        /// - 목록의 첫 번째 항목을 기본값으로 사용
        /// </summary>
        private string GetDefaultStandardFolderPath()
            => StandardFolderList.FirstOrDefault().Code ?? string.Empty;

        /// <summary>
        /// 비교(Target) 폴더 기본 선택 경로를 반환한다.
        /// - 내 사번으로 시작하는 폴더를 우선 선택
        /// - 없으면 목록의 첫 번째 항목을 선택
        /// </summary>
        private string GetDefaultTargetFolderPath()
        {
            if (TargetFolderList.Count == 0)
                return string.Empty;

            var myEmpNo = Util.GfnGetUserName();

            // 1) 내 사번으로 시작하는 폴더 우선 (12345678 또는 12345678_OK 등)
            var mine = TargetFolderList.FirstOrDefault(x =>
                !string.IsNullOrWhiteSpace(x.Name) &&
                x.Name.StartsWith(myEmpNo, StringComparison.OrdinalIgnoreCase));

            if (mine != null && !string.IsNullOrWhiteSpace(mine.Code))
                return mine.Code;

            // 2) 없으면 첫 번째
            return TargetFolderList.FirstOrDefault().Code ?? string.Empty;
        }

        private static bool IsOkFolderName(string? folderName)
        {
            if (string.IsNullOrWhiteSpace(folderName))
                return false;

            if (!folderName.EndsWith("_OK", StringComparison.OrdinalIgnoreCase))
                return false;

            if (folderName.Length < 11) // "12345678_OK"
                return false;

            return folderName.Substring(0, 8).All(char.IsDigit);
        }

    }

}
