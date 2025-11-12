using System.Collections.ObjectModel;
using System.Windows.Controls;
using System.Windows.Input;
using InnoPVManagementSystem.Common.Foundation;
using InnoPVManagementSystem.Common.Messaging;
using InnoPVManagementSystem.Common.Models;
using InnoPVManagementSystem.Modules.ProductManagement.Views;
using InnoPVManagementSystem.Modules.RenewProductManagement.Views;

using InnoPVManagementSystem.Modules.InnoSupportTool.View;
using InnoPVManagementSystem.Modules.RunSetting.View;
using InnoPVManagementSystem.Common.ViewModels.Base;
using InnoPVManagementSystem.Modules.DocumentOutput.View;
using InnoPVManagementSystem.Modules.CheckRegulation.View;
using InnoPVManagementSystem.Modules.OutPutResult.Views;
using InnoPVManagementSystem.Modules.BusinessPlanCreator.View;
using InnoPVManagementSystem.Modules.CompareMerge.Views;

namespace InnoPVManagementSystem.Common.ViewModels
{
    /// <summary>
    /// 왼쪽 메뉴(및 탭에 표시될 데이터) 관리 ViewModel
    /// 탭이 추가될 때 AppMenuItem의 ViewType에 UserControl 타입을 연결
    /// </summary>
    public class MenuViewModel
    {
        public ObservableCollection<AppMenuItem> MenuTreeItems { get; } = new();
        public ICommand MenuClickCommand { get; }

