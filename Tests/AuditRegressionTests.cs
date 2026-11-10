using System;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using GuidingAnalyzer.Models;
using GuidingAnalyzer.Services;
using Xunit;

namespace GuidingAnalyzer.Tests;

public class AuditRegressionTests
{
    private static readonly DateTime Start = new(2026, 10, 5, 22, 0, 0);

    [Fact]
    public void DithersAndNonFiniteFramesDoNotPolluteStatistics()
    {
        var points = Enumerable.Range(0, 30).Select(i => new GuidingPoint {
            Timestamp = Start.AddSeconds(i * 3), RaError = .3, DecError = .4
        }).ToList();
        points.Add(new GuidingPoint { IsDitherOrSettling = true, RaError = 100, DecError = 100 });
        points.Add(new GuidingPoint { RaError = double.NaN, DecError = 0 });
        var stats = new StatisticsService().ComputeStatistics(points);
        Assert.Equal(30, stats.FrameCount);
        Assert.Equal(.5, stats.TotalRms, 10);
    }

    [Fact]
    public void ZeroPulsesBetweenCorrectionsAreNotDirectionReversals()
    {
        var session = new GuidingSession();
        session.RawPoints = Enumerable.Range(0, 60).Select(i => new GuidingPoint {
            Timestamp = Start.AddSeconds(i * 3), DecError = 2, StarSNR = 100,
            DecCorrection = i % 2 == 0 ? 100 : 0
        }).ToList();
        Assert.DoesNotContain(new AnomalyDetector().Detect(session), a => a.Type == AnomalyType.Backlash);
        Assert.DoesNotContain(new AdviceService().GenerateAdvice(session), a => a.Category == AdviceCategory.Backlash);
    }

