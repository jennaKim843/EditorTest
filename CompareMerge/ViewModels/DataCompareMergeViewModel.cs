// Modules/CompareMerge/ViewModels/DataCompareMergeViewModel.cs
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Media3D;
using InnoPVManagementSystem.Common.Foundation;
using InnoPVManagementSystem.Common.Services;
using InnoPVManagementSystem.Modules.CompareMerge.Views;
using Microsoft.Win32;

namespace InnoPVManagementSystem.Modules.CompareMerge.ViewModels
{
    /// <summary>
    /// Compare/Merge 화면용 ViewModel
    /// - 비교(대용량 자동 전환), 적용(머지/내보내기 훅), 파일/폴더 선택 커맨드 포함
    /// - IsComparing으로 진행 오버레이/버튼활성 제어
    /// </summary>
    public class DataCompareMergeViewModel : INotifyPropertyChanged
    {
        // ====== 임계값 (필요시 설정으로 분리) ======
        private const long BigFileSizeBytes = 200L * 1024 * 1024; // 200MB
        private const long BigLineCountThreshold = 5_000_000;          // 500만 라인

        private CancellationTokenSource? _cts;

        public DataCompareMergeViewModel()
        {
            // 초기화
            InitCommand = new RelayCommand(_ => Init(), _ => !IsComparing);

            // 파일 비교/적용
            CompareFolderCommand = new RelayCommand(async _ => await CompareFolderAsync(), _ => !IsComparing && CanCompare);
            ApplyCommand = new RelayCommand(async _ => await ApplyAsync(), _ => !IsComparing && CanApply);
            ApplyAllCommand = new RelayCommand(async _ => await ApplyAsync(), _ => !IsComparing && CanApply); //TODO 전체적용

            // 파일/폴더 선택
            SelectFolderCommand = new RelayCommand(_ => SelectFolder(), _ => !IsComparing);
            SelectLeftFileCommand = new RelayCommand(_ => SelectLeftFile(), _ => !IsComparing);
            SelectRightFileCommand = new RelayCommand(_ => SelectRightFile(), _ => !IsComparing);

            // 취소
            CancelCommand = new RelayCommand(_ => _cts?.Cancel(), _ => IsComparing);

            LeftFolderList = new ObservableCollection<OptionItem>();
            RightFolderList = new ObservableCollection<OptionItem>();
        }

