// ReadOnlyDiffWindow.xaml.cs
using System.IO;
using System.Windows;
using InnoPVManagementSystem.Modules.CompareMerge.Views;

namespace InnoPVManagementSystem.Modules.CompareMerge.Views
{
    public partial class ReadOnlyDiffWindow : Window
    {
        public ReadOnlyDiffWindow(string leftPath, string rightPath)
        {
            InitializeComponent();

            // TODO io파일은 csv변환후 읽기 가능
            //DiffView.LeftEditor.Text = File.ReadAllText(leftPath);
            //DiffView.RightEditor.Text = File.ReadAllText(rightPath);
            //DiffView.CompareNow();

            //Title = $"파일 비교 (읽기 전용)  —  L: {System.IO.Path.GetFileName(leftPath)}  |  R: {System.IO.Path.GetFileName(rightPath)}";

            // 에디터에 직접 텍스트 세팅하지 말고, LoadFiles가 전부 처리
            DiffView.LoadFiles(leftPath, rightPath);
        }
    }
}