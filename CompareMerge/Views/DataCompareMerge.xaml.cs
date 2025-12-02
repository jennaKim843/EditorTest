using InnoPVManagementSystem.Modules.CompareMerge.ViewModels;
using System.Windows.Controls;
using System.Windows.Input;

namespace InnoPVManagementSystem.Modules.CompareMerge.Views
{
    /// <summary>
    /// 보너스 관리 (BonusManagement)
    /// </summary>
    public partial class DataCompareMerge : UserControl
    {
        /// <summary>
        /// 현재 DataContext를 ViewModel 타입으로 캐스팅한 단축 프로퍼티
        /// - null 가능성 있으므로 사용 시 null 체크 필요
        /// </summary>
        //private DataCompareMergeViewModel? Vm => DataContext as DataCompareMergeViewModel;

        public DataCompareMerge()
        {
            InitializeComponent();

            //// 그리드 복사시 제외컬럼 지정
            //GridCopyHelper.AttachCopyHandler(grdLoyaltyMaintBonusSet, new[] { ColumnConstants.No });

            //// - DataContext 변경: ViewModel 교체 시 이벤트 재구독 필요
            //// - Loaded/Unloaded: 화면 진입/이탈 시 구독/해제(메모리 누수 방지)
            //this.DataContextChanged += OnDataContextChanged;
            //this.Loaded += OnLoaded;
            //this.Unloaded += OnUnloaded;
        }

        private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is not DataGridRow row) return;

            var item = row.DataContext; // 현재 행의 바운드 객체
            if (item == null) return;

            if (DataContext is DataCompareMergeViewModel vm)
            {
                var cmd = vm.RowActionCommand;
                if (cmd != null && cmd.CanExecute(item))
                    cmd.Execute(item);
            }
        }

        ///// <summary>
        ///// 현재 DataContext를 ViewModel 타입으로 캐스팅한 단축 프로퍼티
        ///// - null 가능성 있으므로 사용 시 null 체크 필요
        ///// </summary>
        //private DataCompareMergeViewModel? Vm => DataContext as DataCompareMergeViewModel;

        ///// <summary>
        ///// 화면 로드시 호출
        ///// - ViewModel의 PropertyChanged 구독(테이블 교체 감지)
        ///// - 최초 테이블에 대해 컬럼 바인딩 시도
        ///// </summary>
        //private void OnLoaded(object? s, RoutedEventArgs e)
        //{
        //    if (Vm != null) Vm.PropertyChanged += OnVmPropertyChanged;
        //    TryBindColumns(Vm?.Table);
        //}

        ///// <summary>
        ///// 화면 언로드 시 호출
        ///// - 이벤트 구독 해제(메모리 누수 방지)
        ///// - 주로 내비게이션 이동/탭 닫기 등에서 호출됨
        ///// </summary>
        //private void OnUnloaded(object? s, RoutedEventArgs e)
        //{
        //    if (Vm != null) Vm.PropertyChanged -= OnVmPropertyChanged;
        //}

        ///// <summary>
        ///// DataContext가 교체될 때 호출
        ///// - 이전 ViewModel의 이벤트 구독 해제
        ///// - 새로운 ViewModel에 이벤트 재구독
        ///// - 새 ViewModel의 Table 기준으로 컬럼 바인딩 시도
        ///// </summary>
        //private void OnDataContextChanged(object? s, DependencyPropertyChangedEventArgs e)
        //{
        //    if (e.OldValue is DataCompareMergeViewModel oldVm)
        //        oldVm.PropertyChanged -= OnVmPropertyChanged;

        //    if (e.NewValue is DataCompareMergeViewModel newVm)
        //    {
        //        newVm.PropertyChanged += OnVmPropertyChanged;
        //        TryBindColumns(newVm.Table);
        //    }
        //}

        ///// <summary>
        ///// ViewModel의 PropertyChanged 이벤트 핸들러
        ///// - Table 속성이 변경되면 그리드 컬럼을 재바인딩 (스키마 변화 반영)
        ///// - 대용량 테이블 교체 시에도 컬럼이 최신 구조로 갱신됨
        ///// </summary>
        //private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        //{
        //    if (e.PropertyName == nameof(DataCompareMergeViewModel.Table))
        //    {
        //        TryBindColumns(Vm?.Table);
        //    }
        //}

        ///// <summary>
        ///// DataTable → DataGrid 컬럼 바인딩 유틸
        ///// - 기존 컬럼 제거 후 DataGridBinder를 사용해 DataTable 스키마로 컬럼 자동 구성
        ///// - GridBindOptions로 행번호/체크박스/최대 너비/숨김/필수 컬럼 등 옵션 제어
        ///// - View 레이어에서만 가능한 UI 구성을 담당 (MVVM 위배 아님)
        ///// </summary>
        ///// <param name="table">그리드에 표시할 DataTable (null이면 동작하지 않음)</param>
        //private void TryBindColumns(DataTable? table)
        //{
        //    if (table == null) return;

        //    // 기존 컬럼 초기화(스키마 변경 반영 위해 매번 클리어)
        //    grdLoyaltyMaintBonusSet.Columns.Clear();

        //    // 공용 바인더를 통해 DataGrid 컬럼/스타일 일괄 구성
        //    DataGridBinder.BindToDataGrid(table, grdLoyaltyMaintBonusSet);
        //}

    }
}
