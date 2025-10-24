using finalProject.Models;
using Microsoft.Win32;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Window = System.Windows.Window;
using IoPath = System.IO.Path;
using ShapePath = System.Windows.Shapes.Path;
using Size = System.Windows.Size;
using Point = System.Windows.Point;

namespace finalProject.Views
{
    public partial class ResultDashboard : Window
    {
        private DispatcherTimer updateTimer;
        private FactoryIOControl factoryControl;

        private bool _isClosing = false;

        // ⭐ ObservableCollection 사용으로 깜빡임 방지
        private ObservableCollection<RecentResultItem> recentResultsList;

        public ResultDashboard(FactoryIOControl factory)
        {
            InitializeComponent();

            factoryControl = factory;

            // ⭐ ObservableCollection 초기화
            recentResultsList = new ObservableCollection<RecentResultItem>();
            GridRecent.ItemsSource = recentResultsList;

            // ⭐ DataGrid 열 자동 생성 이벤트 (고정 너비 설정)
            GridRecent.AutoGeneratingColumn += GridRecent_AutoGeneratingColumn;

            // FactoryIOControl의 통계 업데이트 이벤트 구독
            factoryControl.OnStatisticsUpdated += UpdateStatisticsUI;

            // 주기적 UI 업데이트 타이머 (1초마다)
            updateTimer = new DispatcherTimer();
            updateTimer.Interval = TimeSpan.FromSeconds(1);
            updateTimer.Tick += UpdateTimer_Tick;
            updateTimer.Start();

            // 초기 데이터 로드
            LoadInitialData();

            // Export 버튼 이벤트 연결
            BtnExport.Click += BtnExport_Click;

            // 창 종료 이벤트 핸들러
            this.Closing += ResultDashboard_Closing;
        }

        /// <summary>
        /// ⭐ DataGrid 열 너비를 고정값으로 설정 (픽셀 단위)
        /// </summary>
        private void GridRecent_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
        {
            // 각 속성명에 따라 고정 너비 설정
            switch (e.PropertyName)
            {
                case "Time":
                    e.Column.Width = 80; // 시간 열: 100px
                    e.Column.Header = "Time";
                    break;
                case "Count":
                    e.Column.Width = 60; // 타입 열: 100px
                    e.Column.Header = "Count";
                    break;
                case "Result":
                    e.Column.Width = 60; // 결과 열: 80px
                    e.Column.Header = "Result";
                    break;
                case "Type":
                    e.Column.Width = new DataGridLength(1, DataGridLengthUnitType.Star); // 나머지 공간
                    e.Column.Header = "Type";
                    break;
            }
        }

        private void LoadInitialData()
        {
            if (factoryControl != null)
            {
                var stats = factoryControl.GetCurrentStatistics();
                UpdateStatisticsUI(stats);
            }
        }

        private void UpdateTimer_Tick(object sender, EventArgs e)
        {
            // 주기적으로 통계 갱신
            if (factoryControl != null)
            {
                var stats = factoryControl.GetCurrentStatistics();
                UpdateStatisticsUI(stats);
            }
        }

