using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;

namespace finalProject.Models
{
    /// <summary>
    /// 개별 검사 결과 데이터
    /// </summary>
    public class InspectionResult
    {
        public DateTime Time { get; set; }
        public string Type { get; set; }
        public string Result { get; set; }
        public int DefectCount { get; set; }
    }

    /// <summary>
    /// 시간대별 불량률 데이터
    /// </summary>
    public class DefectRatePoint
    {
        public DateTime Time { get; set; }
        public double Rate { get; set; }
    }

    /// <summary>
    /// PCB 검사 통계 데이터를 관리하는 클래스
    /// </summary>
    public class InspectionStatistics
    {
        // 총 검사 횟수
        public int TotalInspected { get; set; }

        // 정상 제품 수
        public int NormalCount { get; set; }

        // 총 불량 제품 수
        public int DefectCount { get; set; }

        // 불량 유형별 카운트
        public Dictionary<string, int> DefectTypeCount { get; set; }

        // 불량 개수 범위별 카운트 추가
        public Dictionary<string, int> DefectCountRange { get; set; }

        // 최근 검사 결과 리스트
        public ObservableCollection<InspectionResult> RecentResults { get; set; }

        // ⭐ 시간대별 불량률 기록 추가 ⭐
        public ObservableCollection<DefectRatePoint> DefectRateHistory { get; set; }

        // 마지막 기록 시간 (예: 1분마다 기록)
        private DateTime lastRecordTime;
        private int inspectionsInCurrentPeriod;
        private int defectsInCurrentPeriod;

        // 정상률 (%)
        public double NormalRate => TotalInspected > 0
            ? Math.Round((double)NormalCount / TotalInspected * 100, 2)
            : 0;

        // 불량률 (%)
        public double DefectRate => TotalInspected > 0
            ? Math.Round((double)DefectCount / TotalInspected * 100, 2)
            : 0;

        public InspectionStatistics()
        {
            TotalInspected = 0;
            NormalCount = 0;
            DefectCount = 0;

            DefectTypeCount = new Dictionary<string, int>
            {
                { "short", 0 },
                { "mousebite", 0 },
                { "pin-hole", 0 },
                { "spur", 0 },
                { "open", 0 },
                { "copper", 0 }
            };

            DefectCountRange = new Dictionary<string, int>
            {
                { "1-2", 0 },
                { "3-4", 0 },
                { "5-6", 0 },
                { "7+", 0 }
            };

            RecentResults = new ObservableCollection<InspectionResult>();

            // ⭐ 시간대별 불량률 초기화 ⭐
            DefectRateHistory = new ObservableCollection<DefectRatePoint>();
            lastRecordTime = DateTime.Now;
            inspectionsInCurrentPeriod = 0;
            defectsInCurrentPeriod = 0;
        }

        /// <summary>
        /// 검사 결과를 기록합니다
        /// </summary>
        public void RecordInspection(bool isNormal, List<string> detectedDefects = null, int totalDefectCount = 0)
        {
            TotalInspected++;
            inspectionsInCurrentPeriod++;

            if (isNormal)
            {
                NormalCount++;
                AddRecentResult("Normal", "OK", 0);
            }
            else
            {
                DefectCount++;
                defectsInCurrentPeriod++;

                // ⭐⭐ totalDefectCount를 매개변수로 받아서 사용 ⭐⭐
                int defectCount = totalDefectCount;

                // 불량 유형별 카운트 증가
                if (detectedDefects != null && detectedDefects.Count > 0)
                {
                    foreach (var defect in detectedDefects)
                    {
                        if (DefectTypeCount.ContainsKey(defect))
                        {
                            DefectTypeCount[defect]++;
                        }
                    }

                    string defectTypes = string.Join(", ", detectedDefects);
                    AddRecentResult(defectTypes, "NG", defectCount);
                }
                else
                {
                    defectCount = 1;
                    AddRecentResult("Unknown", "NG", 1);
                }

                // ⭐⭐ 실제 불량 개수로 범위 분류 ⭐⭐
                if (defectCount >= 1 && defectCount <= 2)
                {
                    DefectCountRange["1-2"]++;
                }
                else if (defectCount >= 3 && defectCount <= 4)
                {
                    DefectCountRange["3-4"]++;
                }
                else if (defectCount >= 5 && defectCount <= 6)
                {
                    DefectCountRange["5-6"]++;
                }
                else if (defectCount >= 7)
                {
                    DefectCountRange["7+"]++;
                }
            }

            UpdateDefectRateHistory();
        }

        /// <summary>
        /// 시간대별 불량률 업데이트 (1분마다 또는 10회 검사마다)
        /// </summary>
        private void UpdateDefectRateHistory()
        {
            var now = DateTime.Now;

            // 1분이 지났거나 10회 이상 검사했을 때 기록
            if ((now - lastRecordTime).TotalSeconds >= 60 || inspectionsInCurrentPeriod >= 10)
            {
                if (inspectionsInCurrentPeriod > 0)
                {
                    double periodRate = Math.Round(
                        (double)defectsInCurrentPeriod / inspectionsInCurrentPeriod * 100, 2);

                    DefectRateHistory.Add(new DefectRatePoint
                    {
                        Time = now,
                        Rate = periodRate
                    });

                    // 최대 20개 포인트만 유지 (화면에 표시할 개수)
                    while (DefectRateHistory.Count > 20)
                    {
                        DefectRateHistory.RemoveAt(0);
                    }
                }

                // 카운터 초기화
                lastRecordTime = now;
                inspectionsInCurrentPeriod = 0;
                defectsInCurrentPeriod = 0;
            }
        }


        /// <summary>
        /// 최근 결과 리스트에 추가
        /// </summary>
        private void AddRecentResult(string type, string result, int defectCount)
        {
            var newResult = new InspectionResult
            {
                Time = DateTime.Now,
                Type = type,
                Result = result,
                DefectCount = defectCount
            };

            // 리스트 맨 앞에 추가
            RecentResults.Insert(0, newResult);

            // 최대 50개만 유지
            while (RecentResults.Count > 50)
            {
                RecentResults.RemoveAt(RecentResults.Count - 1);
            }
        }

        /// <summary>
        /// 통계 초기화
        /// </summary>
        public void Reset()
        {
            TotalInspected = 0;
            NormalCount = 0;
            DefectCount = 0;

            foreach (var key in DefectTypeCount.Keys.ToList())
            {
                DefectTypeCount[key] = 0;
            }

            // ⭐⭐ 불량 개수 범위도 초기화 ⭐⭐
            foreach (var key in DefectCountRange.Keys.ToList())
            {
                DefectCountRange[key] = 0;
            }

            RecentResults.Clear();

            // ⭐ 시간대별 불량률 기록 초기화 ⭐
            DefectRateHistory.Clear();
            lastRecordTime = DateTime.Now;
            inspectionsInCurrentPeriod = 0;
            defectsInCurrentPeriod = 0;
        }

        /// <summary>
        /// 특정 불량 유형의 발생률 (%)
        /// </summary>
        public double GetDefectTypeRate(string defectType)
        {
            if (TotalInspected == 0) return 0;

            if (DefectTypeCount.ContainsKey(defectType))
            {
                return Math.Round((double)DefectTypeCount[defectType] / TotalInspected * 100, 2);
            }

            return 0;
        }
    }
}