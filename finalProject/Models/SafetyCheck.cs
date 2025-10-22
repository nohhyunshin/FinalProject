using finalProject.Views;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading; // Timer using
using System.Threading.Tasks;
using System.Windows;
using Rect = OpenCvSharp.Rect;
using Size = OpenCvSharp.Size;

namespace finalProject.Models
{
    /// <summary>
    /// (신규) PPE 분석 결과를 담을 클래스
    /// </summary>
    public class PPEResult
    {
        public bool HasHelmet { get; set; }
        public bool HasVest { get; set; }
        public bool HasGloves { get; set; }
        public bool HasGoggles { get; set; }
    }

    class SafetyCheck
    {
        public static MainWindow MainWin { get; set; }
        public static WorkersInfo WorkersWin { get; set; }

        // 쿨다운 타이머
        private static DateTime lastEmailAlert = DateTime.MinValue;
        private static readonly TimeSpan emailCooldown = TimeSpan.FromHours(6);

        // ONNX 모델 세션
        private static InferenceSession session;

        // (삭제) --- 실시간 처리 로직 모두 제거 ---
        // private static bool isProcessingActive = true;
        // private static string currentWorkerId = null;
        // private static DateTime lastFaceRecognitionTime = DateTime.MinValue;
        // ... (관련 변수 모두 삭제) ...
        // (삭제) ---

        public static void InitializeModel()
        {
            string modelPath = "best.onnx";     // 모델 경로
            session = new InferenceSession(modelPath);
        }

        // (삭제) ProcessFrame, TryRecognizeFaceAsync, AnalyzePPEStatus
        // (모두 PerformOneShotCheckAsync로 통합/대체됨)


        /// <summary>
        /// (신규) 캡처된 단일 프레임으로 안면 인식과 PPE 분석을 '동시에' 실행하고
        /// 완료되는 즉시 화면을 전환합니다.
        /// </summary>
        public static async Task PerformOneShotCheckAsync(Mat frame)
        {
            if (MainWin == null || frame.Empty() || session == null)
            {
                frame?.Dispose();
                return;
            }

            try
            {
                Debug.WriteLine("=== One-Shot Check Start ===");

                // 1. 안면 인식과 PPE 분석을 병렬로 시작
                Task<Worker> faceTask = FaceRecognitionService.RecognizeFaceAsync(frame.Clone());
                Task<PPEResult> ppeTask = RunYoloCheckAsync(frame.Clone());

                // 2. 두 작업이 모두 끝날 때까지 대기
                await Task.WhenAll(faceTask, ppeTask);

                // 3. 결과 수집
                Worker recognizedWorker = await faceTask;
                PPEResult ppeResult = await ppeTask;

                Debug.WriteLine($"[결과] 안면 인식: {recognizedWorker?.Name ?? "UNKNOWN"}");
                Debug.WriteLine($"[결과] PPE: Helmet={ppeResult.HasHelmet}, Vest={ppeResult.HasVest}, Gloves={ppeResult.HasGloves}, Goggles={ppeResult.HasGoggles}");

                // ★★★ 추가: MainWindow UI 업데이트 ★★★
                SafetyCheckUI.UpdatePPEUI(ppeResult.HasHelmet, ppeResult.HasVest, ppeResult.HasGloves, ppeResult.HasGoggles);

                // ★★★ 추가: UI 업데이트가 화면에 반영될 시간 확보 ★★★
                await Task.Delay(500);

                // 4. ★★★ 모든 작업자 기본 이미지 캡처 (PPE 착용 여부 무관) ★★★
                string workerId = recognizedWorker?.WorkerId ?? "UNKNOWN";

                bool alreadyCaptured = false;
                if (workerId != "UNKNOWN")
                {
                    alreadyCaptured = WorkerSessionManager.WorkersImageCap(workerId);
                }

                if (!alreadyCaptured)
                {
                    // 모든 작업자 기본 이미지 캡처
                    await WorkersCap.CaptureWorkerImage(frame.Clone());
                    Debug.WriteLine($"{workerId} 작업자 이미지 캡처 완료");

                    // 캡처 완료 마킹 (인식된 작업자만)
                    if (workerId != "UNKNOWN")
                    {
                        WorkerSessionManager.MarkAsCaptured(workerId);
                    }
                }
                else
                {
                    Debug.WriteLine($"{workerId}는 이미 캡처 완료됨. 기본 이미지 캡처 생략.");
                }

                // 5. ★★★ PPE 위반자라면 추가로 위반 이미지 캡처 ★★★
                bool hasViolation = !ppeResult.HasHelmet || !ppeResult.HasVest || !ppeResult.HasGloves || !ppeResult.HasGoggles;

                if (hasViolation)
                {
                    // 위반 이미지는 항상 캡처 (중복 체크 안 함)
                    await WorkersCap.CaptureViolationImage(frame.Clone());
                    Debug.WriteLine($"{workerId} PPE 위반 이미지 캡처 완료 (추가)");

                    // 이메일 알림 (쿨다운 체크)
                    if (DateTime.Now - lastEmailAlert > emailCooldown)
                    {
                        lastEmailAlert = DateTime.Now;
                        _ = SafetyAlert.ProcessViolation(workerId, !ppeResult.HasHelmet, !ppeResult.HasVest, !ppeResult.HasGloves, !ppeResult.HasGoggles);
                    }
                }

                // 6. MainWindow 카메라 중지
                MainWin.StopCamera();

                // 7. 작업자 출근 처리 (인식된 경우)
                if (recognizedWorker != null)
                {
                    if (!WorkerSessionManager.TryCheckIn(recognizedWorker.WorkerId))
                    {
                        MessageBox.Show($"{recognizedWorker.Name} 님은 이미 출근 처리되었습니다.\n");
                    }
                }

                // 8. WorkersInfo 창 준비 및 표시
                if (WorkersWin == null) WorkersWin = new WorkersInfo();
                SafetyCheckUI.WorkersWin = WorkersWin;

                MainWin.Hide();
                WorkersWin.Show();

                // 9. 새 창에 모든 정보(얼굴, PPE) 업데이트
                SafetyCheckUI.UpdateWorkersInfo(ppeResult.HasHelmet, ppeResult.HasVest, ppeResult.HasGloves, ppeResult.HasGoggles, recognizedWorker);
                Debug.WriteLine("=== Screen Switched to WorkersInfo ===");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"One-shot check 실패: {ex.Message}");
            }
            finally
            {
                frame?.Dispose();
            }
        }

