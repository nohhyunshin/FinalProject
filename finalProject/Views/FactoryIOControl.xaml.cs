using finalProject.Models;
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using System.Drawing.Imaging;
using System.Linq;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.ComponentModel;

namespace finalProject.Views
{
    public partial class FactoryIOControl : Window
    {
        private TcpListener modbusServer;
        private TcpClient connectedClient;
        private NetworkStream stream;
        private DispatcherTimer plcTimer;
        private Thread serverThread;
        private PLCManager plcManager;
        private DispatcherTimer plcKeepAliveTimer;
        private bool isRunning = false;
        private bool isServerRunning = false;
        private bool _isClosing = false;

        // 로직 클래스 인스턴스
        private BasesLidsLogic basesLidsLogic;
        private PickAndPlaceLogic pickAndPlaceLogic;
        private StackerLogic stackerLogic;

        // Factory IO가 보내는 신호 (센서들) - Inputs
        private bool[] factoryInputs = new bool[100];

        // Factory IO가 읽는 신호 (액추에이터들) - Coils
        private bool[] factoryCoils = new bool[100];

        private ushort[] holdingRegisters = new ushort[100];

        // YOLO 및 분류 로직 변수
        private bool prodsAtEntryPrev = false;
        private bool convWithSensorStop = false;
        private bool prodAtPusherPrev = false;
        private bool productsPusher = false;
        private bool errorPusher = false;
        private bool prodCounterPrev = false;
        private bool errorCounterPrev = false;
        private bool prodsBoxNeeded = false;
        private bool errorsBoxNeeded = false;
        private bool prodsRollerActive = false;
        private bool errorsRollerActive = false;
        private bool prodCounterWasHigh = false;
        private bool errorCounterWasHigh = false;
        private bool normalSortStop = false;
        private bool deletePCBStop = false;
        private bool errorEnterPrev = false;
        private bool errorSortConvStop = false;
        private bool normalSensorPrev = false;
        private bool errorCateSensorPrev = false;

        private const int CONV_RESTART_DELAY = 40;
        private int pusherTimer = 0;
        private const int PUSHER_ACTIVE_TIME = 20;
        private int productCount = 0;
        private int errorCount = 0;
        private int prodRollerTimer = 0;
        private int errorRollerTimer = 0;
        private const int ROLLER_ACTIVE_TIME = 170;
        private const int ERROR_ROLLER_ACTIVE_TIME = 170;
        private int errorSortConvStopTimer = 0;
        private const int ERROR_SORT_CONV_RESTART_DELAY = 40;
        private int errorPusherTimer = 0;
        private const int ERROR_PUSHER_ACTIVE_TIME = 20;
        private int prodRollerDelayTimer = 0;
        private const int PROD_ROLLER_DELAY_TIME = 40; // 2초 (50ms * 40 = 2000ms)
        private bool prodRollerDelayActive = false;

        // 비전 시스템 관련 변수 추가
        private readonly CameraManager _cameraManager;
        private readonly ImageProcessor _imageProcessor;
        private readonly VisionProcessor _visionProcessor;
        private readonly ROIProcessor _roiProcessor;
        private InspectionStatistics inspectionStats;
        private readonly string _savePath = @"C:\Users\user\Desktop\PCB";
        private Bitmap _currentFrame;
        private readonly object _frameLock = new object();
        private bool isInspecting = false;
        private bool lastInspectionResult = true;
        private bool isClassifying = false;
        private bool shouldPushError = false;

        public FactoryIOControl()
        {
            InitializeComponent();

            // 로직 클래스 초기화
            basesLidsLogic = new BasesLidsLogic();
            pickAndPlaceLogic = new PickAndPlaceLogic();
            stackerLogic = new StackerLogic();

            InitializeModbusServer();
            InitializePlcTimer();
            InitializePLC();

            // 통계 관리자 초기화
            inspectionStats = new InspectionStatistics();

            // 비전 시스템 초기화 로직
            _cameraManager = new CameraManager();
            _imageProcessor = new ImageProcessor();
            _roiProcessor = new ROIProcessor(_savePath, "pcb_best.onnx");

            // ROI Processor 로그 이벤트 연결
            _roiProcessor.LogMessageRequested += LogMessage;
            _roiProcessor.BinarizedImageUpdated += UpdateBinarizedImage;

            // ONNX 모델 파일 경로 지정
            _visionProcessor = new VisionProcessor("pcb_best.onnx");

            // 저장 폴더 생성
            Directory.CreateDirectory(_savePath);

            // 카메라 시작 및 이벤트 핸들러 연결
            _cameraManager.NewFrame += CameraManager_NewFrame;

            this.Closing += FactoryIOControl_Closing;
        }