        public MenuViewModel()
        {
            // 최상위 메뉴 목록 초기화
            MenuTreeItems.Clear();

            // 1. Runsetting 그룹
            var runSetMenu = new AppMenuItem
            {
                Header = "1. Runsetting",
                Children = new ObservableCollection<AppMenuItem>
                {
                    new AppMenuItem { Key = "RunsetMenu1", Header = "Runsetting", ViewType = typeof(RunSetting) },
                }
            };

            // 2. 상품정보관리 그룹
            var pruductMenu = new AppMenuItem
            {
                Header = "2. 상품정보관리",
                Children = new ObservableCollection<AppMenuItem>
                {
                    new AppMenuItem { Key = "pruductMenu01", Header = "상품관리 현황", ViewType = typeof(ProductManagementStatus) },
                    new AppMenuItem { Key = "pruductMenu02", Header = "My 상품관리", ViewType = typeof(MyProductManagement) },
                    new AppMenuItem { Key = "pruductMenu03", Header = "금리확정형(K100)", ViewType = typeof(FixedRateProduct) },
                    new AppMenuItem { Key = "pruductMenu04", Header = "금리연동형(K200)", ViewType = typeof(FloatingInterestRateProduct) },
                    new AppMenuItem { Key = "pruductMenu05", Header = "변액상품(K300)", ViewType = typeof(VariableInsuranceProduct) },
                    new AppMenuItem { Key = "pruductMenu06", Header = "위험률 관리(RiskRate)", ViewType = typeof(RiskRateManagement) },
                    // 위험률관리화면으로 통일
                    //new AppMenuItem { Key = "pruductMenu07_back", Header = "할증률 관리(RiskRateAdj)", ViewType = typeof(RiskRateAdjustment) },
                    new AppMenuItem { Key = "pruductMenu07", Header = "예정해지율 관리(ExpectedLapseSet)", ViewType = typeof(ExpectedRateManagement) },
                    new AppMenuItem { Key = "pruductMenu08", Header = "보너스 관리(LoyaltyMainBonusSet)", ViewType = typeof(BonusManagement) },
                    new AppMenuItem { Key = "pruductMenu09", Header = "보험요율 관리(PV_Mapping_Code)", ViewType = typeof(InsuranceRateCodeManagement) }
                }
            };

            // 3. 갱신상품관리
            var renewProductMenu = new AppMenuItem
            {
                Header = "3. 갱신상품관리",
                Children = new ObservableCollection<AppMenuItem>
                {
                    new AppMenuItem { Key = "RenewMenu01", Header = "갱신 전/후 위험률 관리", ViewType = typeof(RenewBnARiskRate) },
                    new AppMenuItem { Key = "RenewMenu02", Header = "갱신상품관리", ViewType = typeof(RenewProduct) }
                }
            };

            // 4. 예시표 산출
            var documentOutputMenu = new AppMenuItem
            {
                Header = "4. 예시표 산출",
                Children = new ObservableCollection<AppMenuItem>
                {
                    new AppMenuItem { Key = "DocumentOutputMenu1", Header = "예시표 템플릿 생성",     ViewType = typeof(SampleTemplate) },
                    new AppMenuItem { Key = "DocumentOutputMenu2", Header = "예시표 산출",            ViewType = typeof(SamplePrint) },
                    new AppMenuItem { Key = "DocumentOutputMenu3", Header = "확인의뢰서 템플릿 생성", ViewType = typeof(VerificationTemplate) },
                    new AppMenuItem { Key = "DocumentOutputMenu4", Header = "확인의뢰서 산출",        ViewType = typeof(VerificationPrint) },
                }
            };

            // 5. 규정적합성 체크
            var checkRegulationMenu = new AppMenuItem
            {
                Header = "5. 규정적합성 체크",
                Children = new ObservableCollection<AppMenuItem>
                {
                    new AppMenuItem { Key = "CheckRegulation1", Header = "규정 정보 관리", ViewType = typeof(RegulationManagement) },
                    new AppMenuItem { Key = "CheckRegulation2", Header = "기준연령 체크",        ViewType = typeof(AgeCheckManagement) },
                    new AppMenuItem { Key = "CheckRegulation2", Header = "보장성 체크",        ViewType = typeof(CoverageCheckManagement) },
                    // 저축성은 로직이 구축되면 연결 할 것
                     //new AppMenuItem { Key = "CheckRegulation2", Header = "저축성 체크",        ViewType = typeof(SavingsCheckManagement) },
                    new AppMenuItem { Key = "CheckRegulation2", Header = "기타 체크",        ViewType = typeof(MiscCheckManagement) },
                    new AppMenuItem { Key = "CheckRegulation2", Header = "사업방법서 생성",        ViewType = typeof(BusinessPlanCreator) },
                }
            };

            // 4. 산출결과 조회
            var outPutResultMenu = new AppMenuItem
            {
                Header = "6. 산출결과 조회",
                Children = new ObservableCollection<AppMenuItem>
                {
                    new AppMenuItem { Key = "OutPutMenu01", Header = "산출결과조회-보험료", ViewType = typeof(PreOutPut) },
                    new AppMenuItem { Key = "OutPutMenu02", Header = "산출결과조회-준비금", ViewType = typeof(RseOutPut) }
                }
            };

            // 7. 기타메뉴
            var etcMenu = new AppMenuItem
            {
                Header = "7. 기타메뉴",
                Children = new ObservableCollection<AppMenuItem>
                {
                    new AppMenuItem { Key = "Diff",Header = "InnoDiff", ViewType = typeof(InnoDiff) },
                    new AppMenuItem { Key = "DiffMulti",Header = "InnoDiffMulti", ViewType = typeof(InnoDiffMulti) },
                    new AppMenuItem { Key = "CSV",Header = "InnoCSV", ViewType = typeof(InnoCsv) },
                }
            };

            // 8. 데이터 취합
            var compareMergeMenu = new AppMenuItem
            {
                Header = "8. 데이터 취합",
                Children = new ObservableCollection<AppMenuItem>
                {
                    new AppMenuItem { Key = "CompareMergeMenu01",Header = "데이터(csv, io) 비교", ViewType = typeof(DataCompareMerge) }
                }
            };

            //Menus = new ObservableCollection<AppMenuItem>
            //{
            //    // 메뉴(탭) 추가는 여기서 한 줄씩만!
            //    new AppMenuItem { Key = "RunSetting",Header = "RunSetting", ViewType = typeof(RunSetting) },
            //    new AppMenuItem { Key = "Diff",Header = "InnoDiff", ViewType = typeof(InnoDiff) },
            //    new AppMenuItem { Key = "CSV",Header = "InnoCSV", ViewType = typeof(InnoCsv) },
            //};

            // 최상위 메뉴 트리에 추가
            MenuTreeItems.Add(runSetMenu);
            MenuTreeItems.Add(pruductMenu);
            MenuTreeItems.Add(renewProductMenu);
            MenuTreeItems.Add(documentOutputMenu);
            MenuTreeItems.Add(checkRegulationMenu);
            MenuTreeItems.Add(outPutResultMenu);
            MenuTreeItems.Add(etcMenu);
            MenuTreeItems.Add(compareMergeMenu);

            // 메뉴 클릭 명령 초기화
            MenuClickCommand = new RelayCommand<AppMenuItem>(ExecuteMenuClick);
        }

        private void ExecuteMenuClick(AppMenuItem menuItem)
        {
            if (menuItem == null) return;

            // 자식이 있는 상위 메뉴는 클릭해도 무시
            if (menuItem.Children != null && menuItem.Children.Count > 0)
                return;

            if (menuItem.ViewType == null)
                return;

            var view = (UserControl?)Activator.CreateInstance(menuItem.ViewType);
            Messenger.Send(new UiCommandMessage
            {
                Command = UiCommandType.OpenTab,
                Target = menuItem.Key,
                Payload = new OpenTabPayload
                {
                    Header = menuItem.Header,
                    Content = view
                }
            });
        }
    }
}
