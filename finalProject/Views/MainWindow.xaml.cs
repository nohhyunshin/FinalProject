using finalProject.Models;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using System.Collections.Generic;
using System.Text;
using System.Threading; // Thread using 추가
using System.Threading.Tasks; // Task using 추가
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Path = System.IO.Path;
using Window = System.Windows.Window;

namespace finalProject
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private VideoCapture _capture;
        private Mat _frame;
        private CancellationTokenSource _cts;
        private bool _isRunning = false;
        private bool _isClosing = false;

        // (신규) 중복 검사 실행 방지 플래그
        private bool _isCheckRunning = false;

        public MainWindow()
        {
            InitializeComponent();
            _frame = new Mat();

            // CameraDetection 초기화 및 MainWindow 참조 설정
            SafetyCheck.MainWin = this;
            SafetyCheck.InitializeModel();

            // PPE 체크 후 UI 업데이트
            SafetyCheckUI.MainWin = this;

            // 이메일 전송 코드 초기화
            SafetyAlert.Initialize();

            // 창 로드 시 자동 실행
            this.Loaded += MainWindow_Loaded;
        }

        // 창 로드 시 자동 실행 이벤트 핸들러
        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (_isClosing) return;

            // (신규) 2초 후 캡처 및 분석 실행
            await StartCameraAndRunCheckAsync();
        }

        /// <summary>
        /// (신규) 카메라를 시작하고, 2초 후 1회 캡처 및 분석을 실행합니다.
        /// </summary>
        public async Task StartCameraAndRunCheckAsync()
        {
            if (_isCheckRunning || _isClosing) return;
            _isCheckRunning = true; // 검사 중복 실행 방지

            try
            {
                _capture = new VideoCapture(0);
                if (!_capture.IsOpened())
                {
                    MessageBox.Show("카메라를 열 수 없습니다.", "Error");
                    _capture?.Release();
                    _capture?.Dispose();
                    _capture = null;
                    return;
                }

                _cts = new CancellationTokenSource();
                _isRunning = true;

                // (수정) 카메라 루프는 이제 UI 표시만 담당
                _ = RunCameraLoopAsync(_cts.Token);

                // Live 문구 숨기기
                liveDot.Visibility = Visibility.Collapsed;
                txtLive.Visibility = Visibility.Collapsed;

                // (요청사항) 카메라 시작 전 딜레이 2초
                await Task.Delay(2000, _cts.Token);

                // (요청사항) 2초 후 1회 캡처
                if (_capture.Read(_frame) && !_frame.Empty() && !_isClosing)
                {
                    // (요청사항) 캡처한 프레임으로 분석 및 화면 전환 실행
                    // 이 함수가 카메라 중지 및 화면 전환까지 모두 처리합니다.
                    await SafetyCheck.PerformOneShotCheckAsync(_frame.Clone());
                }
                else if (!_isClosing)
                {
                    MessageBox.Show("캡처에 실패했습니다.", "Error");
                }
            }
            catch (OperationCanceledException)
            {
                // 정상이면 무시
            }
            catch (Exception ex)
            {
                if (!_isClosing)
                {
                    MessageBox.Show($"카메라 동작 오류: {ex.Message}", "Error");
                }
                StopCamera();
            }
            // finally에서 _isCheckRunning = false; 제거
            // (성공 시 어차피 화면이 넘어가므로)
        }

        /// <summary>
        /// (수정) 이 루프는 이제 'UI에 카메라 영상 표시'만 담당합니다. (SafetyCheck 제거)
        /// </summary>
        private async Task RunCameraLoopAsync(CancellationToken token)
        {
            await Task.Run(async () =>
            {
                while (!token.IsCancellationRequested && !_isClosing)
                {
                    if (_capture == null || !_capture.IsOpened() && !_isClosing) break;

                    try
                    {
                        if (_capture.Read(_frame) && !_frame.Empty() && !_isClosing)
                        {
                            // --- (수정) ---
                            // SafetyCheck.ProcessFrame(_frame); 호출 제거
                            // ---

                            if (!_isClosing && Application.Current != null)
                            {
                                try
                                {
                                    Application.Current.Dispatcher.Invoke(() =>
                                    {
                                        if (_isClosing) return;

                                        try
                                        {
                                            BitmapSource bmp = _frame.ToWriteableBitmap();
                                            imgCamera.Source = bmp;

                                            // 카메라 실행 시 라이브 문구 송출
                                            liveDot.Visibility = Visibility.Visible;
                                            txtLive.Visibility = Visibility.Visible;

                                            // 카메라 실행 시 문구 및 로딩바 숨김
                                            progressRing.Visibility = Visibility.Collapsed;
                                            camera.Visibility = Visibility.Collapsed;
                                            txtCam.Visibility = Visibility.Collapsed;
                                        }
                                        catch (Exception uiEx)
                                        {
                                            Console.WriteLine($"UI 업데이트 오류: {uiEx.Message}");
                                        }

                                    });
                                }
                                catch (TaskCanceledException)
                                {
                                    break;
                                }
                            }
                        }
                        else
                        {
                            await Task.Delay(10, token);
                        }
                        await Task.Delay(15, token); // UI 표시는 부드럽게
                    }
                    catch (OperationCanceledException) { break; }
                    catch (ObjectDisposedException) { break; }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"카메라 루프 오류: {ex.Message}");
                        if (!_isClosing) { await Task.Delay(100, token); }
                        else { break; }
                    }
                }

                try
                {
                    _frame?.Release();
                    _frame = null;
                }
                catch { }
            }, token);
        }

        public void StopCamera()
        {
            if (!_isRunning) return;

            Console.WriteLine("카메라 중지 시작...");
            _isRunning = false;

            try { _cts?.Cancel(); } catch { }
            Thread.Sleep(100);
            try { _cts?.Dispose(); _cts = null; } catch { }
            try { _capture?.Release(); _capture?.Dispose(); _capture = null; } catch { }

            Console.WriteLine("카메라 중지 완료");
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            Console.WriteLine("앱 종료 시작...");
            _isClosing = true;

            StopCamera();

            try { SafetyCheck.Cleanup(); }
            catch (Exception ex) { Console.WriteLine($"SafetyCheck 해제 오류: {ex.Message}"); }

            Console.WriteLine("앱 종료 완료");
            base.OnClosing(e);
        }
    }
}