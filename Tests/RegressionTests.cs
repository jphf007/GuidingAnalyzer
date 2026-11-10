using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Markup;
using System.Windows.Media;
using GuidingAnalyzer.Models;
using GuidingAnalyzer.Services;
using Xunit;

namespace GuidingAnalyzer.Tests;

public class RegressionTests
{
    [Theory]
    [InlineData("48.8123", "30.25", true)]
    [InlineData("-33.5", ".5", true)]
    [InlineData("48,8", "30", false)]
    [InlineData("48.8", "30,5", false)]
    [InlineData("91", "30", false)]
    [InlineData("NaN", "30", false)]
    [InlineData("48.8", "0", false)]
    [InlineData("48.8", "-1", false)]
    [InlineData("", "30", false)]
    public void DotDecimalsAreIndependentOfFrenchCulture(string latitude, string sensitivity, bool expected)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.Equal(expected, PolarInput.TryParse(latitude, sensitivity, out _, out _, out _));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void ActualPolarTabBindingsAndButtonApplyBothValuesWithoutLosingFocus()
    {
        RunSta(() =>
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var vm = new GuidingAnalyzerVM {
                AvailableSessions = new ObservableCollection<GuidingSession>(Night(48.8, 3, -2))
            };
            var resources = new GuidingAnalyzerDockableView();
            var host = new ContentPresenter {
                Content = new { AnalyzerVM = vm },
                ContentTemplate = (DataTemplate)resources["GuidingAnalyzer.GuidingAnalyzerDockableVM_Dockable"],
                Language = XmlLanguage.GetLanguage("fr-FR")
            };
            host.Measure(new Size(1400, 1000)); host.Arrange(new Rect(0, 0, 1400, 1000));
            Pump();
            var view = Assert.IsType<GuidingAnalyzerView>(VisualTreeHelper.GetChild(host, 0));
            Assert.Same(vm, view.DataContext);
            var tabs = (TabControl)view.FindName("MainTabControl");
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(t => (string)t.Header == "Polar Alignment");
            view.Measure(new Size(1400, 1000)); view.Arrange(new Rect(0, 0, 1400, 1000));
            Pump();
            var latitude = (TextBox)view.FindName("PolarLatitudeTextBox");
            var sensitivity = (TextBox)view.FindName("PolarSensitivityTextBox");
            var button = (Button)view.FindName("PolarRecalculateButton");
            Assert.Equal("48.8", latitude.Text);
            Assert.Same(vm.RefreshPolarAlignmentCommand, button.Command);
            Invoke(button);
            Assert.True(vm.HasPolarResult, vm.PolarErrorText);
            string initialError = vm.PolarErrAzText;
            string initialTurns = vm.PolarAzTurnsText;
            latitude.Text = "35.125"; sensitivity.Text = "15.5"; Pump();
            Assert.Equal("35.125", vm.PolarLatitudeInput);
            Assert.Equal("15.5", vm.PolarArcminPerTurnInput);
            Invoke(button);
            Assert.Equal(35.125, vm.PolarLatitudeDeg);
            Assert.Equal(15.5, vm.PolarArcminPerTurn);
            Assert.True(vm.HasPolarResult, vm.PolarErrorText);
            Assert.NotEqual(initialError, vm.PolarErrAzText);
            Assert.NotEqual(initialTurns, vm.PolarAzTurnsText);
            latitude.Text = "48,8"; Invoke(button);
            Assert.True(vm.HasPolarError);
            Assert.False(vm.HasPolarResult);
            Assert.Equal(35.125, vm.PolarLatitudeDeg); // no partial commit on invalid input
            latitude.Text = "48.8"; sensitivity.Text = "30"; Invoke(button);
            Assert.True(vm.HasPolarResult, vm.PolarErrorText);
            Assert.Equal(initialError, vm.PolarErrAzText);
            Assert.Equal(initialTurns, vm.PolarAzTurnsText);
            view.DataContext = null;
        });
    }

    private static void Invoke(Button button)
    {
        ((IInvokeProvider)new ButtonAutomationPeer(button)).Invoke();
        Pump();
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => {
            try { action(); } catch (Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "WPF test timed out");
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    [Fact]
    public void ConstantPositionDoesNotBecomeArtificialDrift()
    {
        var points = Enumerable.Range(0, 200).Select(i => new GuidingPoint {
            Timestamp = Epoch.AddSeconds(i * 3), RaError = 1, DecError = -2
        }).ToList();
        var curve = new MountCurveService().ReconstructMountCurve(points, 1);
        Assert.All(curve, p => { Assert.Equal(0, p.RaMountPosition); Assert.Equal(0, p.DecMountPosition); });
    }

    [Fact]
    public void PulseActsOnFollowingFrameAndDitherDoesNotCreateAJump()
    {
        var points = new List<GuidingPoint> {
            new() { Timestamp = Epoch, RaError = 0, RaCorrection = -100, RaGuideDistanceArcsec = 1 },
            new() { Timestamp = Epoch.AddSeconds(3), RaError = 1 },
            new() { Timestamp = Epoch.AddSeconds(6), RaError = 100, IsDitherOrSettling = true },
            new() { Timestamp = Epoch.AddSeconds(9), RaError = 10 },
            new() { Timestamp = Epoch.AddSeconds(12), RaError = 10 }
        };
        var curve = new MountCurveService().ReconstructMountCurve(points, 1);
        Assert.Equal(curve[0].RaMountPosition, curve[1].RaMountPosition);
        Assert.Equal(curve[1].RaMountPosition, curve[3].RaMountPosition);
        Assert.True(curve[2].IsDitherOrSettling);
    }

    [Fact]
    public void RequestedDistanceIsCappedByThePulseActuallySent()
    {
        var points = new List<GuidingPoint> {
            new() { Timestamp = Epoch, RaCorrection = -100, RaGuideDistanceArcsec = 10 },
            new() { Timestamp = Epoch.AddSeconds(3), RaError = 1 }
        };
        var curve = new MountCurveService().ReconstructMountCurve(points, 1, .01, .01);
        Assert.Equal(1, curve[0].RaCorrectionArcsec);
        Assert.Equal(curve[0].RaMountPosition, curve[1].RaMountPosition);
    }

    [Fact]
    public void PerfectDecGuidingLeavesTheReconstructedDecPositionConstant()
    {
        var points = new List<GuidingPoint> {
            new() { Timestamp = Epoch, RaError = .6, DecError = -.4,
                RaGuideDistanceArcsec = .6, DecGuideDistanceArcsec = -.4,
                RaCorrection = -80, DecCorrection = 40 },
            new() { Timestamp = Epoch.AddSeconds(3), RaError = 0, DecError = 0 }
        };
        var curve = new MountCurveService().ReconstructMountCurve(points, 1, .0075, .01);
        Assert.Equal(curve[0].DecMountPosition, curve[1].DecMountPosition, 10);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryRaPointMatchesTheDistributedV002IncludingDithersAndRejectedFrames(bool knownRates)
    {
        var random = new Random(28);
        var points = Enumerable.Range(0, 4096).Select(i => new GuidingPoint {
            FrameIndex = i, Timestamp = Epoch.AddSeconds(i * 3.15),
            RaError = Math.Sin(i / 31.0) + random.NextDouble(),
            DecError = Math.Cos(i / 29.0) + random.NextDouble(),
            RaGuideDistanceArcsec = i % 7 == 0 ? null : .3 * Math.Sin(i / 11.0),
            DecGuideDistanceArcsec = i % 9 == 0 ? null : .4 * Math.Cos(i / 13.0),
            RaCorrection = i % 5 == 0 ? 0 : (i % 2 == 0 ? 70 : -90),
            DecCorrection = i % 4 == 0 ? 0 : (i % 2 == 0 ? -50 : 60),
            IsDitherOrSettling = i % 200 >= 180 && i % 200 < 190,
            IsGuideStep = i % 17 != 0
        }).ToList();
        double rate = knownRates ? .0075 : 0;
        var expected = new Fixtures.MountCurveV002().ReconstructMountCurve(points, 1, rate, rate);
        var actual = new MountCurveService().ReconstructMountCurve(points, 1, rate, rate);
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < actual.Count; i++) {
            Assert.Equal(expected[i].RaMountPosition, actual[i].RaMountPosition);
            Assert.Equal(expected[i].RaCorrectionArcsec, actual[i].RaCorrectionArcsec);
            Assert.Equal(expected[i].IsDitherOrSettling, actual[i].IsDitherOrSettling);
        }
    }

    [Fact]
    public void DecDitherReconnectIgnoresPulsesOnAnInvalidRecoveryFrame()
    {
        var points = new List<GuidingPoint> {
            new() { Timestamp = Epoch, RaError = 2, DecError = -3 },
            new() { Timestamp = Epoch.AddSeconds(3), IsDitherOrSettling = true,
                RaError = 100, DecError = 100, DecGuideDistanceArcsec = 100, DecCorrection = -1000 },
            new() { Timestamp = Epoch.AddSeconds(6), IsGuideStep = false,
                RaGuideDistanceArcsec = .2, RaCorrection = -20, DecGuideDistanceArcsec = -.4, DecCorrection = 40 },
            new() { Timestamp = Epoch.AddSeconds(9), RaError = 8, DecError = 9 },
            new() { Timestamp = Epoch.AddSeconds(12), RaError = 8, DecError = 9 }
        };
        var curve = new MountCurveService().ReconstructMountCurve(points, 1, .01, .01);
        foreach (int i in new[] { 3, 4 }) {
            Assert.Equal(curve[0].DecMountPosition, curve[i].DecMountPosition, 10);
        }
    }

    [Fact]
    public async Task RecordedDecPulseIsUndoneWithTheCorrectSignThroughTheParser()
    {
        // Two measured frames from the supplied log; no equipment or pointing metadata.
        string path = Path.Combine(Path.GetTempPath(), "GuidingAnalyzer-" + Guid.NewGuid() + ".txt");
        try {
            await File.WriteAllTextAsync(path, """
                Guiding Begins at 2026-10-05 22:52:23
                Pixel scale = 2.26 arc-sec/px
                Mount = Test, xAngle = -179.5, xRate = 1.563, yAngle = -88.2, yRate = 3.074, parity = +/-
                Frame,Time,mount,dx,dy,RARawDistance,DECRawDistance,RAGuideDistance,DECGuideDistance,RADuration,RADirection,DECDuration,DECDirection,XStep,YStep,StarMass,SNR,ErrorCode
                316,1002.144,Mount,0.262,0.532,-0.267,-0.535,-0.176,-0.375,113,E,122,N,,,424931,387.87,0
                317,1005.294,Mount,0.043,-0.079,-0.043,0.079,-0.014,0.000,9,E,0,,,,368375,370.39,0
                Guiding Ends at 2026-10-05 23:10:00
                """);
            var session = Assert.Single(await new GuidingDataService().LoadAllSessionsAsync(path));
            var curve = new MountCurveService().ReconstructSession(session);
            Assert.Equal((.079 - (-.535) + (-.375)) * 2.26,
                curve[1].DecMountPosition - curve[0].DecMountPosition, 8);
        } finally { File.Delete(path); }
    }

    [Fact]
    public void MeasuredDriftUsesElapsedTimeWithUnevenExposureIntervals()
    {
        var curve = Enumerable.Range(0, 100).Select(i => {
            double seconds = i * 3 + (i % 4) * .25;
            return new MountCurvePoint { Timestamp = Epoch.AddSeconds(seconds),
                RaMountPosition = seconds / 60 * 2, DecMountPosition = seconds / 60 * -3 };
        }).ToList();
        var drift = new StatisticsService().ComputeDriftOnMountCurve(curve);
        Assert.Equal(2, drift.RaDriftArcsecPerMin, 6);
        Assert.Equal(-3, drift.DecDriftArcsecPerMin, 6);
    }

    [Fact]
    public void RapidSessionChangesKeepTheLastSessionOnScreen()
    {
        RunSta(() => {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            var sessions = Night(48.8, 3, -2);
            var vm = new GuidingAnalyzerVM { AvailableSessions = new ObservableCollection<GuidingSession>(sessions) };
            var field = typeof(GuidingAnalyzerVM).GetField("_sessionAnalysisTask",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            vm.SelectedSession = sessions[0];
            var first = (Task)field.GetValue(vm)!;
            vm.SelectedSession = sessions[1];
            var second = (Task)field.GetValue(vm)!;
            var timeout = DateTime.UtcNow.AddSeconds(20);
            while ((!first.IsCompleted || !second.IsCompleted) && DateTime.UtcNow < timeout) Pump();
            Assert.True(first.IsCompleted && second.IsCompleted);
            Assert.Same(sessions[1], vm.CurrentSession);
            Assert.False(vm.IsLoading);
            Assert.DoesNotContain("Error:", vm.StatusMessage);
        });
    }

    [Theory]
    [InlineData(48.8, 3, -2)]
    [InlineData(35, -4, 2.5)]
    [InlineData(-33.5, 2, 1)]
    public void PolarErrorRecoveredFromIndependentThreeDimensionalTracking(double lat, double az, double alt)
    {
        var result = new PolarAlignmentService().Compute(Night(lat, az, alt), lat);
        Assert.True(result.IsValid, result.ErrorMessage);
        Assert.InRange(result.ErrAzArcmin, az - .03, az + .03);
        Assert.InRange(result.ErrAltArcmin, alt - .03, alt + .03);
        Assert.True(double.IsFinite(result.ResidualRmsMasPerMin));
        Assert.InRange(result.Conditioning, 1, 10);
    }

    [Fact]
    public void MissingPositionOrParityIsExcludedAndTwoSessionsDoNotClaimZeroResidual()
    {
        var sessions = Night(48.8, 3, -2);
        sessions[2].SessionInfo!.HourAngle = double.NaN;
        var service = new PolarAlignmentService();
        var result = service.Compute(sessions, 48.8);
        Assert.True(result.IsValid, result.ErrorMessage);
        Assert.Equal(1, result.ExcludedCount);
        Assert.True(double.IsNaN(result.ResidualRmsMasPerMin));
        sessions[1].SessionInfo!.MountParity = "+/?";
        Assert.False(service.Compute(sessions, 48.8).IsValid);
    }

    [Fact]
    public void RepeatedTargetGeometryIsRejected()
    {
        var session = Night(48.8, 3, -2)[0];
        Assert.False(new PolarAlignmentService().Compute(new[] { session, session }, 48.8).IsValid);
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(1500)]
    [InlineData(60000)]
    public void FftAmplitudeRemainsCorrectWithHannPaddingAndLargeLogs(int n)
    {
        var signal = Enumerable.Range(0, n).Select(i => 3 * Math.Cos(2 * Math.PI * i / 64)).ToArray();
        var fft = new FftAnalysisService().ComputeFft(signal, 2, GuidingAxis.RA);
        int peak = Array.IndexOf(fft.Amplitudes, fft.Amplitudes.Max());
        Assert.InRange(fft.Amplitudes[peak], 2.9, 3.1);
        Assert.InRange(3600 / fft.Frequencies[peak], 127, 129);
    }

    [Fact]
    public void SamplingPreservesTimeAndDoesNotBridgeDithers()
    {
        var points = Enumerable.Range(0, 200).Select(i => new MountCurvePoint {
            Timestamp = Epoch.AddSeconds(i * 3 + (i % 2) * .1),
            RaMountPosition = i * 3 + (i % 2) * .1,
            IsDitherOrSettling = i >= 120 && i < 140
        }).ToList();
        var segment = SpectralSampling.LongestSegment(points);
        Assert.Equal(120, segment.Count);
        var sampled = SpectralSampling.Resample(segment, p => p.RaMountPosition);
        double dt = (segment[^1].Timestamp - Epoch).TotalSeconds / 119;
        for (int i = 0; i < sampled.Length; i++) Assert.InRange(Math.Abs(i * dt - sampled[i]), 0, 1e-6);
    }

    [Fact]
    public void DriftToggleChangesRaButLeavesEveryDecPointUnchanged()
    {
        RunSta(() => {
            var session = Night(48.8, 3, -2)[0];
            foreach (var p in session.MountCurve)
                p.RaMountPosition = (p.Timestamp - session.StartTime).TotalSeconds * .1;
            var vm = new GuidingAnalyzerVM { CurrentSession = session };
            vm.ShowDriftCorrected = true;
            var corrected = vm.MountCurvePlot.Series.OfType<OxyPlot.Series.LineSeries>().ToArray();
            var guided = vm.GuidingCurvePlot.Series.OfType<OxyPlot.Series.LineSeries>().Select(s => s.Points.ToArray()).ToArray();
            vm.ShowDriftCorrected = false;
            var original = vm.MountCurvePlot.Series.OfType<OxyPlot.Series.LineSeries>().ToArray();
            var guidedAfter = vm.GuidingCurvePlot.Series.OfType<OxyPlot.Series.LineSeries>().ToArray();
            for (int i = 0; i < guided.Length; i++) Assert.Equal(guided[i], guidedAfter[i].Points);
            Assert.Equal(original[1].Points, corrected[1].Points);
            Assert.All(corrected[0].Points, p => Assert.InRange(Math.Abs(p.Y), 0, 1e-8));
            Assert.True(original[0].Points[^1].Y > 50);
        });
    }

    [Fact]
    public void DecFftRetainsTheDriftWhereRaFftRemovesIt()
    {
        var ramp = Enumerable.Range(0, 2048).Select(i => i * .01).ToArray();
        var service = new FftAnalysisService();
        var dec = service.ComputeFft(ramp, 2, GuidingAxis.Dec);
        var ra = service.ComputeFft(ramp, 2, GuidingAxis.RA);
        Assert.True(dec.Amplitudes[1] > 1);
        Assert.True(ra.Amplitudes.Max() < 1e-8);
        Assert.Empty(dec.SignificantPeaks); // a ramp is not a confirmed periodic component
    }

    [Fact]
    public void ImpactSelectionFindsTwoComponentsWithAmplitudesAndPowerContributions()
    {
        var signal = Enumerable.Range(0, 4096).Select(i =>
            2 * Math.Cos(2 * Math.PI * i / 128) + Math.Cos(2 * Math.PI * i / 32)).ToArray();
        var fft = new FftAnalysisService().ComputeFft(signal, 2, GuidingAxis.RA);
        Assert.Equal(2, fft.SignificantPeaks.Count);
        var first = fft.SignificantPeaks[0]; var second = fft.SignificantPeaks[1];
        Assert.InRange(first.PeriodSeconds, 255, 257);
        Assert.InRange(second.PeriodSeconds, 63, 65);
        Assert.InRange(first.PowerPercent, 79, 81);
        Assert.InRange(second.PowerPercent, 19, 21);
        Assert.InRange(first.RmsContributionArcsec, 1.40, 1.43);
        Assert.InRange(second.RmsContributionArcsec, .70, .72);
        Assert.InRange(first.AmplitudeArcsec, 1.99, 2.01);
        Assert.Equal(first.FrequencyCph / 3.6, first.FrequencyMilliHz);
        Assert.All(fft.SignificantPeaks, p => { Assert.True(p.PeakToNoiseRatio >= 5); Assert.True(p.CyclesObserved >= 2); });
    }

    [Fact]
    public void RandomNoiseAndTooShortAnObservationDoNotBecomeDominantFrequencies()
    {
        var random = new Random(42);
        var noise = Enumerable.Range(0, 4096).Select(_ =>
            Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble())).ToArray();
        var service = new FftAnalysisService();
        Assert.Empty(service.ComputeFft(noise, 2, GuidingAxis.RA).SignificantPeaks);
        var incomplete = Enumerable.Range(0, 1024).Select(i => Math.Cos(2 * Math.PI * i / 1500)).ToArray();
        Assert.Empty(service.ComputeFft(incomplete, 2, GuidingAxis.Dec).SignificantPeaks);
    }

    [Fact]
    public void PaddingDoesNotDuplicateOneDominantComponent()
    {
        var signal = Enumerable.Range(0, 1500).Select(i => 3 * Math.Cos(2 * Math.PI * i / 64)).ToArray();
        var peak = Assert.Single(new FftAnalysisService().ComputeFft(signal, 2, GuidingAxis.RA).SignificantPeaks);
        Assert.InRange(peak.PowerPercent, 98, 100);
        Assert.InRange(peak.RmsContributionArcsec, 2.10, 2.14);
    }

    [Fact]
    public void FftTabShowsFrequencyRowsAndKeepsTheCompleteSpectrum()
    {
        RunSta(() => {
            var session = Night(48.8, 3, -2)[0];
            var signal = Enumerable.Range(0, 4096).Select(i =>
                2 * Math.Cos(2 * Math.PI * i / 128) + Math.Cos(2 * Math.PI * i / 32)).ToArray();
            var service = new FftAnalysisService();
            session.FftRa = service.ComputeFft(signal, 2, GuidingAxis.RA);
            session.FftDec = service.ComputeFft(signal, 2, GuidingAxis.Dec);
            session.FftRaUncorrected = session.FftRa;
            session.FftDecUncorrected = session.FftDec;
            var vm = new GuidingAnalyzerVM { CurrentSession = session };
            vm.ShowDriftCorrected = true; // refresh the actual plot models
            var view = new GuidingAnalyzerView { DataContext = vm };
            var tabs = (TabControl)view.FindName("MainTabControl");
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(t => ((string)t.Header).Contains("FFT"));
            view.Measure(new Size(1400, 1000)); view.Arrange(new Rect(0, 0, 1400, 1000)); Pump();
            Assert.Equal("Frequency (mHz)", vm.FftRaPlot.Axes[0].Title);
            var series = Assert.IsType<OxyPlot.Series.AreaSeries>(vm.FftRaPlot.Series[0]);
            Assert.Equal(session.FftRa.Frequencies.Length - 1, series.Points.Count);
            var text = VisualDescendants(view).OfType<TextBlock>().Select(t => t.Text).ToArray();
            Assert.Contains("Frequency", text);
            Assert.Contains("Power share", text);
            Assert.Contains(text, t => t.Contains("3.906 mHz"));
            // An optional visual artifact uses the real WPF layout, without opening NINA.
            string? previewPath = Environment.GetEnvironmentVariable("GUIDING_ANALYZER_FFT_PREVIEW");
            if (!string.IsNullOrEmpty(previewPath))
            {
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(1400, 1000, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(view);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var output = File.Create(previewPath); encoder.Save(output);
            }
            view.DataContext = null;
        });
    }

    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in VisualDescendants(child)) yield return descendant;
        }
    }

    [Fact]
    public async Task PhdLogParserRetainsGuideDisplacementsAndMissingCoordinates()
    {
        string path = Path.Combine(Path.GetTempPath(), "GuidingAnalyzer-" + Guid.NewGuid() + ".txt");
        try
        {
            await File.WriteAllTextAsync(path, """
                Guiding Begins at 2026-10-08 20:00:00
                Pixel scale = 2.0 arc-sec/px
                Mount = Test, xAngle = 0, xRate = 4, yAngle = 90, yRate = 4, parity = +/+
                Frame,Time,mount,dx,dy,RARawDistance,DECRawDistance,RAGuideDistance,DECGuideDistance,RADuration,RADirection,DECDuration,DECDirection,XStep,YStep,StarMass,SNR,ErrorCode
                1,0,Mount,0,0,1,2,0.5,-0.25,100,W,50,N,0,0,100,20,0
                2,3,Mount,0,0,1,2,0,0,0,,0,,0,0,100,20,1
                3,6,DROP,0,0,0,0,0,0,0,,0,,0,0,0,0,2
                Guiding Ends at 2026-10-08 20:01:00
                """);
            var session = Assert.Single(await new GuidingDataService().LoadAllSessionsAsync(path));
            Assert.True(double.IsNaN(session.SessionInfo!.HourAngle));
            Assert.True(double.IsNaN(session.SessionInfo.DecDeg));
            Assert.Equal(1, session.RawPoints[0].RaGuideDistanceArcsec);
            Assert.Equal(-.5, session.RawPoints[0].DecGuideDistanceArcsec);
            Assert.True(session.RawPoints[1].IsGuideStep); // saturated star still found
            Assert.False(session.RawPoints[2].IsGuideStep);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task LogStillBeingWrittenByPhd2CanBeLoaded()
    {
        string path = Path.Combine(Path.GetTempPath(), "GuidingAnalyzer-" + Guid.NewGuid() + ".txt");
        try
        {
            // PHD2 mid-session: the log is held open for writing, there is no
            // "Guiding Ends" yet, and the newest row has only been partly written.
            using (var phd2 = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            {
                var log = """
                    Guiding Begins at 2026-10-08 20:00:00
                    Pixel scale = 2.0 arc-sec/px
                    Frame,Time,mount,dx,dy,RARawDistance,DECRawDistance,RAGuideDistance,DECGuideDistance,RADuration,RADirection,DECDuration,DECDirection,XStep,YStep,StarMass,SNR,ErrorCode
                    1,0,Mount,0,0,1,2,0.5,-0.25,100,W,50,N,0,0,100,20,0
                    2,3,Mount,0,0,1,2,0,0,0,,0,,0,0,100,20,0

                    """ + "3,6,Mount,0,0,1.";
                var bytes = System.Text.Encoding.UTF8.GetBytes(log);
                phd2.Write(bytes, 0, bytes.Length);
                phd2.Flush();

                var session = Assert.Single(await new GuidingDataService().LoadAllSessionsAsync(path));
                Assert.Equal(2, session.RawPoints.Count);
                Assert.Equal(2, session.RawPoints[1].FrameIndex);
            }
        }
        finally { File.Delete(path); }
    }

    private static readonly DateTime Epoch = new(2026, 10, 8, 20, 0, 0);
    internal static List<GuidingSession> Night(double lat, double az, double alt)
        => new[] { -3.0, 0.0, 3.0 }.Select((ha, i) => TrackingSession(lat, az, alt, ha, i == 1 ? -1 : 1, i)).ToList();

    private static GuidingSession TrackingSession(double lat, double azArcmin, double altArcmin, double ha, int parity, int index)
    {
        double rad = Math.PI / 180;
        double hemi = lat < 0 ? -1 : 1;
        // Independent rigid-body rotation of a telescope about its displaced polar axis.
        // Effective North axis is the negative South axis in the southern hemisphere.
        var axis = new DVector(hemi * altArcmin / 60 * rad,
            hemi * azArcmin / 60 * rad * Math.Cos(lat * rad), 1).Unit();
        double dec = (index - 1) * 20 * rad;
        var start = new DVector(Math.Cos(dec) * Math.Cos(ha * Math.PI / 12),
            -Math.Cos(dec) * Math.Sin(ha * Math.PI / 12), Math.Sin(dec));
        var session = new GuidingSession {
            SessionIndex = index + 1, StartTime = Epoch.AddHours(index), PixelScale = 1,
            SessionInfo = new GuidingSessionInfo { HourAngle = ha, DecDeg = dec / rad,
                MountParity = parity > 0 ? "+/+" : "+/-", GuideRateDEC = 7.5 }
        };
        double accumulated = 0;
        for (int i = 0; i < 200; i++)
        {
            double t = i * 3 + (i % 3) * .2;
            double theta = -t * 2 * Math.PI / 86164.0905;
            var tracked = start.Rotate(axis, theta);
            double celestialDrift = (dec - Math.Asin(tracked.Z)) / rad * 3600;
            double raw = parity * celestialDrift + accumulated;
            double guide = .6 * raw;
            session.RawPoints.Add(new GuidingPoint {
                FrameIndex = i, Timestamp = session.StartTime.AddSeconds(t), DecError = raw,
                DecGuideDistanceArcsec = guide, DecCorrection = -Math.Sign(guide) * Math.Abs(guide) / .0075
            });
            accumulated -= guide;
        }
        session.MountCurve = new MountCurveService().ReconstructMountCurve(session.RawPoints, 1, .0075, .0075);
        return session;
    }
    private record struct DVector(double X, double Y, double Z)
    {
        public DVector Unit() { double l = Math.Sqrt(X * X + Y * Y + Z * Z); return new(X / l, Y / l, Z / l); }
        public DVector Rotate(DVector k, double angle)
        {
            double c = Math.Cos(angle), s = Math.Sin(angle), dot = X * k.X + Y * k.Y + Z * k.Z;
            return new(X * c + (k.Y * Z - k.Z * Y) * s + k.X * dot * (1 - c),
                Y * c + (k.Z * X - k.X * Z) * s + k.Y * dot * (1 - c),
                Z * c + (k.X * Y - k.Y * X) * s + k.Z * dot * (1 - c));
        }
    }
}
