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

        public ResultDashboard(FactoryIOControl factory)
        {
            InitializeComponent();

            factoryControl = factory;

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
        }

        private void LoadInitialData()
        {
            // DataGrid 열 너비 설정
            ConfigureDataGridColumns();
    
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

            // KPI 업데이트
            TxtTotal.Text = stats.TotalInspected.ToString();
            TxtNg.Text = stats.DefectCount.ToString();
            TxtConf.Text = $"{stats.NormalRate}%";
            TxtRate.Text = $"{stats.DefectRate}%";

            // Pie Chart 데이터 업데이트
            UpdatePieChart(stats, PieCanvas);
            UpdateDefectCountChart(stats, PieCanvas2);

            // Line Chart 데이터 업데이트
            DrawDefectRateChart();

            // 최근 결과 테이블 업데이트
            UpdateRecentResults(stats);

            // 창 종료 이벤트 핸들러
            this.Closing += ResultDashboard_Closing;
        }

        private void ResultDashboard_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_isClosing) return;
            _isClosing = true;

            try
            {
                Debug.WriteLine("🛑 대시보드 창 닫는 중...");

                if (factoryControl != null)
                {
                    factoryControl.StopFactoryIOSystem();
                    Debug.WriteLine("✅ 컨베이어 정지 및 PLC 초기화 완료");
                }

                MessageBox.Show(
                    "✅ 작업이 안전하게 종료되었습니다.\n\n" +
                    "- 컨베이어: 정지\n" +
                    "- PLC 신호: 초기화",
                    "작업 종료",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"⚠ 대시보드 종료 중 오류: {ex.Message}");
            }
            finally
            {
                // ⭐ Application.Current.Shutdown() 대신 Environment.Exit() 사용
                Environment.Exit(0);
            }
        }

        /// <summary>
        /// 최근 검사 결과 테이블 업데이트
        /// </summary>
        private void UpdateRecentResults(InspectionStatistics stats)
        {
            if (stats?.RecentResults == null) return;

            // 최근 10개만 표시
            var recentData = stats.RecentResults.Take(10).Select(r => new
            {
                Time = r.Time.ToString("HH:mm:ss"),
                Type = r.Type,
                Result = r.Result,
                Count = r.DefectCount > 0 ? r.DefectCount.ToString() : "-"
            }).ToList();

            GridRecent.ItemsSource = recentData;
        }

        /// <summary>
        /// DataGrid 열 너비를 동일하게 설정
        /// </summary>
        private void ConfigureDataGridColumns()
        {
            // DataGrid가 로드된 후에 열 설정
            GridRecent.Loaded += (s, e) =>
            {
                if (GridRecent.Columns.Count > 0)
                {
                              // 모든 열을 균등하게 분배
                    //foreach (var column in GridRecent.Columns)
                    //{
                    //    column.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
                    //}

                              // 또는 특정 비율로 설정
                    GridRecent.Columns[0].Width = new DataGridLength(2, DataGridLengthUnitType.Star); // Time 열을 2배
                    GridRecent.Columns[1].Width = new DataGridLength(1, DataGridLengthUnitType.Star);
                    GridRecent.Columns[2].Width = new DataGridLength(1, DataGridLengthUnitType.Star);
                    GridRecent.Columns[3].Width = new DataGridLength(2, DataGridLengthUnitType.Star);
                }
            };
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

            if (stats == null || stats.DefectCountRange == null)
            {
                Debug.WriteLine("⚠ UpdateDefectCountChart: stats 또는 DefectCountRange가 null");
                return;
            }

            double canvasWidth = 250;
            double canvasHeight = 250;
            double centerX = canvasWidth / 2;
            double centerY = canvasHeight / 2;
            double outerRadius = Math.Min(canvasWidth, canvasHeight) / 2 - 20;
            double innerRadius = outerRadius * 0.70;

            // 불량 개수 범위별 데이터 - 안전한 접근
            var defectRanges = new[]
            {
                new { Name = "1-2", Count = stats.DefectCountRange.ContainsKey("1-2") ? stats.DefectCountRange["1-2"] : 0, Color = "#789DBC" },
                new { Name = "3-4", Count = stats.DefectCountRange.ContainsKey("3-4") ? stats.DefectCountRange["3-4"] : 0, Color = "#FFE3E3" },
                new { Name = "5-6", Count = stats.DefectCountRange.ContainsKey("5-6") ? stats.DefectCountRange["5-6"] : 0, Color = "#FEF9F2" },
                new { Name = "7+", Count = stats.DefectCountRange.ContainsKey("7+") ? stats.DefectCountRange["7+"] : 0, Color = "#C9E9D2" }
            }.Where(d => d.Count > 0).ToArray();

            int totalDefectProducts = defectRanges.Sum(d => d.Count);

            Debug.WriteLine($"📊 UpdateDefectCountChart: 1-2={stats.DefectCountRange.GetValueOrDefault("1-2", 0)}, " +
                           $"3-4={stats.DefectCountRange.GetValueOrDefault("3-4", 0)}, " +
                           $"5-6={stats.DefectCountRange.GetValueOrDefault("5-6", 0)}, " +
                           $"7+={stats.DefectCountRange.GetValueOrDefault("7+", 0)}, " +
                           $"Total={totalDefectProducts}");

            // ⭐ 데이터가 없으면 그냥 빈 차트 표시 ⭐
            if (totalDefectProducts == 0)
            {
                return; // 조용히 종료
            }

            double startAngle = -90;

            foreach (var range in defectRanges)
            {
                double sweepAngle = (double)range.Count / totalDefectProducts * 360;
                Debug.WriteLine($"🎨 차트: {range.Name} = {range.Count}개 ({sweepAngle:F1}도)");

                var donut = CreateDonutSlice(centerX, centerY, outerRadius, innerRadius, startAngle, sweepAngle, range.Color);
                targetCanvas.Children.Add(donut);
                startAngle += sweepAngle;
            }
        }

        /// <summary>
        /// 도넛 차트 조각 생성 (얇은 링 형태)
        /// </summary>
        private System.Windows.Shapes.Path CreateDonutSlice(double centerX, double centerY,
            double outerRadius, double innerRadius, double startAngle, double sweepAngle, string colorHex)
        {
            double startRad = startAngle * Math.PI / 180;
            double endRad = (startAngle + sweepAngle) * Math.PI / 180;

            // 외부 원호의 시작/끝점
            double outerX1 = centerX + outerRadius * Math.Cos(startRad);
            double outerY1 = centerY + outerRadius * Math.Sin(startRad);
            double outerX2 = centerX + outerRadius * Math.Cos(endRad);
            double outerY2 = centerY + outerRadius * Math.Sin(endRad);

            // 내부 원호의 시작/끝점
            double innerX1 = centerX + innerRadius * Math.Cos(startRad);
            double innerY1 = centerY + innerRadius * Math.Sin(startRad);
            double innerX2 = centerX + innerRadius * Math.Cos(endRad);
            double innerY2 = centerY + innerRadius * Math.Sin(endRad);

            bool largeArc = sweepAngle > 180;

            var pathFigure = new System.Windows.Media.PathFigure
            {
                StartPoint = new Point(outerX1, outerY1),
                IsClosed = true
            };

            // 외부 원호
            pathFigure.Segments.Add(new System.Windows.Media.ArcSegment(
                new Point(outerX2, outerY2),
                new Size(outerRadius, outerRadius),
                0,
                largeArc,
                System.Windows.Media.SweepDirection.Clockwise,
                true));

            // 끝점에서 내부로 연결
            pathFigure.Segments.Add(new System.Windows.Media.LineSegment(new Point(innerX2, innerY2), true));

            // 내부 원호 (반대 방향)
            pathFigure.Segments.Add(new System.Windows.Media.ArcSegment(
                new Point(innerX1, innerY1),
                new Size(innerRadius, innerRadius),
                0,
                largeArc,
                System.Windows.Media.SweepDirection.Counterclockwise,
                true));

            var pathGeometry = new System.Windows.Media.PathGeometry();
            pathGeometry.Figures.Add(pathFigure);

            var path = new System.Windows.Shapes.Path
            {
                Fill = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colorHex)),
                Data = pathGeometry
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

            // CSV 파일 저장 다이얼로그
            var saveDialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "CSV 파일 (*.csv)|*.csv",
                FileName = $"PCB_Inspection_Report_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
            };

            if (saveDialog.ShowDialog() == true)
            {
                ExportToCSV(stats, saveDialog.FileName);
                MessageBox.Show("CSV 파일이 저장되었습니다.", "저장 완료",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        /// <summary>
        /// CSV 파일로 내보내기
        /// </summary>
        private void ExportToCSV(InspectionStatistics stats, string filePath)
        {
            var csv = new System.Text.StringBuilder();

            csv.AppendLine("PCB Defect Inspection Report");
            csv.AppendLine($"생성일시,{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
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
            csv.AppendLine("=== 최근 검사 결과 ===");
            csv.AppendLine("시간,유형,결과,불량개수");

            foreach (var result in stats.RecentResults.Take(50))
            {
                csv.AppendLine($"{result.Time:yyyy-MM-dd HH:mm:ss},{result.Type},{result.Result},{result.DefectCount}");
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
}