        /// <summary>
        /// (신규 / 리팩토링) YOLO 모델을 실행하고 PPEResult 객체를 반환합니다.
        /// </summary>
        private static async Task<PPEResult> RunYoloCheckAsync(Mat frame)
        {
            bool hasHelmet = false, hasVest = false, hasGloves = false, hasGoggles = false;

            try
            {
                // 1. 이미지 전처리
                Mat resized = new Mat();
                Cv2.CvtColor(frame, resized, ColorConversionCodes.BGR2RGB);
                Cv2.Resize(resized, resized, new Size(640, 640));

                var inputTensor = new DenseTensor<float>(new[] { 1, 3, 640, 640 });
                var data = new float[3 * 640 * 640];
                int idx = 0;
                for (int c = 0; c < 3; c++)
                {
                    for (int y = 0; y < 640; y++)
                    {
                        for (int x = 0; x < 640; x++)
                        {
                            data[idx++] = resized.At<Vec3b>(y, x)[c] / 255.0f;
                        }
                    }
                }
                data.CopyTo(inputTensor.Buffer.Span);
                resized.Dispose();

                var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("images", inputTensor) };

                // 2. 추론 실행 (비동기)
                using var results = await Task.Run(() => session.Run(inputs));

                var output = results.First().AsEnumerable<float>().ToArray();
                int numBoxes = output.Length / 6;

                // 3. 객체 분류
                var persons = new List<(int x1, int y1, int x2, int y2, float score)>();
                var helmets = new List<(int x1, int y1, int x2, int y2)>();
                var vests = new List<(int x1, int y1, int x2, int y2)>();
                var gloves = new List<(int x1, int y1, int x2, int y2)>();
                var goggles = new List<(int x1, int y1, int x2, int y2)>();

                for (int i = 0; i < numBoxes; i++)
                {
                    float score = output[i * 6 + 4];
                    if (score < 0.3f) continue;

                    int x1 = (int)output[i * 6], y1 = (int)output[i * 6 + 1], x2 = (int)output[i * 6 + 2], y2 = (int)output[i * 6 + 3];
                    int label = (int)output[i * 6 + 5];

                    float scaleX = (float)frame.Width / 640f, scaleY = (float)frame.Height / 640f;
                    x1 = (int)(x1 * scaleX); y1 = (int)(y1 * scaleY); x2 = (int)(x2 * scaleX); y2 = (int)(y2 * scaleY);

                    switch (label)
                    {
                        case 0: helmets.Add((x1, y1, x2, y2)); break;
                        case 1: gloves.Add((x1, y1, x2, y2)); break;
                        case 2: vests.Add((x1, y1, x2, y2)); break;
                        case 4: goggles.Add((x1, y1, x2, y2)); break; // Goggles 라벨
                        case 6: persons.Add((x1, y1, x2, y2, score)); break;
                            // (다른 라벨들...)
                    }
                }

                // 4. PPE 착용 여부 판별 (첫 번째 감지된 사람 기준)
                if (persons.Count > 0)
                {
                    var person = persons.First(); // 가장 확률 높은 첫번째 사람 기준
                    hasHelmet = helmets.Any(h => RectOverlap(person.x1, person.y1, person.x2, person.y2, h.x1, h.y1, h.x2, h.y2));
                    hasVest = vests.Any(v => RectOverlap(person.x1, person.y1, person.x2, person.y2, v.x1, v.y1, v.x2, v.y2));
                    hasGloves = gloves.Any(g => RectOverlap(person.x1, person.y1, person.x2, person.y2, g.x1, g.y1, g.x2, g.y2));
                    hasGoggles = goggles.Any(g => RectOverlap(person.x1, person.y1, person.x2, person.y2, g.x1, g.y1, g.x2, g.y2));
                }
            }
            catch (Exception ex) { Debug.WriteLine($"YOLO check failed: {ex.Message}"); }
            finally
            {
                frame?.Dispose(); // 이 함수로 복제되어 넘어온 프레임 해제
            }

