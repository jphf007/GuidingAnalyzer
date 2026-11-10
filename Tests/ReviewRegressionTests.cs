using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GuidingAnalyzer.Models;
using GuidingAnalyzer.Services;
using Xunit;

namespace GuidingAnalyzer.Tests;

public class ReviewRegressionTests
{
    private static readonly DateTime Start = new(2026, 11, 10, 20, 0, 0);

    [Fact]
    public async Task CompleteFinalFrameWithoutANewlineIsNotLost()
    {
        string path = Path.Combine(Path.GetTempPath(), "GuidingAnalyzer-eof-" + Guid.NewGuid() + ".txt");
        try {
            await File.WriteAllTextAsync(path, """
                Guiding Begins at 2026-11-10 20:00:00
                Pixel scale = 2 arc-sec/px
                Frame,Time,mount,dx,dy,RARawDistance,DECRawDistance,RAGuideDistance,DECGuideDistance,RADuration,RADirection,DECDuration,DECDirection,XStep,YStep,StarMass,SNR,ErrorCode
                1,0,Mount,0,0,1,2,0,0,0,,0,,,,100,20,0
                2,3,Mount,0,0,1,2,0,0,0,,0,,,,100,20,0
                """);
            var sessions = await new GuidingDataService().LoadAllSessionsAsync(path);
            Assert.Equal(2, Assert.Single(sessions).RawPoints.Count);
        } finally { File.Delete(path); }
    }

    [Fact]
    public void ComparisonSpectraUseHertzForBothAxes()
    {
        var comparison = Compare(Session(new FftPeak { FrequencyCph = 7.2, AmplitudeArcsec = 1 }));
        Assert.Equal(.002, Assert.Single(comparison.FftRaSpectra).Frequencies[1], 12);
        Assert.Equal(.002, Assert.Single(comparison.FftDecSpectra).Frequencies[1], 12);
    }

    [Fact]
    public void TwoNearbyPeaksInOneSessionAreNotARecurringPeakAcrossSessions()
    {
        var comparison = Compare(Session(
            new FftPeak { FrequencyCph = 7.2, AmplitudeArcsec = 1 },
            new FftPeak { FrequencyCph = 7.38, AmplitudeArcsec = 5 }), Session());
        Assert.Empty(comparison.RecurringRaPeaks);
        Assert.Empty(comparison.RecurringDecPeaks);
    }

    [Fact]
    public void RecurrenceCountsDistinctSessionsAndUsesOneRepresentativePerSession()
    {
        var comparison = Compare(Session(
            new FftPeak { FrequencyCph = 7.2, AmplitudeArcsec = 1 },
            new FftPeak { FrequencyCph = 7.38, AmplitudeArcsec = 5 }),
            Session(new FftPeak { FrequencyCph = 7.2, AmplitudeArcsec = 3 }));
        var peak = Assert.Single(comparison.RecurringRaPeaks);
        Assert.Equal(2, peak.SessionCount);
        Assert.Equal(1, peak.Prevalence);
        Assert.Equal(4, peak.MeanAmplitudeArcsec, 12);
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("WEST")]
    public void UnknownOrDifferentlyCasedPierSideDoesNotInventAMeridianFlip(string secondSide)
    {
        var first = AltitudeSession(1, "West", 1);
        var second = AltitudeSession(2, secondSide, 1);
        var analysis = new AltitudeAnalysisService().Analyze(first, new() { first, second }, 48.8);
        Assert.False(analysis.MeridianFlipDetected);
        Assert.All(analysis.ScatterPoints, p => Assert.False(p.IsAfterFlip));
    }

    [Fact]
    public void ReturningToTheOriginalPierSideDoesNotUndoTheAfterFlipTimeline()
    {
        var first = AltitudeSession(1, "West", 1);
        var analysis = new AltitudeAnalysisService().Analyze(first,
            new() { first, AltitudeSession(2, "East", 2), AltitudeSession(3, "West", 1) }, 48.8);
        Assert.Contains(analysis.ScatterPoints, p => p.SessionIndex == 3);
        Assert.All(analysis.ScatterPoints.Where(p => p.SessionIndex == 1), p => Assert.False(p.IsAfterFlip));
        Assert.All(analysis.ScatterPoints.Where(p => p.SessionIndex > 1), p => Assert.True(p.IsAfterFlip));
    }

    [Fact]
    public void DoublingRmsMeansOneHundredPercentIncreaseRelativeToTheLowerRms()
    {
        var first = AltitudeSession(1, "West", 1);
        var second = AltitudeSession(2, "East", 2);
        var analysis = new AltitudeAnalysisService().Analyze(first, new() { first, second }, 48.8);
        Assert.True(analysis.MeridianFlipDetected);
        Assert.Equal(100, analysis.Degradation.FlipDeltaPercent, 10);
    }

    private static GuidingSession Session(params FftPeak[] peaks) => new() {
        PixelScale = 1,
        Statistics = new GuidingStatistics { FrameCount = 100, RaRms = .2 },
        FftRa = new FftResult { Frequencies = new[] { 0d, 7.2, 14.4 },
            Amplitudes = new[] { 0d, 1, 0 }, SignificantPeaks = peaks.ToList() },
        FftDec = new FftResult { Frequencies = new[] { 0d, 7.2, 14.4 },
            Amplitudes = new[] { 0d, 1, 0 }, SignificantPeaks = peaks.ToList() }
    };

    private static MultiSessionComparison Compare(params GuidingSession[] sessions) =>
        new MultiSessionService().BuildComparison(sessions.Select(s => MultiSessionService.BuildSummary(s)).ToList(),
            ComparisonScope.AllLoaded, null);

    private static GuidingSession AltitudeSession(int index, string side, double error) => new() {
        SessionIndex = index, PixelScale = 1, StartTime = Start.AddHours(index),
        SessionInfo = new GuidingSessionInfo { PierSide = side, HourAngle = 0, DecDeg = 20 },
        RawPoints = Enumerable.Range(0, 60).Select(i => new GuidingPoint {
            Timestamp = Start.AddHours(index).AddSeconds(i * 15), RaError = error
        }).ToList()
    };
}
