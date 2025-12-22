using InnoPVManagementSystem.Modules.CompareMerge.Models;
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
        }

        private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is not DataGridRow row) return;

            if (row.DataContext is not DiffGridItem item) return;

            if (DataContext is DataCompareMergeViewModel vm)
            {
                var cmd = vm.RowActionCommand;
                if (cmd.CanExecute(item))
                    cmd.Execute(item);
            }
        }


    }
}
