using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;

namespace finalProject.Models
{
    public class ROIProcessor
    {
        private readonly ImageProcessor _imageProcessor;
        private readonly VisionProcessor _visionProcessor;
        private readonly string _savePath;
        private readonly object _frameLock = new object();

        public bool LastInspectionResult { get; private set; } = true;
        public List<string> LastDetectedDefects { get; private set; } = new List<string>();

        // 로그 메시지를 외부로 전달하기 위한 이벤트
        public event Action<string> LogMessageRequested;

        // 이진화 이미지를 UI에 표시하기 위한 이벤트
        public event Action<BitmapSource> BinarizedImageUpdated;

        public int LastTotalDefectCount { get; private set; } = 0;

        // ⭐ 불량 개수 추적을 위한 리스트
        public List<int> DefectCountHistory { get; private set; } = new List<int>();

        // ⭐ 불량 개수 히스토리 업데이트 이벤트
        public event Action<List<int>> DefectCountHistoryUpdated;

        public ROIProcessor(string savePath, string onnxModelPath)
        {
            _savePath = savePath;
            _imageProcessor = new ImageProcessor();
            _visionProcessor = new VisionProcessor(onnxModelPath);

            // 저장 폴더 생성
            Directory.CreateDirectory(_savePath);
        }

        /// <summary>
        /// 센서1: 불량 유무 검출
        /// </summary>
        public void ProcessROI_Sensor1(Bitmap currentFrame)
        {
            if (currentFrame == null)
            {
                LogMessageRequested?.Invoke("캡처할 카메라 프레임이 없습니다.");
                return;
            }

            Bitmap roiBitmap = null;
            Bitmap binarizedBitmap = null;

            try
            {
                // ROI 영역 추출
                roiBitmap = ExtractROI(currentFrame);
                if (roiBitmap == null)
                {
                    LogMessageRequested?.Invoke("ROI 추출 실패");
                    return;
                }

                // 타임스탬프 생성
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmssfff");

                // 원본 이미지 저장
                string originalFileName = $"{timestamp}_sensor1.png";
                string originalFilePath = Path.Combine(_savePath, originalFileName);
                roiBitmap.Save(originalFilePath, ImageFormat.Png);

                // 이진화 이미지 처리
                binarizedBitmap = _imageProcessor.ProcessForPrediction(roiBitmap);

                // 이진화 이미지 저장
                string binaryFileName = $"{timestamp}_sensor1_binary.png";
                string binaryFilePath = Path.Combine(_savePath, binaryFileName);
                binarizedBitmap.Save(binaryFilePath, ImageFormat.Png);

                // 이진화 이미지를 UI에 표시
                BinarizedImageUpdated?.Invoke(BitmapToBitmapSource(binarizedBitmap));

                // 불량 탐지 (원본 ROI 이미지 사용)
                (var detections, _) = _visionProcessor.Predict(roiBitmap);

                // 결과 판정
                if (detections == null || detections.Count == 0)
                {
                    LastInspectionResult = true;
                    LastDetectedDefects = new List<string>(); // ⭐ 정상일 때 빈 리스트
                    LastTotalDefectCount = 0;

                    // ⭐ 정상 제품도 0개로 기록
                    DefectCountHistory.Add(0);

                    LogMessageRequested?.Invoke("[센서1] 결과 : 정상");
                }
                else
                {
                    LastInspectionResult = false;
                    LastTotalDefectCount = detections.Count;

                    // ⭐ 불량 개수 히스토리에 추가
                    DefectCountHistory.Add(LastTotalDefectCount);

                    var defectCounts = detections.GroupBy(d => d.Label)
                                                 .ToDictionary(g => g.Key, g => g.Count());

                    // 불량 종류 리스트 (중복 제거)
                    LastDetectedDefects = defectCounts.Keys.ToList();

                    LogMessageRequested?.Invoke($"[센서1] 결과 : 불량 (총 {LastTotalDefectCount}개)");

                    foreach (var defect in defectCounts)
                    {
                        LogMessageRequested?.Invoke($"   - {defect.Key}: {defect.Value}개");
                    }
                }

                // ⭐ 불량 개수 히스토리 업데이트 이벤트 발생
                DefectCountHistoryUpdated?.Invoke(new List<int>(DefectCountHistory));
            }
            catch (Exception ex)
            {
                LogMessageRequested?.Invoke($"[센서1] 이미지 처리 오류: {ex.Message}");
                // ⭐ 예외 발생 시에도 초기화
                LastDetectedDefects = new List<string>();
            }
            finally
            {
                // 메모리 해제
                roiBitmap?.Dispose();
                binarizedBitmap?.Dispose();
            }
        }