        /// <summary>
        /// 통계 데이터를 받아서 UI 업데이트
        /// </summary>
        public void UpdateStatisticsUI(InspectionStatistics stats)
        {
            if (stats == null) return;

            // ⭐ UI 업데이트를 Dispatcher로 비동기 처리 (깜빡임 방지)
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // KPI 업데이트
                TxtTotal.Text = stats.TotalInspected.ToString();
                TxtNg.Text = stats.DefectCount.ToString();
                TxtConf.Text = $"{stats.NormalRate:F1}%";
                TxtRate.Text = $"{stats.DefectRate:F1}%";

                // Pie Chart 데이터 업데이트
                UpdatePieChart(stats, PieCanvas);
                UpdateDefectCountChart(stats, PieCanvas2);

                // Line Chart 데이터 업데이트
                DrawDefectRateChart();

                // 최근 결과 테이블 업데이트
                UpdateRecentResults(stats);

            }), DispatcherPriority.Background); // ⭐ 낮은 우선순위로 처리
        }

        private void ResultDashboard_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_isClosing) return;
            _isClosing = true;

            try
            {
                Debug.WriteLine("대시 보드 WPF 닫는 중...");

                if (factoryControl != null)
                {
                    factoryControl.StopFactoryIOSystem();
                    Debug.WriteLine("컨베이어 정지 및 PLC 초기화 완료");

                    factoryControl.ActualClose();
                    Debug.WriteLine("PCB 분석 Vision WPF 종료");
                }

                Debug.WriteLine(
                    "작업이 안전하게 종료되었습니다.\n\n" +
                    "- 컨베이어: 정지\n" +
                    "- PLC 신호: 초기화",
                    "작업 종료"
                );
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"대시 보드 WPF 종료 중 오류: {ex.Message}");
            }
            finally
            {
                // ⭐ Application.Current.Shutdown() 대신 Environment.Exit() 사용
                Environment.Exit(0);
            }
        }

        /// <summary>
        /// ⭐ 최근 검사 결과 테이블 업데이트 (ObservableCollection 사용으로 깜빡임 방지)
        /// </summary>
        private void UpdateRecentResults(InspectionStatistics stats)
        {
            if (stats?.RecentResults == null) return;

            // 최근 10개만 가져오기
            var newData = stats.RecentResults.Take(10).ToList();

            // ⭐ 기존 데이터와 비교하여 변경사항이 있을 때만 업데이트
            if (recentResultsList.Count == newData.Count)
            {
                bool hasChanges = false;
                for (int i = 0; i < newData.Count; i++)
                {
                    if (recentResultsList[i].Time != newData[i].Time.ToString("HH:mm:ss") ||
                        recentResultsList[i].Type != newData[i].Type ||
                        recentResultsList[i].Result != newData[i].Result)
                    {
                        hasChanges = true;
                        break;
                    }
                }

                if (!hasChanges) return; // 변경사항 없으면 업데이트 안 함
            }

            // ⭐ ObservableCollection 업데이트 (Clear + AddRange)
            recentResultsList.Clear();
            foreach (var r in newData)
            {
                recentResultsList.Add(new RecentResultItem
                {
                    Time = r.Time.ToString("HH:mm:ss"),
                    Type = r.Type,
                    Result = r.Result,
                    Count = r.DefectCount > 0 ? r.DefectCount.ToString() : "-"
                });
            }
        }

        /// <summary>
        /// 불량 유형별 파이 차트 업데이트
        /// </summary>
        private void UpdatePieChart(InspectionStatistics stats, Canvas targetCanvas)
        {
            // targetCanvas를 클리어
            targetCanvas.Children.Clear();

            if (stats.TotalInspected == 0) return;

            double canvasWidth = 250;
            double canvasHeight = 250;
            double centerX = canvasWidth / 2;
            double centerY = canvasHeight / 2;
            double outerRadius = Math.Min(canvasWidth, canvasHeight) / 2 - 20;
            double innerRadius = outerRadius * 0.70;

            // 불량이 있는 유형만 필터링
            var defectTypes = new[]
            {
                new { Name = "short", Count = stats.DefectTypeCount["short"], Color = "#3DA5FF" },
                new { Name = "mousebite", Count = stats.DefectTypeCount["mousebite"], Color = "#FF6B6B" },
                new { Name = "pin-hole", Count = stats.DefectTypeCount["pin-hole"], Color = "#5EE493" },
                new { Name = "spur", Count = stats.DefectTypeCount["spur"], Color = "#F0E06E" },
                new { Name = "open", Count = stats.DefectTypeCount["open"], Color = "#D498AD" },
                new { Name = "copper", Count = stats.DefectTypeCount["copper"], Color = "#A6A0D8" }
            }.Where(d => d.Count > 0).ToArray(); // ⭐ 카운트가 0보다 큰 것만

            // 실제 불량 개수의 합계로 계산
            int totalDefects = defectTypes.Sum(d => d.Count);

            if (totalDefects == 0)
            {
                // 불량이 없으면 빈 원 표시
                return;
            }

            double startAngle = -90; // 12시 방향부터 시작

            foreach (var defect in defectTypes)
            {
                double sweepAngle = (double)defect.Count / totalDefects * 360;

                // 도넛 조각 그리기
                var donut = CreateDonutSlice(centerX, centerY, outerRadius, innerRadius, startAngle, sweepAngle, defect.Color);
                targetCanvas.Children.Add(donut);

                startAngle += sweepAngle;
            }
        }

        /// <summary>
        /// 불량 개수별 파이 차트 업데이트
        /// </summary>
        private void UpdateDefectCountChart(InspectionStatistics stats, Canvas targetCanvas)
        {
            targetCanvas.Children.Clear();

            // ⭐ 디버그: 불량 개수 출력
            Debug.WriteLine($"📊 불량 개수 차트 - DefectCount: {stats.DefectCount}");

            if (stats.DefectCount == 0) return;

            double canvasWidth = 250;
            double canvasHeight = 250;
            double centerX = canvasWidth / 2;
            double centerY = canvasHeight / 2;
            double outerRadius = Math.Min(canvasWidth, canvasHeight) / 2 - 20;
            double innerRadius = outerRadius * 0.70;

            // ⭐ 디버그: 범위별 개수 출력
            Debug.WriteLine($"   1-2: {stats.DefectCountRange["1-2"]}");
            Debug.WriteLine($"   3-4: {stats.DefectCountRange["3-4"]}");
            Debug.WriteLine($"   5-6: {stats.DefectCountRange["5-6"]}");
            Debug.WriteLine($"   7+: {stats.DefectCountRange["7+"]}");

            // 불량 개수 범위별 분류
            var ranges = new[]
            {
                new { Name = "1-2 defects", Count = stats.DefectCountRange["1-2"], Color = "#789DBC" },
                new { Name = "3-4 defects", Count = stats.DefectCountRange["3-4"], Color = "#D6A99D" },
                new { Name = "5-6 defects", Count = stats.DefectCountRange["5-6"], Color = "#FBF3D5" },
                new { Name = "7+ defects", Count = stats.DefectCountRange["7+"], Color = "#9CAFAA" }
            }.Where(r => r.Count > 0).ToArray();

            int totalCount = ranges.Sum(r => r.Count);

            // ⭐ 디버그: 필터링 후 총 개수
            Debug.WriteLine($"   필터링 후 totalCount: {totalCount}");

            if (totalCount == 0)
            {
                Debug.WriteLine("   ⚠️ totalCount가 0이어서 그래프 안 그림!");
                return;
            }

            double startAngle = -90;

            foreach (var range in ranges)
            {
                double sweepAngle = (double)range.Count / totalCount * 360;

                var donut = CreateDonutSlice(centerX, centerY, outerRadius, innerRadius, startAngle, sweepAngle, range.Color);
                targetCanvas.Children.Add(donut);

                startAngle += sweepAngle;
            }
        }

        /// <summary>
        /// 도넛 조각 생성
        /// </summary>
        private ShapePath CreateDonutSlice(double centerX, double centerY,
            double outerRadius, double innerRadius, double startAngle, double sweepAngle, string colorHex)
        {
            if (sweepAngle <= 0) return null;

            // ⭐ 360도 전체 도넛인 경우 특별 처리 ⭐
            if (sweepAngle >= 359.9)
            {
                return CreateFullDonut(centerX, centerY, outerRadius, innerRadius, colorHex);
            }

            double startRad = startAngle * Math.PI / 180;
            double endRad = (startAngle + sweepAngle) * Math.PI / 180;

            // 외곽 호
            Point outerStart = new Point(centerX + outerRadius * Math.Cos(startRad), centerY + outerRadius * Math.Sin(startRad));
            Point outerEnd = new Point(centerX + outerRadius * Math.Cos(endRad), centerY + outerRadius * Math.Sin(endRad));

            // 내부 호
            Point innerStart = new Point(centerX + innerRadius * Math.Cos(startRad), centerY + innerRadius * Math.Sin(startRad));
            Point innerEnd = new Point(centerX + innerRadius * Math.Cos(endRad), centerY + innerRadius * Math.Sin(endRad));

            bool isLargeArc = sweepAngle > 180;

            PathFigure figure = new PathFigure { StartPoint = outerStart };
            figure.Segments.Add(new ArcSegment
            {
                Point = outerEnd,
                Size = new Size(outerRadius, outerRadius),
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = isLargeArc
            });
            figure.Segments.Add(new LineSegment { Point = innerEnd });
            figure.Segments.Add(new ArcSegment
            {
                Point = innerStart,
                Size = new Size(innerRadius, innerRadius),
                SweepDirection = SweepDirection.Counterclockwise,
                IsLargeArc = isLargeArc
            });
            figure.IsClosed = true;

            PathGeometry geometry = new PathGeometry();
            geometry.Figures.Add(figure);

            var path = new ShapePath
            {
                Data = geometry,
                Fill = (Brush)new BrushConverter().ConvertFrom(colorHex),
                Stroke = Brushes.Transparent,
                StrokeThickness = 0
            };

            return path;
        }

        /// <summary>
        /// ⭐ 360도 전체 도넛 생성 (단일 범위가 100%일 때) ⭐
        /// </summary>
        private ShapePath CreateFullDonut(double centerX, double centerY,
            double outerRadius, double innerRadius, string colorHex)
        {
            // 두 개의 반원을 합쳐서 완전한 원 생성
            PathFigure figure = new PathFigure
            {
                StartPoint = new Point(centerX + outerRadius, centerY)
            };

            // 외곽 반원 1
            figure.Segments.Add(new ArcSegment
            {
                Point = new Point(centerX - outerRadius, centerY),
                Size = new Size(outerRadius, outerRadius),
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = true
            });

            // 외곽 반원 2
            figure.Segments.Add(new ArcSegment
            {
                Point = new Point(centerX + outerRadius, centerY),
                Size = new Size(outerRadius, outerRadius),
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = true
            });

            // 내부 원으로 연결
            figure.Segments.Add(new LineSegment { Point = new Point(centerX + innerRadius, centerY) });

            // 내부 반원 1 (반대 방향)
            figure.Segments.Add(new ArcSegment
            {
                Point = new Point(centerX - innerRadius, centerY),
                Size = new Size(innerRadius, innerRadius),
                SweepDirection = SweepDirection.Counterclockwise,
                IsLargeArc = true
            });

            // 내부 반원 2 (반대 방향)
            figure.Segments.Add(new ArcSegment
            {
                Point = new Point(centerX + innerRadius, centerY),
                Size = new Size(innerRadius, innerRadius),
                SweepDirection = SweepDirection.Counterclockwise,
                IsLargeArc = true
            });

            figure.IsClosed = true;

            PathGeometry geometry = new PathGeometry();
            geometry.Figures.Add(figure);

            var path = new ShapePath
            {
                Data = geometry,
                Fill = (Brush)new BrushConverter().ConvertFrom(colorHex),
                Stroke = Brushes.Transparent,
                StrokeThickness = 0
            };

            return path;
        }

        /// <summary>
        /// ⭐ 시간대별 불량률 라인 차트 그리기 (테스트용) ⭐
        /// </summary>
        private void DrawDefectRateChart()
        {
            if (factoryControl == null) return;

            var stats = factoryControl.GetCurrentStatistics();
            if (stats?.DefectRateHistory == null) return;

            LineChart.Children.Clear();

            // 축 그리기
            Line yAxis = new Line
            {
                X1 = 40,
                Y1 = 10,
                X2 = 40,
                Y2 = 230,
                Stroke = new SolidColorBrush(Color.FromRgb(35, 42, 54)),
                StrokeThickness = 2
            };
            Line xAxis = new Line
            {
                X1 = 40,
                Y1 = 230,
                X2 = 760,
                Y2 = 230,
                Stroke = new SolidColorBrush(Color.FromRgb(35, 42, 54)),
                StrokeThickness = 2
            };
            LineChart.Children.Add(yAxis);
            LineChart.Children.Add(xAxis);

            var data = stats.DefectRateHistory;

            // ⭐ 테스트용: 데이터가 1개만 있어도 표시 ⭐
            if (data.Count < 1) return; // 데이터가 하나도 없으면 return

            // Y축 눈금선과 레이블 먼저 그리기
            double chartHeight = 220.0;
            double maxRate = data.Count > 0 ? Math.Max(data.Max(d => d.Rate), 10) : 10;

            for (int i = 0; i <= 5; i++)
            {
                double y = 230 - (i * chartHeight / 5);
                double rateValue = (i * maxRate / 5);

                // 눈금선
                Line gridLine = new Line
                {
                    X1 = 40,
                    Y1 = y,
                    X2 = 760,
                    Y2 = y,
                    Stroke = new SolidColorBrush(Color.FromRgb(35, 42, 54)),
                    StrokeThickness = 0.5,
                    StrokeDashArray = new DoubleCollection { 2, 2 }
                };
                LineChart.Children.Add(gridLine);

                // Y축 레이블
                TextBlock label = new TextBlock
                {
                    Text = $"{rateValue:F0}%",
                    Foreground = new SolidColorBrush(Color.FromRgb(152, 162, 179)),
                    FontSize = 10
                };
                Canvas.SetLeft(label, 5);
                Canvas.SetTop(label, y - 8);
                LineChart.Children.Add(label);
            }

            // 데이터가 1개만 있으면 포인트만 표시
            if (data.Count == 1)
            {
                double yScale = chartHeight / maxRate;
                double x = 40;
                double y = 230 - (data[0].Rate * yScale);

                Ellipse point = new Ellipse
                {
                    Width = 8,
                    Height = 8,
                    Fill = new SolidColorBrush(Color.FromRgb(255, 107, 107))
                };
                Canvas.SetLeft(point, x - 4);
                Canvas.SetTop(point, y - 4);
                LineChart.Children.Add(point);

                // 디버그 출력
                Debug.WriteLine($"📊 라인차트: 데이터 1개 - Rate={data[0].Rate}%");
                return;
            }

            // 그래프 영역 설정
            double chartWidth = 720.0;
            double xStep = chartWidth / Math.Max(data.Count - 1, 1);
            double yScale2 = chartHeight / maxRate;

            // 선 그리기
            for (int i = 0; i < data.Count - 1; i++)
            {
                double x1 = 40 + (i * xStep);
                double y1 = 230 - (data[i].Rate * yScale2);
                double x2 = 40 + ((i + 1) * xStep);
                double y2 = 230 - (data[i + 1].Rate * yScale2);

                Line line = new Line
                {
                    X1 = x1,
                    Y1 = y1,
                    X2 = x2,
                    Y2 = y2,
                    Stroke = new SolidColorBrush(Color.FromRgb(255, 107, 107)),
                    StrokeThickness = 2
                };
                LineChart.Children.Add(line);
            }

            // 포인트 표시
            for (int i = 0; i < data.Count; i++)
            {
                double x = 40 + (i * xStep);
                double y = 230 - (data[i].Rate * yScale2);

                Ellipse point = new Ellipse
                {
                    Width = 6,
                    Height = 6,
                    Fill = new SolidColorBrush(Color.FromRgb(255, 107, 107))
                };

                Canvas.SetLeft(point, x - 3);
                Canvas.SetTop(point, y - 3);
                LineChart.Children.Add(point);
            }

            // 디버그 출력
            Debug.WriteLine($"📊 라인차트: {data.Count}개 데이터 표시됨");
        }

        /// <summary>
        /// Export CSV 버튼 클릭 이벤트
        /// </summary>
        private void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            if (factoryControl == null) return;

            var stats = factoryControl.GetCurrentStatistics();

            // 날짜 필터 가져오기
            DateTime? fromDate = FromDate.SelectedDate;
            DateTime? toDate = ToDate.SelectedDate;

            // 제품 필터 가져오기 (ComboBox에서 선택된 항목)
            string selectedProduct = null;
            if (ProductFilter.SelectedItem is ComboBoxItem selectedItem)
            {
                string content = selectedItem.Content?.ToString();
                // "All"이 아닌 경우에만 필터로 사용
                if (content != "All")
                {
                    selectedProduct = content;
                }
            }

            // CSV 파일 저장 다이얼로그
            var saveDialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "CSV 파일 (*.csv)|*.csv",
                FileName = $"PCB_Inspection_Report_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
            };

            if (saveDialog.ShowDialog() == true)
            {
                ExportToCSV(stats, saveDialog.FileName, fromDate, toDate, selectedProduct);
                MessageBox.Show("CSV 파일이 저장되었습니다.", "저장 완료",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        /// <summary>
        /// CSV 파일로 내보내기 (필터 적용)
        /// </summary>
        private void ExportToCSV(InspectionStatistics stats, string filePath, DateTime? fromDate, DateTime? toDate, string selectedProduct)
        {
            var csv = new System.Text.StringBuilder();

            csv.AppendLine("PCB Defect Inspection Report");
            csv.AppendLine($"생성일시,{DateTime.Now:yyyy-MM-dd HH:mm:ss}");

            // 필터 정보 추가
            if (fromDate.HasValue || toDate.HasValue || !string.IsNullOrEmpty(selectedProduct))
            {
                csv.AppendLine();
                csv.AppendLine("=== 적용된 필터 ===");
                if (fromDate.HasValue)
                    csv.AppendLine($"시작 날짜,{fromDate.Value:yyyy-MM-dd}");
                if (toDate.HasValue)
                    csv.AppendLine($"종료 날짜,{toDate.Value:yyyy-MM-dd}");
                if (!string.IsNullOrEmpty(selectedProduct))
                    csv.AppendLine($"불량 유형 필터,{selectedProduct}");
            }

            csv.AppendLine();

            csv.AppendLine("=== 전체 통계 ===");
            csv.AppendLine("항목,값");
            csv.AppendLine($"총 검사 수,{stats.TotalInspected}");
            csv.AppendLine($"정상 제품,{stats.NormalCount}");
            csv.AppendLine($"불량 제품,{stats.DefectCount}");
            csv.AppendLine($"정상률,{stats.NormalRate}%");
            csv.AppendLine($"불량률,{stats.DefectRate}%");
            csv.AppendLine();

            csv.AppendLine("=== 불량 유형별 통계 ===");
            csv.AppendLine("불량 유형,발생 횟수,비율");

            foreach (var kvp in stats.DefectTypeCount)
            {
                double rate = stats.GetDefectTypeRate(kvp.Key);
                csv.AppendLine($"{kvp.Key},{kvp.Value},{rate}%");
            }

            csv.AppendLine();
            csv.AppendLine("=== 최근 검사 결과 (필터 적용) ===");
            csv.AppendLine("시간,유형,결과,불량개수");

            // 필터링 로직 적용
            var filteredResults = stats.RecentResults.AsEnumerable();

            // 날짜 필터링
            if (fromDate.HasValue)
            {
                filteredResults = filteredResults.Where(r => r.Time.Date >= fromDate.Value.Date);
            }
            if (toDate.HasValue)
            {
                filteredResults = filteredResults.Where(r => r.Time.Date <= toDate.Value.Date);
            }

            // 제품 타입 필터링 (Type 컬럼에 선택된 불량 타입이 포함되어 있는지 확인)
            if (!string.IsNullOrEmpty(selectedProduct))
            {
                filteredResults = filteredResults.Where(r =>
                    !string.IsNullOrEmpty(r.Type) &&
                    r.Type.Contains(selectedProduct, StringComparison.OrdinalIgnoreCase));
            }

            // CSV 출력 (Type을 큰따옴표로 감싸서 한 셀에 표시)
            foreach (var result in filteredResults.Take(50))
            {
                csv.AppendLine($"{result.Time:yyyy-MM-dd HH:mm:ss},\"{result.Type}\",{result.Result},{result.DefectCount}");
            }

            System.IO.File.WriteAllText(filePath, csv.ToString(), System.Text.Encoding.UTF8);
        }

        /// <summary>
        /// Vision 버튼 클릭 이벤트
        /// </summary>
        private void BtnCamera_Click(object sender, RoutedEventArgs e)
        {
            // FactoryIOControl 창 표시
            factoryControl?.ShowWindow();
        }

        /// <summary>
        /// 창이 닫힐 때
        /// </summary>
        protected override void OnClosed(EventArgs e)
        {
            updateTimer?.Stop();

            // 이벤트 구독 해제
            if (factoryControl != null)
            {
                factoryControl.OnStatisticsUpdated -= UpdateStatisticsUI;
            }

            base.OnClosed(e);
        }
    }

    // ⭐ DataGrid 바인딩용 클래스 추가
    public class RecentResultItem
    {
        public string Time { get; set; }
        public string Type { get; set; }
        public string Result { get; set; }
        public string Count { get; set; }
    }
}