        public void StartFactoryIOSystem()  // ⭐ void를 그대로 유지 (WorkersInfo에서 수정)
        {
            if (!isRunning && connectedClient != null && connectedClient.Connected)
            {
                isRunning = true;
                plcTimer.Start();

                // ⭐⭐ PLC 컨베이어 가동 신호 전송 (%MX0 ON) ⭐⭐
                if (plcManager?.IsConnected == true)
                {
                    bool plcSuccess = plcManager.SetConveyorRunning(true);
                    if (plcSuccess)
                    {
                        LogMessage("✅ PLC 컨베이어 가동 신호 전송 성공 (%MX0 ON)");
                    }
                    else
                    {
                        LogMessage("⚠ PLC 컨베이어 가동 신호 전송 실패");
                    }
                }
                else
                {
                    LogMessage("⚠ PLC 미연결 - 컨베이어 신호 전송 생략");
                }

                // 카메라도 함께 시작
                if (!_cameraManager.StartCamera())
                {
                    LogMessage("사용 가능한 카메라를 찾을 수 없습니다.");
                    MessageBox.Show("카메라를 찾을 수 없습니다!", "카메라 오류", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                else
                {
                    LogMessage("카메라 시작됨.");
                }

                LogMessage("▶ 시스템 시작");
                LogMessage("  - Factory IO 가동");
                if (plcManager?.IsConnected == true)
                {
                    LogMessage("  - PLC 제어 활성화");
                }
            }
        }

        public bool StopFactoryIOSystem()
        {
            if (isRunning)
            {
                isRunning = false;
                plcTimer.Stop();
                ResetAllOutputs();

                // ⭐⭐ PLC 모든 신호 OFF ⭐⭐
                if (plcManager?.IsConnected == true)
                {
                    bool plcSuccess = plcManager.ResetAllSignals();
                    if (plcSuccess)
                    {
                        LogMessage("✅ PLC 신호 초기화 완료 (모든 신호 OFF)");
                    }
                    else
                    {
                        LogMessage("⚠ PLC 신호 초기화 실패");
                    }
                }

                LogMessage("⏸ 시스템 정지 (외부 호출)");

                return true;
            }

            return false;
        }

        public bool IsConnected()
        {
            return connectedClient != null && connectedClient.Connected;
        }

        public bool IsPLCConnected()
        {
            return plcManager?.IsConnected == true;
        }

        // 새 메서드 및 이벤트 핸들러 추가
        /// <summary>
        /// 카메라에서 새 프레임이 들어올 때마다 호출됩니다.
        /// </summary>
        private void CameraManager_NewFrame(Bitmap frame)
        {
            if (_isClosing) return;

            try
            {
                lock (_frameLock)
                {
                    _currentFrame?.Dispose();
                    _currentFrame = (Bitmap)frame.Clone();
                }

                // UI 스레드에서 이미지를 업데이트
                if (!_isClosing) // ⭐ 다시 확인
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_isClosing) return;

                        try
                        {
                            CameraFeed.Source = ROIProcessor.BitmapToBitmapSource(frame);
                        }
                        catch { }
                    }));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"프레임 처리 오류: {ex.Message}");
            }
        }

        /// <summary>
        /// ROI 영역을 캡처하고 전체 이미지 처리 파이프라인을 실행합니다.
        /// </summary>
        private void CaptureAndProcessROI_Sensor1()
        {
            Bitmap currentFrame;
            lock (_frameLock)
            {
                currentFrame = _currentFrame != null ? (Bitmap)_currentFrame.Clone() : null;
            }

            if (currentFrame != null)
            {
                _roiProcessor.ProcessROI_Sensor1(currentFrame);
                lastInspectionResult = _roiProcessor.LastInspectionResult;
                currentFrame.Dispose();
            }
        }

        private void CaptureAndProcessROI_Sensor2()
        {
            Bitmap currentFrame;
            lock (_frameLock)
            {
                currentFrame = _currentFrame != null ? (Bitmap)_currentFrame.Clone() : null;
            }

            if (currentFrame != null)
            {
                _roiProcessor.ProcessROI_Sensor2(currentFrame);
                lastInspectionResult = _roiProcessor.LastInspectionResult;
                currentFrame.Dispose();
            }
        }

        private void InitializePLC()
        {
            try
            {
                // PLC IP 주소 확인 필요
                plcManager = new PLCManager("192.168.0.200", 2004);

                // 이벤트 핸들러 연결
                plcManager.OnLogMessage += (msg) => LogMessage($"[PLC] {msg}");
                plcManager.OnConnected += () =>
                {
                    LogMessage("PLC 연결 성공");
                    Dispatcher.Invoke(() =>
                    {
                        LogMessage("PLC: 연결됨");
                    });
                };
                plcManager.OnDisconnected += () =>
                {
                    LogMessage("PLC 연결 끊김");
                    Dispatcher.Invoke(() =>
                    {
                        LogMessage("PLC: 끊김");
                    });
                };

                // PLC 연결 시도
                if (plcManager.Connect())
                {
                    // Keep-Alive 타이머 시작 (5초마다)
                    plcKeepAliveTimer = new DispatcherTimer();
                    plcKeepAliveTimer.Interval = TimeSpan.FromSeconds(5);
                    plcKeepAliveTimer.Tick += (s, e) => plcManager?.UpdateKeepAlive();
                    plcKeepAliveTimer.Start();

                    LogMessage("✓ PLC 통신 준비 완료");
                }
                else
                {
                    LogMessage("⚠ PLC 연결 실패 - Factory IO만 사용 가능");
                }
            }
            catch (Exception ex)
            {
                LogMessage($"⚠ PLC 초기화 실패: {ex.Message}");
            }
        }

        private void InitializeModbusServer()
        {
            try
            {
                modbusServer = new TcpListener(IPAddress.Any, 502);
                modbusServer.Start();
                isServerRunning = true;

                serverThread = new Thread(ListenForClients);
                serverThread.IsBackground = true;
                serverThread.Start();

                LogMessage("✓ Modbus 서버 시작 (Port 502)");
                LogMessage("  Factory IO에서 연결을 기다리는 중...");
            }
            catch (Exception ex)
            {
                LogMessage($"✗ Modbus 서버 시작 실패: {ex.Message}");
                MessageBox.Show($"Modbus 서버를 시작할 수 없습니다!\n\n오류: {ex.Message}",
                    "서버 오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ListenForClients()
        {
            while (isServerRunning)
            {
                try
                {
                    if (modbusServer.Pending())
                    {
                        connectedClient = modbusServer.AcceptTcpClient();
                        stream = connectedClient.GetStream();

                        Dispatcher.Invoke(() => LogMessage("✓ Factory IO 연결됨!"));

                        HandleClientRequests();
                    }
                    Thread.Sleep(100);
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => LogMessage($"연결 오류: {ex.Message}"));
                }
            }
        }

        private void HandleClientRequests()
        {
            byte[] buffer = new byte[256];

            while (connectedClient != null && connectedClient.Connected && isServerRunning)
            {
                try
                {
                    if (stream.DataAvailable)
                    {
                        int bytesRead = stream.Read(buffer, 0, buffer.Length);
                        if (bytesRead > 0)
                        {
                            byte[] response = ProcessModbusRequest(buffer, bytesRead);
                            if (response != null)
                            {
                                stream.Write(response, 0, response.Length);
                            }
                        }
                    }
                    Thread.Sleep(1);
                }
                catch (Exception)
                {
                    break;
                }
            }

            Dispatcher.Invoke(() => LogMessage("✗ Factory IO 연결 끊김"));
            connectedClient?.Close();
            connectedClient = null;
        }

        private byte[] ProcessModbusRequest(byte[] request, int length)
        {
            if (length < 8) return null;

            ushort transactionId = (ushort)((request[0] << 8) | request[1]);
            byte unitId = request[6];
            byte functionCode = request[7];

            switch (functionCode)
            {
                case 0x01: // Read Coils
                    return ReadCoils(request, transactionId, unitId);

                case 0x02: // Read Discrete Inputs
                    return ReadDiscreteInputs(request, transactionId, unitId);

                case 0x03: // Read Holding Registers
                    return ReadHoldingRegisters(request, transactionId, unitId);

                case 0x05: // Write Single Coil
                    return WriteSingleCoil(request, transactionId, unitId);

                case 0x0F: // Write Multiple Coils
                    return WriteMultipleCoils(request, transactionId, unitId);

                default:
                    LogMessage($"⚠ 지원하지 않는 Function Code: {functionCode:X2}");
                    return null;
            }
        }

        private byte[] ReadCoils(byte[] request, ushort transactionId, byte unitId)
        {
            ushort startAddress = (ushort)((request[8] << 8) | request[9]);
            ushort quantity = (ushort)((request[10] << 8) | request[11]);

            int byteCount = (quantity + 7) / 8;
            byte[] response = new byte[9 + byteCount];

            response[0] = (byte)(transactionId >> 8);
            response[1] = (byte)(transactionId & 0xFF);
            response[2] = 0x00;
            response[3] = 0x00;
            response[4] = (byte)((3 + byteCount) >> 8);
            response[5] = (byte)((3 + byteCount) & 0xFF);
            response[6] = unitId;
            response[7] = 0x01;
            response[8] = (byte)byteCount;

            for (int i = 0; i < quantity; i++)
            {
                if (startAddress + i < factoryCoils.Length && factoryCoils[startAddress + i])
                {
                    response[9 + i / 8] |= (byte)(1 << (i % 8));
                }
            }

            return response;
        }

        private byte[] ReadDiscreteInputs(byte[] request, ushort transactionId, byte unitId)
        {
            ushort startAddress = (ushort)((request[8] << 8) | request[9]);
            ushort quantity = (ushort)((request[10] << 8) | request[11]);

            int byteCount = (quantity + 7) / 8;
            byte[] response = new byte[9 + byteCount];

            response[0] = (byte)(transactionId >> 8);
            response[1] = (byte)(transactionId & 0xFF);
            response[2] = 0x00;
            response[3] = 0x00;
            response[4] = (byte)((3 + byteCount) >> 8);
            response[5] = (byte)((3 + byteCount) & 0xFF);
            response[6] = unitId;
            response[7] = 0x02;
            response[8] = (byte)byteCount;

            for (int i = 0; i < quantity; i++)
            {
                if (startAddress + i < factoryCoils.Length && factoryCoils[startAddress + i])
                {
                    response[9 + i / 8] |= (byte)(1 << (i % 8));
                }
            }

            return response;
        }

        private byte[] ReadHoldingRegisters(byte[] request, ushort transactionId, byte unitId)
        {
            ushort startAddress = (ushort)((request[8] << 8) | request[9]);
            ushort quantity = (ushort)((request[10] << 8) | request[11]);

            byte[] response = new byte[9 + (quantity * 2)];

            response[0] = (byte)(transactionId >> 8);
            response[1] = (byte)(transactionId & 0xFF);
            response[2] = 0x00;
            response[3] = 0x00;
            response[4] = (byte)((3 + (quantity * 2)) >> 8);
            response[5] = (byte)((3 + (quantity * 2)) & 0xFF);
            response[6] = unitId;
            response[7] = 0x03;
            response[8] = (byte)(quantity * 2);

            for (int i = 0; i < quantity; i++)
            {
                int address = startAddress + i;
                if (address < holdingRegisters.Length)
                {
                    response[9 + (i * 2)] = (byte)(holdingRegisters[address] >> 8);
                    response[10 + (i * 2)] = (byte)(holdingRegisters[address] & 0xFF);
                }
            }

            return response;
        }

        private byte[] WriteSingleCoil(byte[] request, ushort transactionId, byte unitId)
        {
            ushort address = (ushort)((request[8] << 8) | request[9]);
            bool value = request[10] == 0xFF;

            if (address < factoryInputs.Length)
            {
                factoryInputs[address] = value;
            }

            byte[] response = new byte[12];
            Array.Copy(request, response, 12);
            return response;
        }

        private byte[] WriteMultipleCoils(byte[] request, ushort transactionId, byte unitId)
        {
            ushort startAddress = (ushort)((request[8] << 8) | request[9]);
            ushort quantity = (ushort)((request[10] << 8) | request[11]);

            for (int i = 0; i < quantity; i++)
            {
                int address = startAddress + i;
                if (address < factoryInputs.Length)
                {
                    bool value = (request[13 + i / 8] & (1 << (i % 8))) != 0;
                    factoryInputs[address] = value;
                }
            }

            byte[] response = new byte[12];
            response[0] = (byte)(transactionId >> 8);
            response[1] = (byte)(transactionId & 0xFF);
            response[2] = 0x00;
            response[3] = 0x00;
            response[4] = 0x00;
            response[5] = 0x06;
            response[6] = unitId;
            response[7] = 0x0F;
            response[8] = request[8];
            response[9] = request[9];
            response[10] = request[10];
            response[11] = request[11];

            return response;
        }

        private void InitializePlcTimer()
        {
            plcTimer = new DispatcherTimer();
            plcTimer.Interval = TimeSpan.FromMilliseconds(50);
            plcTimer.Tick += PlcScanCycle;
        }

        private void PlcScanCycle(object sender, EventArgs e)
        {
            try
            {
                ExecutePlcLogic();
            }
            catch (Exception ex)
            {
                LogMessage($"⚠ 스캔 오류: {ex.Message}");
            }
        }

        private void ExecutePlcLogic()
        {
            // 모든 입력이 꺼졌을 때 리셋
            bool allInputsOff = !factoryInputs[FactoryAddresses.INPUT_BASES_AT_ENTRY] &&
                                !factoryInputs[FactoryAddresses.INPUT_BASES_AT_EXIT] &&
                                !factoryInputs[FactoryAddresses.INPUT_LIDS_AT_ENTRY] &&
                                !factoryInputs[FactoryAddresses.INPUT_LIDS_AT_EXIT];

            if (allInputsOff)
            {
                // 필요시 리셋 로직
            }

            // 1. Bases 로직 실행
            basesLidsLogic.ExecuteBasesLogic(factoryInputs, factoryCoils);

            // 2. Lids 로직 실행
            basesLidsLogic.ExecuteLidsLogic(factoryInputs, factoryCoils);

            // 3. 픽앤플레이스 조립 로직 실행
            pickAndPlaceLogic.ExecuteAssembly(
                basesLidsLogic.BasesReadyForAssembly,
                basesLidsLogic.LidsReadyForAssembly,
                factoryCoils,
                basesLidsLogic
            );

            // 4. YOLO 모델 연동 - 불량 검출
            ExecuteYoloDetectionLogic();

            // 5. 정상 제품 분류 로직
            ExecuteNormalSortingLogic();

            // 6. 불량 제품 분류 로직
            ExecuteErrorSortingLogic();

            // 7. Stacker 로직 실행
            stackerLogic.ExecuteNormalStacker(factoryInputs, factoryCoils, holdingRegisters);
            stackerLogic.ExecuteErrorStacker(factoryInputs, factoryCoils, holdingRegisters);

            // 8. 공통 컨베이어 제어
            factoryCoils[FactoryAddresses.COIL_CURVED_CONVC] = true;
            factoryCoils[FactoryAddresses.COIL_SORT_CONVC] = !errorSortConvStop;
        }

        // 불량 유무 검출
        private void ExecuteYoloDetectionLogic()
        {
            bool prodsEnter = factoryInputs[FactoryAddresses.INPUT_ERROR_DERECTED] && !prodsAtEntryPrev;
            prodsAtEntryPrev = factoryInputs[FactoryAddresses.INPUT_ERROR_DERECTED];

            if (prodsEnter)
            {
                convWithSensorStop = true;
                isInspecting = true;


                if (!prodsRollerActive)
                {
                    prodsBoxNeeded = true;
                }

                // ⭐⭐ PLC 컨베이어 정지 신호 추가 ⭐⭐
                if (plcManager?.IsConnected == true)
                {
                    bool stopSuccess = plcManager.SetConveyorRunning(false);
                    if (stopSuccess)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            LogMessage("⏸ PLC 컨베이어 정지 (%MX0 OFF)");
                        });
                    }
                }

                // ⭐ 비동기 처리로 변경
                Task.Run(() =>
                {
                    try
                    {
                        CaptureAndProcessROI_Sensor1();

                        Dispatcher.Invoke(() =>
                        {
                            // 불량 유형 리스트 가져오기
                            List<string> detectedDefects = _roiProcessor.LastDetectedDefects?.ToList() ?? new List<string>();

                            // 통계 기록
                            inspectionStats.RecordInspection(lastInspectionResult, detectedDefects, _roiProcessor.LastTotalDefectCount);

                            // ResultDashboard UI 업데이트 (ResultDashboard가 열려있는 경우)
                            UpdateDashboardStatistics();

                            // 로그 출력
                            if (lastInspectionResult)
                            {
                                LogMessage($"✅ 정상 제품 [{inspectionStats.TotalInspected}번째 검사]");
                            }
                            else
                            {
                                string defectTypes = detectedDefects.Count > 0 ? string.Join(", ", detectedDefects) : "분류 안됨";
                            }
                        });

                        // PLC로 검사 결과 전송
                        if (plcManager?.IsConnected == true)
                        {
                            bool sendSuccess = plcManager.SendInspectionResult(lastInspectionResult);
                            if (sendSuccess)
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    if (lastInspectionResult)
                                    {
                                        LogMessage("✅ PLC로 정상 제품 신호 전송 (%MX1)");
                                    }
                                    else
                                    {
                                        LogMessage("❌ PLC로 불량 제품 신호 전송 (%MX2)");
                                    }
                                });
                            }
                        }

                        System.Threading.Thread.Sleep(300);
                        convWithSensorStop = false;
                        isInspecting = false;

                        // PLC 컨베이어 재가동
                        if (plcManager?.IsConnected == true)
                        {
                            plcManager.SetConveyorRunning(true);
                            Dispatcher.Invoke(() =>
                            {
                                LogMessage("▶ PLC 컨베이어 재가동 (%MX0 ON)");
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            LogMessage($"⚠ 검사 중 오류: {ex.Message}");
                        });

                        convWithSensorStop = false;
                        isInspecting = false;

                        if (plcManager?.IsConnected == true)
                        {
                            plcManager.SetConveyorRunning(true);
                        }
                    }
                });
            }

            // 조명 제어 로직 (기존과 동일)
            if (factoryInputs[FactoryAddresses.INPUT_ERROR_DERECTED])
            {
                bool yellowLight = isInspecting;
                bool greenLight = !isInspecting && lastInspectionResult;
                bool redLight = !isInspecting && !lastInspectionResult;

                factoryCoils[FactoryAddresses.COIL_DEFECTED_LIGHT] = yellowLight;
                factoryCoils[FactoryAddresses.COIL_NORMAL_LIGHT] = greenLight;
                factoryCoils[FactoryAddresses.COIL_ERROR_LIGHT] = redLight;
            }
            else
            {
                factoryCoils[FactoryAddresses.COIL_DEFECTED_LIGHT] = false;
                factoryCoils[FactoryAddresses.COIL_NORMAL_LIGHT] = false;
                factoryCoils[FactoryAddresses.COIL_ERROR_LIGHT] = false;
            }

            // ⭐ 중요: convWithSensorStop 상태에 따라 컨베이어 제어
            factoryCoils[FactoryAddresses.COIL_CONV_WITH_SENSOR] = !convWithSensorStop;
            factoryCoils[FactoryAddresses.COIL_BOX_EMITTER] = prodsBoxNeeded && !prodsRollerActive;
        }

        private void ExecuteNormalSortingLogic()
        {
            // 1. 정상 제품 센서 감지
            bool normalSensor = factoryInputs[FactoryAddresses.INPUT_NORMAL_SENSOR];
            bool productPassedSensor = !normalSensor && normalSensorPrev;

            if (productPassedSensor && lastInspectionResult)
            {
                normalSortStop = true;
                productsPusher = true;
                pusherTimer = 0;
            }
            normalSensorPrev = normalSensor;

            // 2. Pusher 타이머 처리
            if (productsPusher)
            {
                pusherTimer++;
                if (pusherTimer >= PUSHER_ACTIVE_TIME)
                {
                    normalSortStop = false;
                    productsPusher = false;
                    pusherTimer = 0;
                }
            }

            // 3. 정상 제품 카운트
            bool prodCounterCurrent = factoryInputs[FactoryAddresses.INPUT_PROD_COUNTER];

            if (prodCounterCurrent && !prodCounterPrev)
            {
                prodCounterWasHigh = true;
            }

            if (!prodCounterCurrent && prodCounterPrev && prodCounterWasHigh)
            {
                productCount++;
                LogMessage($"📦 상품 개수 측정 : {productCount}/3");
                prodCounterWasHigh = false;
            }
            prodCounterPrev = prodCounterCurrent;

            // 4. 박스에 3개 담기면 롤러 가동
            if (productCount >= 3 && !prodsRollerActive && !prodRollerDelayActive)
            {
                prodRollerDelayActive = true;
                prodRollerDelayTimer = 0;
                prodRollerTimer = 0;
                productCount = 0;
                prodsBoxNeeded = false;
            }

            if (prodRollerDelayActive)
            {
                prodRollerDelayTimer++;
                if (prodRollerDelayTimer >= PROD_ROLLER_DELAY_TIME)
                {
                    prodsRollerActive = true;
                    prodRollerTimer = 0;
                    prodRollerDelayActive = false;
                    prodRollerDelayTimer = 0;
                }
            }

            if (prodsRollerActive)
            {
                prodRollerTimer++;
                if (prodRollerTimer >= ROLLER_ACTIVE_TIME)
                {
                    prodsRollerActive = false;
                    prodRollerTimer = 0;
                }
            }

            // Outputs
            factoryCoils[FactoryAddresses.COIL_NORMAL_PUSHER] = productsPusher;
            factoryCoils[FactoryAddresses.COIL_NORMAL_SORT] = !normalSortStop;
            factoryCoils[FactoryAddresses.COIL_NORMAL_ROLLER] = prodsRollerActive;
        }

        private void ExecuteErrorSortingLogic()
        {
            // 1. 불량 종류 분석
            bool errorEnter = factoryInputs[FactoryAddresses.INPUT_ERROR_SORT_SENSOR] && !errorEnterPrev;
            errorEnterPrev = factoryInputs[FactoryAddresses.INPUT_ERROR_SORT_SENSOR];

            if (errorEnter)
            {
                // 컨베이어 정지 시작
                errorSortConvStop = true;
                errorSortConvStopTimer = 0;

                if (!errorsRollerActive)
                {
                    errorsBoxNeeded = true;
                }

                // 카메라 작업 없이 이미 분석된 결과만 사용
                List<string> detectedDefects = _roiProcessor.LastDetectedDefects?.ToList() ?? new List<string>();
                string defectInfo = detectedDefects.Count > 0
                    ? string.Join(", ", detectedDefects)
                    : "분류 안됨";

                LogMessage($"🔍 불량 분류 - 유형: {defectInfo}, 개수: {detectedDefects.Count}");

                // PLC로 결과 전송 (선택사항)
                if (plcManager?.IsConnected == true)
                {
                    bool sendSuccess = plcManager.SendInspectionResult(lastInspectionResult);
                    if (sendSuccess)
                    {
                        if (lastInspectionResult)
                        {
                            LogMessage("✅ PLC로 정상 제품 신호 전송 (%MX1)");
                        }
                        else
                        {
                            LogMessage("❌ PLC로 불량 제품 신호 전송 (%MX2)");
                        }
                    }
                }

                // pin-hole 유무에 따른 재가공 / 폐기 결정
                bool hasPinHole = detectedDefects.Contains("pin-hole");
                shouldPushError = !hasPinHole;  // pin-hole 없으면 폐기 (pusher 작동)

                if (hasPinHole)
                {
                    LogMessage("🔴 pin-hole 포함 → 재가공 불가 (빨간불)");
                }
                else
                {
                    LogMessage("🟡 pin-hole 없음 → 재가공 가능 (노란불)");
                }
            }

            // 컨베이어 정지 타이머 (2초 = 50ms * 40 = 2000ms)
            if (errorSortConvStop)
            {
                errorSortConvStopTimer++;
                if (errorSortConvStopTimer >= ERROR_SORT_CONV_RESTART_DELAY)
                {
                    errorSortConvStop = false;
                    errorSortConvStopTimer = 0;
                    LogMessage("▶ 불량 분류 컨베이어 재가동");
                }
            }

            // 조명 제어 수정
            if (factoryInputs[FactoryAddresses.INPUT_ERROR_SORT_SENSOR])
            {
                // 센서에 제품이 있을 때만 조명 제어
                bool yellowLight = shouldPushError;      // 재가공 가능 (pin-hole 없음)
                bool redLight = !shouldPushError;        // 재가공 불가 (pin-hole 있음)

                factoryCoils[FactoryAddresses.COIL_REPROCESSING] = yellowLight;
                factoryCoils[FactoryAddresses.COIL_DISPOSED_LIGTH] = redLight;
            }
            else
            {
                // 센서에 제품이 없으면 모든 조명 OFF
                factoryCoils[FactoryAddresses.COIL_REPROCESSING] = false;
                factoryCoils[FactoryAddresses.COIL_DISPOSED_LIGTH] = false;
            }

            // 2. 불량 컨베이어 Pusher
            bool errorCateSensor = factoryInputs[FactoryAddresses.INPUT_ERROR_CATE_SENSOR];
            bool errorPassedSensor = !errorCateSensor && errorCateSensorPrev;

            if (errorPassedSensor && shouldPushError)
            {
                deletePCBStop = true;
                errorPusher = true;
                errorPusherTimer = 0;
                shouldPushError = false;
                LogMessage("⚡ 폐기 Pusher 작동");
            }
            errorCateSensorPrev = errorCateSensor;

            if (errorPusher)
            {
                errorPusherTimer++;
                if (errorPusherTimer >= ERROR_PUSHER_ACTIVE_TIME)
                {
                    deletePCBStop = false;
                    errorPusher = false;
                    errorPusherTimer = 0;
                }
            }

            // 3. 불량 제품 카운트
            bool errorCounterCurrent = factoryInputs[FactoryAddresses.INPUT_ERROR_COUNTER];

            if (errorCounterCurrent && !errorCounterPrev)
            {
                errorCounterWasHigh = true;
            }

            if (!errorCounterCurrent && errorCounterPrev && errorCounterWasHigh)
            {
                errorCount++;
                LogMessage($"📦 불량품 개수 측정 : {errorCount}/3");
                errorCounterWasHigh = false;
            }
            errorCounterPrev = errorCounterCurrent;

            // 4. 박스에 3개 담기면 롤러 가동
            if (errorCount >= 3 && !errorsRollerActive)
            {
                errorsRollerActive = true;
                errorRollerTimer = 0;
                errorCount = 0;
                errorsBoxNeeded = false;
            }

            if (errorsRollerActive)
            {
                errorRollerTimer++;
                if (errorRollerTimer >= ERROR_ROLLER_ACTIVE_TIME)
                {
                    errorsRollerActive = false;
                    errorRollerTimer = 0;
                }
            }

            // Outputs
            factoryCoils[FactoryAddresses.COIL_ERROR_BOX_EMITTER] = errorsBoxNeeded && !errorsRollerActive;
            factoryCoils[FactoryAddresses.COIL_ERROR_PUSHER] = errorPusher;
            factoryCoils[FactoryAddresses.COIL_DEL_PCB] = !deletePCBStop;
            factoryCoils[FactoryAddresses.COIL_ERROR_ROLLER] = errorsRollerActive;
        }

        private void UpdateDashboardStatistics()
        {
            try
            {
                // 이벤트를 통한 업데이트
                OnStatisticsUpdated?.Invoke(inspectionStats);
            }
            catch (Exception ex)
            {
                LogMessage($"⚠ Dashboard 업데이트 오류: {ex.Message}");
            }
        }

        // 5. 통계 이벤트 선언 (방법 2를 사용하는 경우)
        public event Action<InspectionStatistics> OnStatisticsUpdated;

        // 6. 통계 초기화 메서드
        public void ResetStatistics()
        {
            inspectionStats.Reset();
            LogMessage("📊 통계가 초기화되었습니다.");
            UpdateDashboardStatistics();
        }

        // 7. 통계 데이터 접근 메서드 (외부에서 통계 조회용)
        public InspectionStatistics GetCurrentStatistics()
        {
            return inspectionStats;
        }

        private void ResetAllOutputs(bool resetStatistics = false)
        {
            Array.Clear(factoryCoils, 0, factoryCoils.Length);

            // 로직 클래스 리셋
            basesLidsLogic.Reset();
            pickAndPlaceLogic.Reset();
            stackerLogic.Reset();

            // YOLO 및 분류 변수 리셋
            prodsAtEntryPrev = false;
            convWithSensorStop = false;
            productsPusher = false;
            errorPusher = false;
            prodCounterPrev = false;
            errorCounterPrev = false;
            prodsBoxNeeded = false;
            errorsBoxNeeded = false;
            prodsRollerActive = false;
            errorsRollerActive = false;
            prodCounterWasHigh = false;
            errorCounterWasHigh = false;
            normalSortStop = false;
            deletePCBStop = false;
            errorEnterPrev = false;
            errorSortConvStop = false;
            normalSensorPrev = false;
            errorCateSensorPrev = false;

            pusherTimer = 0;
            productCount = 0;
            errorCount = 0;
            prodRollerTimer = 0;
            errorRollerTimer = 0;
            errorSortConvStopTimer = 0;
            errorPusherTimer = 0;
            isInspecting = false;
            lastInspectionResult = true;

            // 통계도 리셋할지 선택
            if (resetStatistics)
            {
                inspectionStats.Reset();
                LogMessage("📊 통계도 함께 초기화되었습니다.");
            }
        }

        // 분석 결과 로그 메세지 연동
        private void LogMessage(string message)
        {
            Dispatcher.Invoke(() =>
            {
                string timeStamp = DateTime.Now.ToString("HH:mm:ss.fff");
                TxtLog.Items.Add($"[{timeStamp}] {message}");

                // 자동 스크롤 (최신 로그로)
                if (TxtLog.Items.Count > 0)
                {
                    TxtLog.ScrollIntoView(TxtLog.Items[TxtLog.Items.Count - 1]);
                }
            });
        }

        // 이진화 이미지 연동
        private void UpdateBinarizedImage(BitmapSource binarizedImage)
        {
            Dispatcher.Invoke(() =>
            {
                BinarizedImageFeed.Source = binarizedImage;
            });
        }

        public bool IsSystemRunning()
        {
            return isRunning;
        }

        public void ShowWindow()
        {
            this.Show();
            this.Activate();
        }

        public void HideWindow()
        {
            this.Hide();
        }

        private void FactoryIOControl_Closing(object sender, CancelEventArgs e)
        {
            // 실제 종료가 아닌 경우 (X 버튼 클릭)
            if (!_isClosing)
            {
                e.Cancel = true;  // 창 닫기 취소
                this.Hide();      // 창만 숨기기
                LogMessage("Vision 창이 숨겨졌습니다. (카메라는 계속 동작 중)");
                return;
            }

            try
            {
                // 1. PLC 타이머 정지
                isRunning = false;
                plcTimer?.Stop();
                plcKeepAliveTimer?.Stop();
                Console.WriteLine("타이머 정지 완료");

                // 2. 출력 리셋
                ResetAllOutputs();
                Console.WriteLine("출력 리셋 완료");

                // 3. PLC 신호 리셋 및 연결 해제 ⭐⭐
                if (plcManager != null)
                {
                    LogMessage("PLC 종료 처리 중...");
                    plcManager.ResetAllSignals();
                    System.Threading.Thread.Sleep(300);
                    plcManager.Dispose();
                    Console.WriteLine("PLC 정리 완료");
                }

                // 4. 카메라 정지
                StopCameraResources();

                // 5. 네트워크 스트림 정리
                isServerRunning = false;
                try { stream?.Close(); stream?.Dispose(); stream = null; } catch { }
                try { connectedClient?.Close(); connectedClient = null; } catch { }
                try { modbusServer?.Stop(); modbusServer = null; } catch { }

                // 6. 서버 스레드 종료 대기
                if (serverThread != null && serverThread.IsAlive)
                {
                    serverThread.Join(1000);
                }

                System.Threading.Thread.Sleep(300);
                Console.WriteLine("=== FactoryIOControl 종료 완료 ===");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"종료 중 오류: {ex.Message}");
            }
        }

        // 대시보드에서 실제로 프로그램 종료할 때 호출
        public void ActualClose()
        {
            _isClosing = true;
            this.Close();
        }

        // 카메라 리소스 정리 메서드 (강화)
        private void StopCameraResources()
        {
            Console.WriteLine("=== 카메라 리소스 정리 시작 ===");

            try
            {
                // 1. 이벤트 핸들러 제거 (중요!)
                if (_cameraManager != null)
                {
                    _cameraManager.NewFrame -= CameraManager_NewFrame;
                    Console.WriteLine("카메라 이벤트 해제 완료");
                }

                // 2. 카메라 정지
                if (_cameraManager != null && _cameraManager.IsRunning)
                {
                    _cameraManager.StopCamera();
                    Console.WriteLine("카메라 정지 완료");
                }

                // 3. 현재 프레임 정리
                lock (_frameLock)
                {
                    _currentFrame?.Dispose();
                    _currentFrame = null;
                    Console.WriteLine("현재 프레임 정리 완료");
                }

                // 4. Vision Processor 정리 (IDisposable이면)
                try
                {
                    (_visionProcessor as IDisposable)?.Dispose();
                    Console.WriteLine("Vision Processor 정리 완료");
                }
                catch { }

                // 5. 약간의 대기 (카메라 완전 해제)
                System.Threading.Thread.Sleep(200);

                Console.WriteLine("=== 카메라 리소스 정리 완료 ===");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"카메라 정리 오류: {ex.Message}");
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            Console.WriteLine("=== OnClosed 호출 ===");
            _isClosing = true;

            // 혹시 Closing에서 정리 안 된 것들 재정리
            try
            {
                StopCameraResources();
            }
            catch { }

            base.OnClosed(e);

            // ⭐ 강제 종료 (최후의 수단)
            System.Threading.Thread.Sleep(300);
            Console.WriteLine("=== OnClosed 완료 ===");
        }
    }
}