        // ====== 바인딩 속성 ======
        private string? _leftFolderPath;
        public string? LeftFolderPath
        {
            get => _leftFolderPath;
            set { _leftFolderPath = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanCompare)); Invalidate(); }
        }

        private string? _rightFolderPath;
        public string? RightFolderPath
        {
            get => _rightFolderPath;
            set { _rightFolderPath = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanCompare)); Invalidate(); }
        }

        private string? _baseFolderPath;
        public string? BaseFolderPath
        {
            get => _baseFolderPath;
            set { _baseFolderPath = value; OnPropertyChanged(); RefreshFolderList(); }
        }

        private bool _isComparing;
        public bool IsComparing
        {
            get => _isComparing;
            set { _isComparing = value; OnPropertyChanged(); Invalidate(); }
        }

        private string _status = "";
        public string Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(); }
        }

        // 그리드 행 모델
        public class GridItem
        {
            //public int No { get; set; }
            //public string FileName { get; set; } = "";
            //public long DeletedCount { get; set; }
            //public long AddedCount { get; set; }
            //public long ModifiedCount { get; set; }

            public int No { get; set; }
            public string LeftDisplay { get; set; } = "";   // 기준폴더: 존재하면 파일명, 없으면 "미존재"
            public string RightDisplay { get; set; } = "";  // 대상폴더: 존재하면 파일명, 없으면 "미존재"

            public string? LeftFullPath { get; set; }
            public string? RightFullPath { get; set; }

            public string State { get; set; } = "대기";     // 초기값: "대기"

            // 존재 여부 (↔ "미존재" 문자열 의존 X)
            public bool LeftExists { get; set; }
            public bool RightExists { get; set; }

            // 버튼 표시 조건
            public bool CanAction => LeftExists && RightExists;
        }

        private ObservableCollection<GridItem> _gridItems = new();
        public ObservableCollection<GridItem> GridItems
        {
            get => _gridItems;
            set { _gridItems = value; OnPropertyChanged(); }
        }

        private GridItem? _selectedGridItem;
        public GridItem? SelectedGridItem
        {
            get => _selectedGridItem;
            set { _selectedGridItem = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanApply)); }
        }


        // 비교 결과 요약
        private long _commonCount;
        public long CommonCount { get => _commonCount; set { _commonCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanApply)); Invalidate(); } }

        private long _onlyLeftCount;
        public long OnlyLeftCount { get => _onlyLeftCount; set { _onlyLeftCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanApply)); Invalidate(); } }

        private long _onlyRightCount;
        public long OnlyRightCount { get => _onlyRightCount; set { _onlyRightCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanApply)); Invalidate(); } }

        // 샘플 미리보기 경로(대용량 비교 시 제공)
        private string? _leftOnlySamplePath;
        public string? LeftOnlySamplePath { get => _leftOnlySamplePath; set { _leftOnlySamplePath = value; OnPropertyChanged(); } }

        private string? _rightOnlySamplePath;
        public string? RightOnlySamplePath { get => _rightOnlySamplePath; set { _rightOnlySamplePath = value; OnPropertyChanged(); } }

        private string? _commonSamplePath;
        public string? CommonSamplePath { get => _commonSamplePath; set { _commonSamplePath = value; OnPropertyChanged(); } }

        // 폴더 내부 하위 폴더 리스트(옵션)
        public ObservableCollection<OptionItem> LeftFolderList { get; } // 기준폴더
        public ObservableCollection<OptionItem> RightFolderList { get; }   // 비교폴더

        // ====== 작업 전 검증 ======
        public bool CanCompare => Directory.Exists(LeftFolderPath ?? "") && Directory.Exists(RightFolderPath ?? "");
        //public bool CanApply => (OnlyLeftCount > 0 || OnlyRightCount > 0 || CommonCount > 0);
        public bool CanApply => SelectedGridItem != null && SelectedGridItem.LeftDisplay != "미존재" && SelectedGridItem.RightDisplay != "미존재";


        // ====== 커맨드 ======
        public ICommand InitCommand { get; }
        public ICommand CompareFolderCommand { get; }
        public ICommand ApplyCommand { get; }
        public ICommand ApplyAllCommand { get; }
        public ICommand SelectLeftFileCommand { get; }
        public ICommand SelectRightFileCommand { get; }
        public ICommand SelectFolderCommand { get; }
        public ICommand CancelCommand { get; }

        private void Invalidate()
        {
            CommandManager.InvalidateRequerySuggested();
        }

        // ====== 초기화 ======
        private void Init()
        {
            // 사용자 확인
            if (MessageBox.Show("초기화를 진행하시겠습니까?", "초기화 확인",
                                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            // 모든 조건 초기화
            BaseFolderPath = LeftFolderPath = RightFolderPath = null;
            GridItems.Clear();

        }

        // ====== 비교 실행 ======
        public async Task CompareFolderAsync()
        {
            if (!CanCompare)
            {
                Status = "좌/우 폴더 경로를 확인하세요.";
                return;
            }

            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            try
            {
                IsComparing = true;
                Status = "폴더 스캔중...";

                var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".csv", ".io" };

                // 좌/우 폴더 1뎁스 파일 스캔
                var leftFiles = Directory.EnumerateFiles(LeftFolderPath!, "*.*", SearchOption.TopDirectoryOnly)
                                         .Where(p => allowed.Contains(Path.GetExtension(p)));
                var rightFiles = Directory.EnumerateFiles(RightFolderPath!, "*.*", SearchOption.TopDirectoryOnly)
                                          .Where(p => allowed.Contains(Path.GetExtension(p)));

                // 파일명 기준 맵
                var leftMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var rightMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var p in leftFiles) leftMap[Path.GetFileName(p)] = p;
                foreach (var p in rightFiles) rightMap[Path.GetFileName(p)] = p;

                // 합집합 파일명
                var allNames = new List<string>(leftMap.Keys);
                foreach (var n in rightMap.Keys) if (!leftMap.ContainsKey(n)) allNames.Add(n);

                allNames.Sort(StringComparer.OrdinalIgnoreCase);

                // 그리드 채우기
                GridItems.Clear();
                int no = 1;
                foreach (var name in allNames)
                {
                    ct.ThrowIfCancellationRequested();

                    //bool inLeft = leftMap.ContainsKey(name);
                    //bool inRight = rightMap.ContainsKey(name);
                    bool inLeft = leftMap.TryGetValue(name, out var leftPath);
                    bool inRight = rightMap.TryGetValue(name, out var rightPath);

                    GridItems.Add(new GridItem
                    {
                        No = no++,
                        LeftDisplay = inLeft ? name : "미존재",
                        RightDisplay = inRight ? name : "미존재",

                        LeftExists = inLeft,
                        RightExists = inRight,

                        LeftFullPath = leftPath,
                        RightFullPath = rightPath,
                        State = "대기"
                    });
                }

                // 요약(원하면 파일 개수 기준으로 간단 표기)
                CommonCount = 0; // 이번 단계에선 사용 안 함
                OnlyLeftCount = 0;
                OnlyRightCount = 0;

                Status = $"완료: 총 {GridItems.Count:N0}개 파일명 비교(내용 비교 제외, 상태=대기).";
            }
            catch (OperationCanceledException)
            {
                Status = "취소됨.";
            }
            catch (Exception ex)
            {
                Status = "오류: " + ex.Message;
            }
            finally
            {
                IsComparing = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }

        //public async Task CompareFolderAsync()
        //{
        //    if (!CanCompare)
        //    {
        //        Status = "좌/우 파일 경로를 확인하세요.";
        //        return;
        //    }

        //    _cts?.Cancel();
        //    _cts = new CancellationTokenSource();
        //    var ct = _cts.Token;

        //    try
        //    {
        //        IsComparing = true;
        //        Status = "비교 준비중...";

        //        var lfi = new FileInfo(LeftFolderPath!);
        //        var rfi = new FileInfo(RightFolderPath!);

        //        var bigBySize = (lfi.Length >= BigFileSizeBytes) || (rfi.Length >= BigFileSizeBytes);

        //        long? leftLines = null, rightLines = null;
        //        if (!bigBySize)
        //        {
        //            Status = "라인 수 계산중(스트리밍)...";
        //            var t1 = DiffKit.CountLinesAsync(LeftFolderPath!, ct);
        //            var t2 = DiffKit.CountLinesAsync(RightFolderPath!, ct);
        //            await Task.WhenAll(t1, t2);
        //            leftLines = t1.Result;
        //            rightLines = t2.Result;
        //        }

        //        var isBig = bigBySize
        //                    || (leftLines.HasValue && leftLines.Value >= BigLineCountThreshold)
        //                    || (rightLines.HasValue && rightLines.Value >= BigLineCountThreshold);

        //        if (isBig)
        //        {
        //            Status = "대용량 모드(외부 정렬)로 비교중...";
        //            var res = await DiffKit.CompareFilesExternalAsync(LeftFolderPath!, RightFolderPath!, ct: ct);

        //            UpdateSummary(res.CommonCount, res.OnlyLeftCount, res.OnlyRightCount,
        //                          res.LeftOnlySamplePath, res.RightOnlySamplePath, res.CommonSamplePath);

        //            Status = $"완료: 공통 {CommonCount:N0}, 좌만 {OnlyLeftCount:N0}, 우만 {OnlyRightCount:N0}";
        //        }
        //        else
        //        {
        //            Status = "경량 모드(인메모리)로 비교중...";
        //            var res = await DiffKit.CompareFilesInMemoryAsync(LeftFolderPath!, RightFolderPath!, ct);

        //            UpdateSummary(res.CommonCount, res.OnlyLeftCount, res.OnlyRightCount,
        //                          null, null, null);

        //            Status = $"완료: 공통 {CommonCount:N0}, 좌만 {OnlyLeftCount:N0}, 우만 {OnlyRightCount:N0}";
        //        }
        //    }
        //    catch (OperationCanceledException)
        //    {
        //        Status = "취소됨.";
        //    }
        //    catch (Exception ex)
        //    {
        //        Status = "오류: " + ex.Message;
        //    }
        //    finally
        //    {
        //        IsComparing = false;
        //    }
        //}

        // ===== 행 버튼용 =======
        public ICommand RowActionCommand => new RelayCommand(
            async p =>
            {
                if (p is not GridItem row) return;

                var leftFilePath = row.LeftFullPath;
                var rightFilePath = row.RightFullPath;

                if (!row.CanAction ||
                    string.IsNullOrWhiteSpace(leftFilePath) || string.IsNullOrWhiteSpace(rightFilePath) ||
                    !File.Exists(leftFilePath) || !File.Exists(rightFilePath))
                {
                    Status = "선택한 행의 기준폴더/대상폴더 파일이 모두 존재해야 합니다.";
                    return;
                }

                //    // 파일내용비교
                // 읽기 전용 Diff 창 띄우기
                var win = new ReadOnlyDiffWindow(leftFilePath, rightFilePath);
                win.Owner = Application.Current?.MainWindow;
                win.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                win.ShowDialog();

                //    //row.State = "비교중";
                //}
            },
            p => p is GridItem r && r.CanAction && !IsComparing
        );


        private void UpdateSummary(
            long common, long onlyLeft, long onlyRight,
            string? leftOnlySamplePath, string? rightOnlySamplePath, string? commonSamplePath)
        {
            CommonCount = common;
            OnlyLeftCount = onlyLeft;
            OnlyRightCount = onlyRight;
            LeftOnlySamplePath = leftOnlySamplePath;
            RightOnlySamplePath = rightOnlySamplePath;
            CommonSamplePath = commonSamplePath;
        }
        //private void UpdateSummary(
        //    long common, long onlyLeft, long onlyRight,
        //    string? leftOnlySamplePath, string? rightOnlySamplePath, string? commonSamplePath)
        //{
        //    CommonCount = common;
        //    OnlyLeftCount = onlyLeft;
        //    OnlyRightCount = onlyRight;
        //    LeftOnlySamplePath = leftOnlySamplePath;
        //    RightOnlySamplePath = rightOnlySamplePath;
        //    CommonSamplePath = commonSamplePath;

        //    // ▼ 요약 행 그리드 채우기
        //    GridItems.Clear();

        //    var fileName = $"{Path.GetFileName(LeftFolderPath)}  vs  {Path.GetFileName(RightFolderPath)}";

        //    // '수정(건)' 정의가 없다면, 우선 공통에서의 변경으로 0 처리 혹은 추후 로직 대체
        //    GridItems.Add(new GridItem
        //    {
        //        No = 1,
        //        FileName = fileName,
        //        DeletedCount = OnlyLeftCount,
        //        AddedCount = OnlyRightCount,
        //        ModifiedCount = 0 // TODO: 라인 diff 로직 연계 시 계산
        //    });
        //}

        // ====== 적용(머지/반영) ======
        private async Task ApplyAsync()
        {
            if (!CanApply)
            {
                Status = "적용할 변경이 없습니다.";
                return;
            }

            // TODO: 여기서 실제 반영 규칙을 연결하세요.
            // - OnlyLeft / OnlyRight / Common 샘플 파일을 열어 사용자 확인 후 반영
            // - 혹은 자동 규칙으로 결과 파일 생성
            // 샘플: 좌/우 중 우선순위 우측을 기준으로 결과 파일 생성(데모)
            try
            {
                IsComparing = true;
                Status = "변경 적용중...";

                // 간단 예시: 우측 파일을 결과로 내보내기
                var dst = MakeExportPath(RightFolderPath!, suffix: "_APPLIED");
                await File.WriteAllBytesAsync(dst, await File.ReadAllBytesAsync(RightFolderPath!));
                Status = $"적용 완료: {dst}";
            }
            catch (Exception ex)
            {
                Status = "적용 실패: " + ex.Message;
            }
            finally
            {
                IsComparing = false;
            }

            static string MakeExportPath(string src, string suffix)
            {
                var dir = Path.GetDirectoryName(src)!;
                var name = Path.GetFileNameWithoutExtension(src);
                var ext = Path.GetExtension(src);
                return Path.Combine(dir, $"{name}{suffix}{ext}");
            }
        }

        // ====== 파일/폴더 선택 ======
        private void SelectLeftFile()
        {
            var dlg = new OpenFileDialog
            {
                Title = "좌측 파일 선택",
                Filter = "모든 파일|*.*",
                CheckFileExists = true
            };
            if (!string.IsNullOrEmpty(LeftFolderPath) && File.Exists(LeftFolderPath))
                dlg.InitialDirectory = Path.GetDirectoryName(LeftFolderPath);

            if (dlg.ShowDialog() == true)
            {
                LeftFolderPath = dlg.FileName;
            }
        }

        private void SelectRightFile()
        {
            var dlg = new OpenFileDialog
            {
                Title = "우측 파일 선택",
                Filter = "모든 파일|*.*",
                CheckFileExists = true
            };
            if (!string.IsNullOrEmpty(RightFolderPath) && File.Exists(RightFolderPath))
                dlg.InitialDirectory = Path.GetDirectoryName(RightFolderPath);

            if (dlg.ShowDialog() == true)
            {
                RightFolderPath = dlg.FileName;
            }
        }

        /// <summary>
        /// 폴더 선택: OpenFileDialog 트릭(아무 파일 선택 시 폴더만 추출)
        /// </summary>
        private void SelectFolder()
        {
            var dialog = new OpenFileDialog
            {
                Title = "폴더 선택 (아무 파일이나 선택하면 해당 폴더가 선택됩니다)",
                CheckFileExists = false,
                CheckPathExists = true,
                ValidateNames = false,
                FileName = "Folder Selection.",
                DefaultExt = "folder",
                Filter = "폴더 선택|*.folder"
            };

            if (!string.IsNullOrEmpty(BaseFolderPath) && Directory.Exists(BaseFolderPath))
                dialog.InitialDirectory = BaseFolderPath;

            if (dialog.ShowDialog() == true)
            {
                var selectedPath = Path.GetDirectoryName(dialog.FileName);
                if (!string.IsNullOrEmpty(selectedPath))
                    BaseFolderPath = selectedPath;
            }
        }

        private void RefreshFolderList()
        {
            LeftFolderList.Clear();
            RightFolderList.Clear();

            if (string.IsNullOrWhiteSpace(BaseFolderPath) || !Directory.Exists(BaseFolderPath))
                return;

            try
            {
                var dirs = Directory.GetDirectories(BaseFolderPath);

                foreach (var dir in dirs)
                {
                    string folderName = Path.GetFileName(dir); // 폴더명만 추출

                    var item = new OptionItem(
                        Code: dir,
                        Name: folderName
                    );

                    LeftFolderList.Add(item);
                    RightFolderList.Add(item);
                }

            }
            catch (Exception ex)
            {
                // 권한/경합 등 예외는 상태로만 표시
                Status = "하위 폴더 조회 실패: " + ex.Message;
            }
        }

        // ====== INotifyPropertyChanged ======
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// WPF RequerySuggested 연동형 RelayCommand (CanExecute 자동 갱신)
    /// </summary>
    public sealed class RelayCommand : ICommand
    {
        private readonly Func<object?, Task>? _asyncExec;
        private readonly Action<object?>? _exec;
        private readonly Func<object?, bool>? _can;

        public RelayCommand(Func<object?, Task> exec, Func<object?, bool>? can = null)
        { _asyncExec = exec; _can = can; }

        public RelayCommand(Action<object?> exec, Func<object?, bool>? can = null)
        { _exec = exec; _can = can; }

        public bool CanExecute(object? parameter) => _can?.Invoke(parameter) ?? true;

        public async void Execute(object? parameter)
        {
            if (_asyncExec is not null) { await _asyncExec(parameter); return; }
            _exec?.Invoke(parameter);
        }

        public event EventHandler? CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }
    }
}