    [Fact]
    public void NoiseDoesNotBecomeAnEstimatedWormPeriodOrFiveAdjacentHarmonics()
    {
        var random = new Random(42);
        var noise = Enumerable.Range(0, 4096).Select(_ =>
            Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble())).ToArray();
        var session = new GuidingSession { RawPoints = noise.Select((v, i) =>
            new GuidingPoint { Timestamp = Start.AddSeconds(i * 2), RaError = v }).ToList() };
        session.FftRa = new FftAnalysisService().ComputeFft(noise, 2, GuidingAxis.RA);
        Assert.Empty(session.FftRa.SignificantPeaks);
        var stats = new GuidingStatistics();
        new StatisticsService().ComputePecHarmonics(stats, session.FftRa);
        Assert.Equal(0, stats.WormPeriodSeconds);
        Assert.Empty(stats.RaHarmonics);
        Assert.Null(new AltitudeAnalysisService().EstimateWormPeriod(new() { session }));
    }

    [Fact]
    public void SiteLatitudeCanBeInTheSouthernHemisphereAndMissingDeclinationIsRejected()
    {
        var service = new AltitudeAnalysisService();
        var sessions = new[] { -1.0, 0, 1.0 }.Select(ha => new GuidingSession {
            SessionInfo = new GuidingSessionInfo { HourAngle = ha, DecDeg = -20,
                AltDeg = AltitudeAnalysisService.ComputeAltitude(-33.5, -20, ha * 15, Start, Start) }
        }).ToList();
        Assert.InRange(service.EstimateSiteLatitude(sessions)!.Value, -33.6, -33.4);
        sessions.ForEach(s => s.SessionInfo!.DecDeg = double.NaN);
        Assert.Null(service.EstimateSiteLatitude(sessions));
    }

    [Fact]
    public void SlidingRmsUsesValidContinuousWindowsWithinASessionAndRetainsOffsets()
    {
        GuidingSession Session(int index, int seconds, double error) => new() {
            SessionIndex = index, StartTime = Start.AddSeconds(seconds),
            SessionInfo = new GuidingSessionInfo { HourAngle = 0, DecDeg = 20, PierSide = "West" },
            RawPoints = Enumerable.Range(0, 50).Select(i => new GuidingPoint {
                Timestamp = Start.AddSeconds(seconds + i * 15), RaError = error,
                IsGuideStep = i != 25
            }).ToList()
        };
        var first = Session(1, 0, 2); var second = Session(2, 3600, 10);
        var result = new AltitudeAnalysisService().Analyze(first, new() { first, second }, 48.8);
        Assert.NotEmpty(result.SlidingPoints);
        Assert.All(result.SlidingPoints, p => Assert.Equal(p.SessionIndex == 1 ? 2 : 10, p.RaRms, 10));
        Assert.DoesNotContain(result.SlidingPoints, p => p.Timestamp > Start.AddSeconds(225)
            && p.Timestamp < Start.AddSeconds(540));
    }

    [Fact]
    public async Task FailedCalibrationAndTruncatedSessionHeaderDoNotSwallowTheNextSession()
    {
        string path = Path.Combine(Path.GetTempPath(), "GuidingAnalyzer-audit-" + Guid.NewGuid() + ".txt");
        try {
            await File.WriteAllTextAsync(path, """
                Calibration Begins at 2026-10-05 20:00:00
                Calibration failed
                Guiding Begins at 2026-10-05 21:00:00
                Pixel scale = 2 arc-sec/px
                Guiding Begins at 2026-10-05 22:00:00
                Pixel scale = 2 arc-sec/px
                Frame,Time,mount,RARawDistance,DECRawDistance,ErrorCode
                1,3,Mount,0.1,0.2,0
                2,6,Mount,NaN,0.2,0
                3,9,Mount,,0.2,0
                Guiding Ends at 2026-10-05 22:01:00
                """);
            var session = Assert.Single(await new GuidingDataService().LoadAllSessionsAsync(path));
            Assert.Equal(22, session.StartTime.Hour);
            Assert.True(session.RawPoints[0].IsGuideStep);
            Assert.False(session.RawPoints[1].IsGuideStep);
            Assert.False(session.RawPoints[2].IsGuideStep);
        } finally { File.Delete(path); }
    }

    [Fact]
    public async Task SignedCalibrationAnglesAreNotSilentlyReplacedByZero()
    {
        string path = Path.Combine(Path.GetTempPath(), "GuidingAnalyzer-angles-" + Guid.NewGuid() + ".txt");
        try {
            await File.WriteAllTextAsync(path, """
                Calibration Begins at 2026-10-05 20:00:00
                Direction,Step,dx,dy,x,y,Dist
                West,0,0,0,1,1,0
                West calibration complete. Angle = -179.5 deg, Rate = 1.663 px/sec, Parity = Even
                North calibration complete. Angle = -88.2 deg, Rate = 3.074 px/sec, Parity = Odd
                Calibration complete, mount = Test.
                Guiding Begins at 2026-10-05 22:00:00
                Pixel scale = 2.26 arc-sec/px
                Mount = Test, xAngle = -179.5, xRate = 1.563, yAngle = -88.2, yRate = 3.074, parity = +/-
                Frame,Time,mount,RARawDistance,DECRawDistance,ErrorCode
                1,3,Mount,0.1,0.2,0
                Guiding Ends at 2026-10-05 22:01:00
                """);
            var session = Assert.Single(await new GuidingDataService().LoadAllSessionsAsync(path));
            Assert.Equal(-179.5, session.Calibration!.RaAngleDeg);
            Assert.Equal(-88.2, session.Calibration.DecAngleDeg);
            Assert.Equal(-179.5, session.SessionInfo!.MountXAngle);
            Assert.Equal(-88.2, session.SessionInfo.MountYAngle);
        } finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("enabled")]
    [InlineData("true")]
    [InlineData("1")]
    public void ActualDecReversalsAreReportedAndEnabledCompensationIsRecognized(string enabled)
    {
        var session = new GuidingSession { SessionInfo = new GuidingSessionInfo {
            BacklashCompEnabled = enabled, BacklashPulseMs = 94, DecGuideMode = "Auto"
        }};
        session.RawPoints = Enumerable.Range(0, 60).Select(i => new GuidingPoint {
            Timestamp = Start.AddSeconds(i * 3), DecError = 2, StarSNR = 100,
            DecCorrection = i % 3 == 0 ? (i % 2 == 0 ? 100 : -100) : 0
        }).ToList();
        Assert.Contains(new AnomalyDetector().Detect(session), a => a.Type == AnomalyType.Backlash);
        var advice = new AdviceService().GenerateAdvice(session);
        Assert.Contains(advice, a => a.Category == AdviceCategory.Backlash && a.Action.Contains("active (94 ms)"));
        Assert.DoesNotContain(advice, a => a.Title == "Backlash compensation not enabled");
        session.RawPoints.ForEach(p => p.IsDitherOrSettling = true);
        Assert.DoesNotContain(new AnomalyDetector().Detect(session), a => a.Type == AnomalyType.Backlash);
    }

    [Fact]
    public void UncorrectedNoiseCannotBeDiagnosedAsAggressiveGuiding()
    {
        var session = new GuidingSession { Statistics = new GuidingStatistics() };
        session.RawPoints = Enumerable.Range(0, 60).Select(i => new GuidingPoint {
            Timestamp = Start.AddSeconds(i * 3), RaError = i % 2 == 0 ? .1 : -.1, StarSNR = 100
        }).ToList();
        Assert.DoesNotContain(new AnomalyDetector().Detect(session), a => a.Type == AnomalyType.AggressiveCorrection);
        session.RawPoints.ForEach(p => p.RaCorrection = 100);
        Assert.Contains(new AnomalyDetector().Detect(session), a => a.Type == AnomalyType.AggressiveCorrection);
    }

    [Fact]
    public void QualityDescriptionMatchesTheDowngradedGrade()
    {
        var points = Enumerable.Range(0, 100).Select(i => new GuidingPoint {
            Timestamp = Start.AddMilliseconds(i * 2), RaError = (i - 50) * .002
        }).ToList();
        var stats = new StatisticsService().ComputeStatistics(points);
        Assert.Equal("C", stats.QualityGrade);
        Assert.StartsWith("Acceptable", stats.QualityDescription);
    }

    [Fact]
    public async Task LoadingAnEmptyLogClearsThePreviousSessionAndCharts()
    {
        string path = Path.Combine(Path.GetTempPath(), "GuidingAnalyzer-empty-" + Guid.NewGuid() + ".txt");
        try {
            await File.WriteAllTextAsync(path, "PHD2 version 2.6.14\nNo guiding sessions\n");
            var vm = new GuidingAnalyzerVM { CurrentSession = new GuidingSession(), RaRmsText = "old data" };
            vm.GuidingCurvePlot.Series.Add(new OxyPlot.Series.LineSeries());
            var method = typeof(GuidingAnalyzerVM).GetMethod("LoadLogAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)method.Invoke(vm, new object[] { path })!;
            Assert.Null(vm.CurrentSession);
            Assert.Equal("—", vm.RaRmsText);
            Assert.Empty(vm.GuidingCurvePlot.Series);
            Assert.False(vm.HasSessions);
            Assert.Empty(vm.AvailableSessions);
            Assert.Contains("No guiding session", vm.StatusMessage);
        } finally { File.Delete(path); }
    }

    [Fact]
    public void ShortSessionCannotRetainThePreviousConfidenceOrAmplitudes()
    {
        var vm = new GuidingAnalyzerVM {
            RaDriftConfidenceValue = .9, DecDriftConfidenceValue = .8,
            SummaryMountRaAmplitude = "old", SummaryMountDecResidual = "old"
        };
        typeof(GuidingAnalyzerVM).GetMethod("UpdateStatisticsDisplay", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, new object[] { new GuidingSession { Statistics = new GuidingStatistics() } });
        Assert.Equal(0, vm.RaDriftConfidenceValue);
        Assert.Equal(0, vm.DecDriftConfidenceValue);
        Assert.Equal("—", vm.SummaryMountRaAmplitude);
        Assert.Equal("—", vm.SummaryMountDecResidual);
    }

    [Fact]
    public async Task ComparisonPreparesEverySessionAndDoesNotTreatUnknownDataAsPerfectGuiding()
    {
        var service = new MultiSessionService();
        var sessions = Enumerable.Range(0, 2).Select(n => new GuidingSession {
            PixelScale = 1, StartTime = Start.AddHours(n), SessionIndex = n + 1,
            RawPoints = Enumerable.Range(0, 300).Select(i => new GuidingPoint {
                Timestamp = Start.AddHours(n).AddSeconds(i * 3), RaError = .2 + n * .3
            }).ToList()
        }).ToList();
        var missing = new GuidingSession { PixelScale = 0, RawPoints = sessions[0].RawPoints };
        sessions.Add(missing);
        await service.PrepareSessionsAsync(sessions);
        service.SetSessions("test", sessions);
        Assert.All(sessions.Take(2), s => { Assert.NotNull(s.Statistics); Assert.NotNull(s.FftRa); });
        Assert.Null(missing.Statistics);
        var summaries = service.BuildAllSummaries();
        var unknown = summaries.Single(s => s.Session == missing);
        Assert.False(unknown.IsIncludedInComparison);
        Assert.True(double.IsNaN(unknown.TotalRms));
        unknown.IsIncludedInComparison = true; // even a manual inclusion cannot introduce fake zeroes
        var comparison = service.BuildComparison(summaries, ComparisonScope.AllLoaded, null);
        Assert.Equal(2, comparison.IncludedSessions.Count);
    }

    [Fact]
    public async Task MissingPixelScaleCannotProduceAnArcsecondAnalysis()
    {
        var vm = new GuidingAnalyzerVM { CurrentSession = new GuidingSession() };
        var missing = new GuidingSession { PixelScale = 0, RawPoints = new() { new GuidingPoint() } };
        var method = typeof(GuidingAnalyzerVM).GetMethod("AnalyzeSessionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)method.Invoke(vm, new object[] { missing })!;
        Assert.Null(vm.CurrentSession);
        Assert.False(vm.IsLoading);
        Assert.Contains("pixel scale", vm.StatusMessage);
    }

    [Fact]
    public void ExportsPreserveFrameMillisecondsAndIdentifyTheSessionWithinTheNight()
    {
        string csv = Path.Combine(Path.GetTempPath(), "GuidingAnalyzer-export-" + Guid.NewGuid() + ".csv");
        string json = Path.ChangeExtension(csv, ".json");
        var culture = CultureInfo.CurrentCulture;
        try {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var session = new GuidingSession { SessionIndex = 5, StartTime = Start, PixelScale = 2.26,
                RawPoints = new() { new GuidingPoint { FrameIndex = 1, Timestamp = Start.AddMilliseconds(3191), RaError = .226 } } };
            new ExportService().ExportToCsv(session, csv);
            new ExportService().ExportToJson(session, json);
            Assert.Contains("2026-10-05 22:00:03.191,0.2260", File.ReadAllText(csv));
            var parsed = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(json));
            Assert.Equal(5, (int)parsed["Session"]!["SessionIndex"]!);
            Assert.Equal(Start, (DateTime)parsed["Session"]!["StartTime"]!);
        } finally { CultureInfo.CurrentCulture = culture; File.Delete(csv); File.Delete(json); }
    }
}