            return new PPEResult { HasHelmet = hasHelmet, HasVest = hasVest, HasGloves = hasGloves, HasGoggles = hasGoggles };
        }

        /// <summary>
        /// (신규) 위반 시 알림 및 이미지 캡처를 비동기로 처리합니다.
        /// </summary>
        private static async Task SendViolationAlertAsync(Mat frame, string workerId, PPEResult ppeResult)
        {
            try
            {
                bool shouldCapture = true;
                if (workerId != "UNKNOWN")
                {
                    if (WorkerSessionManager.WorkersImageCap(workerId))
                    {
                        Debug.WriteLine($"{workerId}는 이미 캡처 완료됨. 추가 캡처 생략.");
                        shouldCapture = false;
                    }
                    // MarkAsCaptured는 PerformOneShotCheckAsync에서 이미 처리됨
                }

                if (shouldCapture)
                {
                    // WorkersCap.CaptureViolationImage는 내부적으로 frame.Clone()을 사용합니다.
                    await WorkersCap.CaptureViolationImage(frame);
                    Debug.WriteLine($"{workerId} PPE 미착용 이미지 캡처 완료");
                }

                // (중요) SafetyAlert.cs의 ProcessViolation 함수 시그니처가 5개 인자를 받는지 확인 필요
                await SafetyAlert.ProcessViolation(workerId, !ppeResult.HasHelmet, !ppeResult.HasVest, !ppeResult.HasGloves, !ppeResult.HasGoggles);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"이메일 알림 처리 오류: {ex.Message}");
            }
        }


        // (유지) 사각형 겹침 여부 판단
        private static bool RectOverlap(int x1a, int y1a, int x2a, int y2a,
                                        int x1b, int y1b, int x2b, int y2b)
        {
            int x_overlap = Math.Max(0, Math.Min(x2a, x2b) - Math.Max(x1a, x1b));
            int y_overlap = Math.Max(0, Math.Min(y2a, y2b) - Math.Max(y1a, y1b));
            return x_overlap > 10 && y_overlap > 10;
        }

        // (유지) 일일 세션 초기화
        public static void StartDailyResetTimer()
        {
            // WorkersInfo.xaml.cs에서 이미 타이머를 관리하고 있다면 이쪽은 필요 없을 수 있습니다.
            // 하지만 중복 호출되어도 큰 문제는 없습니다.
            var timer = new Timer(_ =>
            {
                var now = DateTime.Now;
                if (now.Hour == 0 && now.Minute == 0)
                {
                    WorkerSessionManager.ResetDailySessions();
                }
            }, null, TimeSpan.Zero, TimeSpan.FromMinutes(1));
        }

        // (유지) 리소스 정리
        public static void Cleanup()
        {
            session?.Dispose();
        }
    }
}