        /// <summary>
        /// 센서2: 불량 종류 상세 분석
        /// </summary>
        public void ProcessROI_Sensor2(Bitmap currentFrame)
        {
            if (currentFrame == null)
            {
                LogMessageRequested?.Invoke("캡처할 카메라 프레임이 없습니다.");
                return;
            }

            Bitmap roiBitmap = null;
            Bitmap binarizedBitmap = null;

            try
            {
                // ROI 영역 추출
                roiBitmap = ExtractROI(currentFrame);
                if (roiBitmap == null)
                {
                    LogMessageRequested?.Invoke("ROI 추출 실패");
                    return;
                }

                // 타임스탬프 생성
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmssfff");

                // 원본 이미지 저장
                string originalFileName = $"{timestamp}_sensor2.png";
                string originalFilePath = Path.Combine(_savePath, originalFileName);
                roiBitmap.Save(originalFilePath, ImageFormat.Png);

                // 이진화 이미지 처리
                binarizedBitmap = _imageProcessor.ProcessForPrediction(roiBitmap);

                // 이진화 이미지 저장
                string binaryFileName = $"{timestamp}_sensor2_binary.png";
                string binaryFilePath = Path.Combine(_savePath, binaryFileName);
                binarizedBitmap.Save(binaryFilePath, ImageFormat.Png);

                // 이진화 이미지를 UI에 표시
                BinarizedImageUpdated?.Invoke(BitmapToBitmapSource(binarizedBitmap));

                // 불량 탐지 (원본 ROI 이미지 사용)
                (var detections, _) = _visionProcessor.Predict(roiBitmap);

                // 결과 분석
                if (detections == null || detections.Count == 0)
                {
                    LastInspectionResult = true;
                    LastTotalDefectCount = 0;

                    // ⭐ 정상 제품도 0개로 기록
                    DefectCountHistory.Add(0);
                }
                else
                {
                    LastInspectionResult = false;
                    LastTotalDefectCount = detections.Count;

                    // ⭐ 불량 개수 히스토리에 추가
                    DefectCountHistory.Add(LastTotalDefectCount);

                    LogMessageRequested?.Invoke($"[센서2] 불량 탐지! (총 {detections.Count}개)");

                    // 불량 종류별 카운트
                    var defectCounts = detections.GroupBy(d => d.Label)
                                                 .ToDictionary(g => g.Key, g => g.Count());

                    // 불량 종류 저장 (pin-hole 포함 여부 확인용)
                    LastDetectedDefects = defectCounts.Keys.ToList();

                    // 불량 종류별 로그 출력
                    foreach (var defect in defectCounts)
                    {
                        LogMessageRequested?.Invoke($"   - {defect.Key}: {defect.Value}개");
                    }
                }

                // ⭐ 불량 개수 히스토리 업데이트 이벤트 발생
                DefectCountHistoryUpdated?.Invoke(new List<int>(DefectCountHistory));
            }
            catch (Exception ex)
            {
                LogMessageRequested?.Invoke($"[센서2] 이미지 처리 오류: {ex.Message}");
            }
            finally
            {
                // 메모리 해제
                roiBitmap?.Dispose();
                binarizedBitmap?.Dispose();
            }
        }

        /// <summary>
        /// ROI 영역 추출 (중앙 320x320)
        /// </summary>
        private Bitmap ExtractROI(Bitmap currentFrame)
        {
            if (currentFrame == null)
            {
                LogMessageRequested?.Invoke("캡처할 카메라 프레임이 없습니다.");
                return null;
            }

            int roiSize = 320;
            int x = (currentFrame.Width - roiSize) / 2;
            int y = (currentFrame.Height - roiSize) / 2;
            var roiRect = new Rectangle(x, y, roiSize, roiSize);

            return currentFrame.Clone(roiRect, currentFrame.PixelFormat);
        }

        /// <summary>
        /// 원본 이미지 저장
        /// </summary>
        private void SaveOriginalImage(Bitmap roiBitmap, string timestamp, string suffix = "")
        {
            string originalFileName = $"{timestamp}{suffix}.png";
            string originalFilePath = Path.Combine(_savePath, originalFileName);
            roiBitmap.Save(originalFilePath, ImageFormat.Png);
            LogMessageRequested?.Invoke($"원본 이미지 저장: {originalFilePath}");
        }

        /// <summary>
        /// 이진화 이미지 처리 및 저장
        /// </summary>
        private Bitmap ProcessAndSaveBinarized(Bitmap roiBitmap, string timestamp, string suffix = "")
        {
            Bitmap binarizedBitmap = _imageProcessor.ProcessForPrediction(roiBitmap);

            string binaryFileName = $"{timestamp}{suffix}_binary.png";
            string binaryFilePath = Path.Combine(_savePath, binaryFileName);
            binarizedBitmap.Save(binaryFilePath, ImageFormat.Png);
            LogMessageRequested?.Invoke($"이진화 이미지 저장: {binaryFilePath}");

            return binarizedBitmap;
        }

        /// <summary>
        /// ⭐ 불량 개수 히스토리 초기화
        /// </summary>
        public void ClearDefectCountHistory()
        {
            DefectCountHistory.Clear();
            DefectCountHistoryUpdated?.Invoke(new List<int>(DefectCountHistory));
        }

        /// <summary>
        /// System.Drawing.Bitmap을 WPF BitmapSource로 변환
        /// </summary>
        public static BitmapSource BitmapToBitmapSource(Bitmap bitmap)
        {
            using (var memory = new MemoryStream())
            {
                bitmap.Save(memory, ImageFormat.Bmp);
                memory.Position = 0;
                var bitmapImage = new BitmapImage();
                bitmapImage.BeginInit();
                bitmapImage.StreamSource = memory;
                bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                bitmapImage.EndInit();
                bitmapImage.Freeze();
                return bitmapImage;
            }
        }
    }
}