using finalProject.Models;
using ModernWpf.Controls;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Window = System.Windows.Window;

namespace finalProject.Views
{
    /// <summary>
    /// WorkersInfo.xaml에 대한 상호 작용 논리
    /// </summary>
    public partial class WorkersInfo : Window
    {
        private DispatcherTimer sirenTimer;
        private bool sirenVisible = true;
        private FactoryIOControl factoryIOControl;
        private DispatcherTimer connectionCheckTimer;

        public WorkersInfo()
        {
            InitializeComponent();

            // CameraDetection 초기화 및 MainWindow 참조 설정
            SafetyCheck.WorkersWin = this;
            SafetyCheck.InitializeModel();

            // 사이렌 깜빡임 설정
            InitializeSirenBlink();

            // factoryIO 서버 시작
            InitializeFactoryIO();

            InitializeConnectionMonitor();

            // 창 닫기 이벤트 처리 추가
            this.Closing += WorkersInfo_Closing;
        }

        private void InitializeSirenBlink()
        {
            sirenTimer = new DispatcherTimer();
            sirenTimer.Interval = TimeSpan.FromMilliseconds(500); // 0.5초마다
            sirenTimer.Tick += SirenTimer_Tick;
            sirenTimer.Start();
        }

        private void SirenTimer_Tick(object sender, EventArgs e)
        {
            sirenVisible = !sirenVisible;
            sirenF.Opacity = sirenVisible ? 1.0 : 0.2;
            sirenB.Opacity = sirenVisible ? 1.0 : 0.2;
        }

        private void InitializeFactoryIO()
        {
            try
            {
                // FactoryIOControl 인스턴스 생성 (서버 자동 시작됨)
                factoryIOControl = new FactoryIOControl();

                // 창을 숨김 상태로 유지 (백그라운드 실행)
                factoryIOControl.Hide();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Factory IO 초기화 실패: {ex.Message}",
                    "오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void InitializeConnectionMonitor()
        {
            connectionCheckTimer = new DispatcherTimer();
            connectionCheckTimer.Interval = TimeSpan.FromSeconds(2); // 2초마다 확인
            connectionCheckTimer.Tick += ConnectionCheckTimer_Tick;
            connectionCheckTimer.Start();

            Debug.WriteLine("✓ 연결 상태 모니터링 시작");
        }

        private void ConnectionCheckTimer_Tick(object sender, EventArgs e)
        {
            if (factoryIOControl == null) return;

            // 연결 상태를 UI에 표시 (선택사항)
            bool factoryConnected = factoryIOControl.IsConnected();
            bool plcConnected = factoryIOControl.IsPLCConnected();

            // 상태바나 텍스트에 표시하고 싶다면 여기에 추가
            // 예: txtConnectionStatus.Text = $"Factory IO: {(factoryConnected ? "연결됨" : "대기 중")} | PLC: {(plcConnected ? "연결됨" : "끊김")}";

            Debug.WriteLine($"[연결 상태] Factory IO: {factoryConnected}, PLC: {plcConnected}");
        }


        private void btnStartWork_Click(object sender, RoutedEventArgs e)
        {
            if (factoryIOControl == null)
            {
                MessageBox.Show("Factory IO 컨트롤러가 초기화되지 않았습니다.",
                    "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // 1. Factory IO 연결 확인
            if (!factoryIOControl.IsConnected())
            {
                var result = MessageBox.Show(
                    "Factory IO가 연결되지 않았습니다.\n\n" +
                    "Factory IO를 실행하고 Modbus TCP로 연결해주세요.\n" +
                    "(IP: localhost, Port: 502)\n\n" +
                    "Factory IO 없이 계속 진행하시겠습니까?",
                    "Factory IO 연결 필요",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning
                );

                if (result == MessageBoxResult.No)
                {
                    return;
                }
            }

            // 2. PLC 연결 확인 (선택사항)
            if (!factoryIOControl.IsPLCConnected())
            {
                var result = MessageBox.Show(
                    "PLC가 연결되지 않았습니다.\n\n" +
                    "PLC 없이도 Factory IO는 동작하지만,\n" +
                    "실제 PLC 제어는 불가능합니다.\n\n" +
                    "PLC 없이 계속 진행하시겠습니까?",
                    "PLC 연결 확인",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question
                );

                if (result == MessageBoxResult.No)
                {
                    return;
                }
            }

            // 3. Factory IO 시스템 시작 (PLC 메모리 값도 자동으로 설정됨)
            factoryIOControl.StartFactoryIOSystem();

            // 4. 시작 성공 메시지
            MessageBox.Show(
                "✅ 작업이 시작되었습니다!\n\n" +
                $"- Factory IO: {(factoryIOControl.IsConnected() ? "가동 중" : "미연결")}\n" +
                $"- PLC: {(factoryIOControl.IsPLCConnected() ? "제어 중" : "미연결")}\n" +
                "- 컨베이어: 가동\n\n" +
                "대시보드로 이동합니다.",
                "작업 시작",
                MessageBoxButton.OK,
                MessageBoxImage.Information
            );

            // 5. ResultDashboard 창 열기
            ResultDashboard dashboard = new ResultDashboard(factoryIOControl);
            dashboard.Show();

            // 6. 현재 WorkersInfo 창 닫기
            this.Close();
        }

        private void WorkersInfo_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // 타이머 정지
            sirenTimer?.Stop();
            connectionCheckTimer?.Stop();

            try
            {
                Debug.WriteLine("=== WorkersInfo 창 종료 - 프로그램 종료 시작 ===");

                // SafetyCheck 리소스 정리
                SafetyCheck.Cleanup();

                // ⭐ FactoryIOControl 완전히 종료
                if (factoryIOControl != null)
                {
                    // Factory IO 시스템이 실행 중이면 정지
                    if (factoryIOControl.IsSystemRunning())
                    {
                        factoryIOControl.StopFactoryIOSystem();
                    }

                    // FactoryIOControl 창도 완전히 닫기
                    factoryIOControl.ActualClose();
                }

                // MainWindow도 정리
                if (SafetyCheck.MainWin != null)
                {
                    SafetyCheck.MainWin.Close();
                }

                Debug.WriteLine("=== WorkersInfo 창 종료 완료 ===");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"WorkersInfo 종료 중 오류: {ex.Message}");
            }
            finally
            {
                // 전체 애플리케이션 종료
                Application.Current.Shutdown();
            }
        }
    }
}