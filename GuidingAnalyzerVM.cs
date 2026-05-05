using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GuidingAnalyzer.Models;
using GuidingAnalyzer.Services;
using Microsoft.Win32;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace GuidingAnalyzer
{
    public partial class GuidingAnalyzerVM : ObservableObject
    {
        // ── Services ─────────────────────────────────────────────────────────
        private readonly GuidingDataService     _dataService         = new();
        private readonly StatisticsService      _statsService        = new();
        private readonly MountCurveService      _mountService        = new();
        private readonly FftAnalysisService     _fftService          = new();
        private readonly AnomalyDetector        _anomalyDetector     = new();
        private readonly AdviceService          _adviceService       = new();
        private readonly ExportService          _exportService       = new();
        private readonly MultiSessionService    _multiSessionService = new();
        private readonly AltitudeAnalysisService _altitudeService    = new();
        private readonly PolarAlignmentService   _polarService       = new();

        // Full file path kept internally (only the name is displayed)
        private string _fullFilePath = string.Empty;

        // ── Sessions ─────────────────────────────────────────────────────────
        [ObservableProperty] private ObservableCollection<GuidingSession> _availableSessions = new();
        [ObservableProperty] private GuidingSession? _selectedSession;
        [ObservableProperty] private bool _hasSessions = false;

        partial void OnAvailableSessionsChanged(ObservableCollection<GuidingSession> value)
        {
            HasSessions = value != null && value.Count > 0;
        }

        partial void OnSelectedSessionChanged(GuidingSession? value)
        {
            if (value != null) _ = AnalyzeSessionAsync(value);
        }

        // ── Current session ───────────────────────────────────────────────────
        [ObservableProperty] private GuidingSession? _currentSession;

        // ── UI state ─────────────────────────────────────────────────────────
        [ObservableProperty] private bool   _isLoading;
        [ObservableProperty] private string _statusMessage    = "Ready — load a PHD2 log to start.";
        [ObservableProperty] private string _selectedFilePath = string.Empty;

        // ── Displayed statistics ──────────────────────────────────────────────
        [ObservableProperty] private string _raRmsText       = "—";
        [ObservableProperty] private string _decRmsText      = "—";
        [ObservableProperty] private string _totalRmsText    = "—";
        [ObservableProperty] private string _raDriftText     = "—";
        [ObservableProperty] private string _decDriftText    = "—";
        [ObservableProperty] private string _frameCountText  = "—";
        [ObservableProperty] private string _qualityGradeText = "—";

        // ── Drift confidence ─────────────────────────────────────────────────
        [ObservableProperty] private string _raDriftConfidence      = "";
        [ObservableProperty] private string _decDriftConfidence     = "";
        [ObservableProperty] private double _raDriftConfidenceValue  = 0;
        [ObservableProperty] private double _decDriftConfidenceValue = 0;

        // ── Guiding summary (Characteristics tab) ────────────────────────────
        [ObservableProperty] private string _summaryRaRms           = "—";
        [ObservableProperty] private string _summaryDecRms          = "—";
        [ObservableProperty] private string _summaryTotalRms        = "—";
        [ObservableProperty] private string _summaryRaDrift         = "—";
        [ObservableProperty] private string _summaryDecDrift        = "—";
        [ObservableProperty] private string _summaryRaDriftConf     = "—";
        [ObservableProperty] private string _summaryDecDriftConf    = "—";
        [ObservableProperty] private string _summaryFrames          = "—";
        [ObservableProperty] private string _summaryQuality         = "—";
        // Peak-to-peak amplitude of mount curve (including raw drift)
        [ObservableProperty] private string _summaryMountRaAmplitude  = "—";
        [ObservableProperty] private string _summaryMountDecAmplitude = "—";
        // Residual amplitude after drift subtraction
        [ObservableProperty] private string _summaryMountRaResidual   = "—";
        [ObservableProperty] private string _summaryMountDecResidual  = "—";

        // ── Advanced statistics ───────────────────────────────────────────────
        [ObservableProperty] private string _summaryRaSkewness       = "—";
        [ObservableProperty] private string _summaryDecSkewness      = "—";
        [ObservableProperty] private string _summaryRaKurtosis       = "—";
        [ObservableProperty] private string _summaryDecKurtosis      = "—";
        [ObservableProperty] private string _summaryRaDecCorrelation = "—";
        [ObservableProperty] private string _summaryRaStability      = "—";
        [ObservableProperty] private string _summaryDecStability     = "—";

        // ── Altitude analysis ─────────────────────────────────────────────────
        [ObservableProperty] private PlotModel _rmsVsAltitudePlot      = null!;
        [ObservableProperty] private PlotModel _slidingRmsPlot         = null!;
        [ObservableProperty] private string _altitudeLatitudeText      = "—";
        [ObservableProperty] private bool   _altitudeMeridianFlip      = false;
        [ObservableProperty] private bool   _hasAltitudeData           = false;
        // Window
        [ObservableProperty] private string _altitudeWindowLabel       = "—";
        [ObservableProperty] private bool   _altitudeWindowFromWorm    = false;
        // Degradation diagnosis
        [ObservableProperty] private string _degradationIcon           = "➡";
        [ObservableProperty] private string _degradationMain           = string.Empty;
        [ObservableProperty] private string _degradationDetail         = string.Empty;
        [ObservableProperty] private string _degradationColor          = "#AAAACC";
        [ObservableProperty] private string _degradationPearsonText    = "—";
        [ObservableProperty] private string _degradationWestText       = "—";
        [ObservableProperty] private string _degradationEastText       = "—";
        [ObservableProperty] private string _degradationTrendText      = "—";
        [ObservableProperty] private bool   _degradationHasFlipData    = false;

        // ── Polar alignment ───────────────────────────────────────────────────

        // User parameters
        [ObservableProperty] private double _polarLatitudeDeg        = 48.8;   // IDF default
        [ObservableProperty] private double _polarArcminPerTurn      = 30.0;   // arcmin/turn

        // Main result
        [ObservableProperty] private bool   _hasPolarResult          = false;

        // Computed errors
        [ObservableProperty] private string _polarErrAzText          = "—";
        [ObservableProperty] private string _polarErrAltText         = "—";
        [ObservableProperty] private string _polarTotalText          = "—";
        [ObservableProperty] private string _polarErrAzColor         = "#AAAACC";
        [ObservableProperty] private string _polarErrAltColor        = "#AAAACC";
        [ObservableProperty] private string _polarTotalColor         = "#AAAACC";

        // Human-readable directions
        [ObservableProperty] private string _polarAzDirectionText    = "—";
        [ObservableProperty] private string _polarAltDirectionText   = "—";

        // Screw turn fractions
        [ObservableProperty] private string _polarAzTurnsText        = "—";
        [ObservableProperty] private string _polarAltTurnsText       = "—";

        // Overall quality
        [ObservableProperty] private string _polarQualityLabel       = "—";
        [ObservableProperty] private string _polarQualityColor       = "#AAAACC";
        [ObservableProperty] private string _polarQualityIcon        = "◎";

        // Conditioning
        [ObservableProperty] private string _polarConditioningText   = "—";
        [ObservableProperty] private string _polarConditioningColor  = "#AAAACC";
        [ObservableProperty] private string _polarConditioningLabel  = "—";

        // RMS residual
        [ObservableProperty] private string _polarResidualText       = "—";

        // Session count
        [ObservableProperty] private string _polarSessionsText       = "—";

        // Measurement advice
        [ObservableProperty] private string _polarAdviceText         = string.Empty;
        [ObservableProperty] private bool   _hasPolarAdvice          = false;

        // Error message if calculation not possible
        [ObservableProperty] private string _polarErrorText          = string.Empty;
        [ObservableProperty] private bool   _hasPolarError           = false;

        // Contributing sessions table
        [ObservableProperty]
        private ObservableCollection<PolarSessionRow> _polarSessionRows = new();

        // Latitude display
        [ObservableProperty] private string _polarLatitudeDisplay    = "—";

        // ── Worm gear ─────────────────────────────────────────────────────────
        [ObservableProperty] private bool   _hasWormData             = false;
        [ObservableProperty] private string _wormPeriodText          = "—";
        [ObservableProperty] private string _wormPhd2Text            = "—";
        [ObservableProperty] private string _wormAmplitudeText       = "—";
        [ObservableProperty] private string _wormSnrText             = "—";
        [ObservableProperty] private string _wormCyclesText          = "—";
        [ObservableProperty] private string _wormConfidenceText      = "—";
        [ObservableProperty] private string _wormConfidenceColor     = "#AAAACC";
        [ObservableProperty] private string _wormConfidenceDetail    = string.Empty;
        [ObservableProperty] private string _wormSourceSession       = "—";
        [ObservableProperty] private double _wormConfidencePercent   = 0;
        [ObservableProperty] private bool   _wormConsistentPhd2      = false;

        // ── Multi-session ─────────────────────────────────────────────────────
        [ObservableProperty] private ObservableCollection<SessionSummary> _allSessionSummaries   = new();
        [ObservableProperty] private ObservableCollection<string>         _loadedComparisonFiles = new();
        [ObservableProperty] private MultiSessionComparison?              _multiSessionComparison;
        [ObservableProperty] private bool                                  _hasMultiSessionData   = false;
        [ObservableProperty] private bool                                  _isLoadingComparison   = false;
        [ObservableProperty] private PlotModel _multiSessionFftRaPlot  = null!;
        [ObservableProperty] private PlotModel _multiSessionFftDecPlot = null!;

        // Scope selected via RadioButtons
        private ComparisonScope _comparisonScope = ComparisonScope.AllLoaded;

        public bool IsScopeAllLoaded
        {
            get => _comparisonScope == ComparisonScope.AllLoaded;
            set { if (value) { _comparisonScope = ComparisonScope.AllLoaded; OnPropertyChanged(nameof(IsScopeAllLoaded)); OnPropertyChanged(nameof(IsScopeSameCalibration)); RefreshComparison(); } }
        }

        public bool IsScopeSameCalibration
        {
            get => _comparisonScope == ComparisonScope.SameCalibrationGroup;
            set { if (value) { _comparisonScope = ComparisonScope.SameCalibrationGroup; OnPropertyChanged(nameof(IsScopeAllLoaded)); OnPropertyChanged(nameof(IsScopeSameCalibration)); RefreshComparison(); } }
        }

        // ── Anomalies & Advice ────────────────────────────────────────────────
        [ObservableProperty] private ObservableCollection<GuidingAnomaly> _anomalies = new();
        [ObservableProperty] private ObservableCollection<GuidingAdvice>  _advice    = new();

        // ── Curve visibility ──────────────────────────────────────────────────
        [ObservableProperty] private bool _showRa  = true;
        [ObservableProperty] private bool _showDec = true;
        [ObservableProperty] private bool _showDriftCorrected = false;

        partial void OnShowRaChanged(bool  value) { if (_currentSession != null) UpdatePlots(_currentSession); }
        partial void OnShowDecChanged(bool value) { if (_currentSession != null) UpdatePlots(_currentSession); }
        partial void OnShowDriftCorrectedChanged(bool value) { if (_currentSession != null) UpdatePlots(_currentSession); }

        // ── OxyPlot charts ────────────────────────────────────────────────────
        // IMPORTANT: each PlotModel must be a DISTINCT instance.
        // OxyPlot throws InvalidOperationException if the same model is attached
        // to two PlotViews (main view + dockable view).
        [ObservableProperty] private PlotModel _guidingCurvePlot       = null!;
        [ObservableProperty] private PlotModel _mountCurvePlot         = null!;
        [ObservableProperty] private PlotModel _fftRaPlot              = null!;
        [ObservableProperty] private PlotModel _fftDecPlot             = null!;
        [ObservableProperty] private PlotModel _fftRaUncorrectedPlot   = null!;
        [ObservableProperty] private PlotModel _fftDecUncorrectedPlot  = null!;
        [ObservableProperty] private PlotModel _calibrationPlot        = null!;

        // ── Selected FFT mode ─────────────────────────────────────────────────
        [ObservableProperty] private bool _fftShowCorrected   = true;
        [ObservableProperty] private bool _fftShowUncorrected = false;
        partial void OnFftShowCorrectedChanged(bool value)   { if (value) FftShowUncorrected = false; }
        partial void OnFftShowUncorrectedChanged(bool value) { if (value) FftShowCorrected   = false; }

        // ─────────────────────────────────────────────────────────────────────
        // CONSTRUCTOR
        // ─────────────────────────────────────────────────────────────────────

        public GuidingAnalyzerVM()
        {
            // Each PlotModel must be an independent instance —
            // OxyPlot forbids the same object being bound to two PlotViews.
            _guidingCurvePlot       = CreateEmptyPlot("PHD2 Guiding Errors");
            _mountCurvePlot         = CreateEmptyPlot("Reconstructed Mount Curve");
            _fftRaPlot              = CreateEmptyPlot("FFT Spectrum — RA (Drift-corrected)");
            _fftDecPlot             = CreateEmptyPlot("FFT Spectrum — DEC (Drift-corrected)");
            _fftRaUncorrectedPlot   = CreateEmptyPlot("FFT Spectrum — RA (Corrections Removed)");
            _fftDecUncorrectedPlot  = CreateEmptyPlot("FFT Spectrum — DEC (Corrections Removed)");
            _calibrationPlot        = CreateEmptyPlot("Calibration");
            _multiSessionFftRaPlot  = CreateEmptyPlot("FFT RA — Multi-session comparison");
            _multiSessionFftDecPlot = CreateEmptyPlot("FFT DEC — Multi-session comparison");
            _rmsVsAltitudePlot      = CreateEmptyPlot("RMS vs Altitude");
            _slidingRmsPlot         = CreateEmptyPlot("Sliding RMS and altitude");
        }

        // ─────────────────────────────────────────────────────────────────────
        // COMMANDS
        // ─────────────────────────────────────────────────────────────────────

        [RelayCommand]
        private async Task LoadFile()
        {
            var dlg = new OpenFileDialog
            {
                Title  = "Select a PHD2 log",
                Filter = "PHD2 logs (*.txt)|*.txt|All files (*.*)|*.*",
                InitialDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PHD2")
            };

            bool? result = Application.Current?.Dispatcher.Invoke(
                () => dlg.ShowDialog(Application.Current.MainWindow));
            if (result != true) return;

            // Display only the file name; keep the full path privately
            SelectedFilePath = Path.GetFileName(dlg.FileName);
            _fullFilePath    = dlg.FileName;
            IsLoading        = true;
            StatusMessage    = "Loading file…";

            try
            {
                var sessions = await _dataService.LoadAllSessionsAsync(_fullFilePath);
                AvailableSessions.Clear();
                foreach (var s in sessions) AvailableSessions.Add(s);
                HasSessions = sessions.Count > 0;

                if (sessions.Count == 0)
                {
                    StatusMessage = "No guiding session found in this file.";
                    return;
                }

                StatusMessage = $"{sessions.Count} session(s) found — pre-computing curves…";

                // Pre-compute the MountCurve for ALL sessions in the background.
                // This ensures the Polar Alignment tab has real drift data
                // (mount curve) from the first display, without waiting for the
                // user to click each session individually.
                await Task.Run(() =>
                {
                    foreach (var s in sessions)
                    {
                        if (s.MountCurve.Count > 0) continue; // already computed
                        double decRate = (s.SessionInfo?.GuideRateDEC ?? 0) / 1000.0;
                        s.MountCurve = _mountService.ReconstructMountCurve(
                            s.RawPoints, s.PixelScale,
                            s.GuidingRateArcsecPerMs, decRate);
                    }
                });

                StatusMessage = $"{sessions.Count} session(s) found — select one.";

                // Automatically select the longest session.
                // Multi-session sync will occur after AnalyzeSessionAsync (FFT available).
                SelectedSession = sessions.OrderByDescending(s => s.RawPoints.Count)
                                          .First();
            }
            catch (Exception ex)
            {
                StatusMessage = $"Load error: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        [RelayCommand]
        private void ExportCsv()
        {
            if (CurrentSession == null) { StatusMessage = "No session loaded."; return; }

            var dlg = new SaveFileDialog
            {
                Title      = "Export to CSV",
                Filter     = "CSV files (*.csv)|*.csv",
                FileName   = Path.GetFileNameWithoutExtension(CurrentSession.SourceFile) + "_export.csv",
                DefaultExt = "csv"
            };

            if (dlg.ShowDialog(Application.Current.MainWindow) != true) return;

            try
            {
                _exportService.ExportToCsv(CurrentSession, dlg.FileName);
                StatusMessage = $"CSV exported: {dlg.FileName}";
            }
            catch (Exception ex)
            {
                StatusMessage = $"CSV export error: {ex.Message}";
            }
        }

        [RelayCommand]
        private void ExportJson()
        {
            if (CurrentSession == null) { StatusMessage = "No session loaded."; return; }

            var dlg = new SaveFileDialog
            {
                Title      = "Export to JSON",
                Filter     = "JSON files (*.json)|*.json",
                FileName   = Path.GetFileNameWithoutExtension(CurrentSession.SourceFile) + "_analysis.json",
                DefaultExt = "json"
            };

            if (dlg.ShowDialog(Application.Current.MainWindow) != true) return;

            try
            {
                _exportService.ExportToJson(CurrentSession, dlg.FileName);
                StatusMessage = $"JSON exported: {dlg.FileName}";
            }
            catch (Exception ex)
            {
                StatusMessage = $"JSON export error: {ex.Message}";
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // MAIN ANALYSIS
        // ─────────────────────────────────────────────────────────────────────

        private async Task AnalyzeSessionAsync(GuidingSession session)
        {
            IsLoading     = true;
            StatusMessage = $"Analysing session #{session.SessionIndex} ({session.RawPoints.Count} frames)…";

            try
            {
                // All heavy computation in Task.Run (no UI access here)
                await Task.Run(() =>
                {
                    // Filter dithering/settling points for statistics
                    var cleanRawPoints = session.RawPoints.Where(p => !p.IsDitherOrSettling).ToList();
                    session.Statistics = _statsService.ComputeStatistics(cleanRawPoints);

                    // Mount curve: uses the rate read from the PHD2 log
                    // (GuidingRateArcsecPerMs = 0 if not found → fallback 0.5× sidereal in MountCurveService)
                    double decRateArcsecPerMs = (session.SessionInfo?.GuideRateDEC ?? 0) / 1000.0;
                    session.MountCurve = _mountService.ReconstructMountCurve(
                        session.RawPoints, session.PixelScale,
                        session.GuidingRateArcsecPerMs,
                        decRateArcsecPerMs);

                    if (session.MountCurve.Count > 16)
                    {
                        // Filtered signals (no dithers) for "Drift-corrected" FFT
                        double[] raSignal  = _mountService.GetRaSignal(session.MountCurve);
                        double[] decSignal = _mountService.GetDecSignal(session.MountCurve);

                        // Signals without PHD2 corrections = raw mount behaviour
                        double[] raUncorr  = _mountService.GetRaUncorrectedSignal(session.RawPoints);
                        double[] decUncorr = _mountService.GetDecUncorrectedSignal(session.RawPoints);

                        double samplingPeriod = cleanRawPoints.Count > 1
                            ? (cleanRawPoints[^1].Timestamp - cleanRawPoints[0].Timestamp)
                            .TotalSeconds / cleanRawPoints.Count
                            : 2.5;

                        session.FftRa  = _fftService.ComputeFft(raSignal,  samplingPeriod, GuidingAxis.RA);
                        session.FftDec = _fftService.ComputeFft(decSignal, samplingPeriod, GuidingAxis.Dec);
                        session.FftRaUncorrected  = _fftService.ComputeFft(raUncorr,  samplingPeriod, GuidingAxis.RA);
                        session.FftDecUncorrected = _fftService.ComputeFft(decUncorr, samplingPeriod, GuidingAxis.Dec);
                    }

                    // Anomaly detection
                    session.Anomalies = _anomalyDetector.Detect(session);
                    session.Advice    = _adviceService.GenerateAdvice(session);
                });

                // UI updates after Task.Run (UI thread)
                CurrentSession = session;
                UpdateStatisticsDisplay(session);
                UpdatePlots(session);
                UpdateAnomalies(session);
                UpdateAdvice(session);

                // Update multi-session comparison with this analysed session
                // (called HERE after Task.Run so FftRa/FftDec are available)
                if (!string.IsNullOrEmpty(_fullFilePath))
                    await SyncMainFileToComparisonAsync(_fullFilePath,
                        AvailableSessions.ToList());

                // RMS vs Altitude analysis
                UpdateAltitudeAnalysis(session);

                // Polar alignment — recompute from all available sessions
                UpdatePolarAlignment();

                StatusMessage = $"Analysis complete — {session.RawPoints.Count} frames, " +
                                $"total RMS: {session.Statistics?.TotalRms:F2}\"";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // DISPLAY UPDATES
        // ─────────────────────────────────────────────────────────────────────

        private void UpdateStatisticsDisplay(GuidingSession session)
        {
            var s = session.Statistics;
            if (s == null) return;

            RaRmsText        = $"{s.RaRms:F2}\"";
            DecRmsText       = $"{s.DecRms:F2}\"";
            TotalRmsText     = $"{s.TotalRms:F2}\"";
            FrameCountText   = $"{s.FrameCount} frames ({s.ValidFramePercent:F0}% valid)";
            QualityGradeText = $"{s.QualityGrade} — {s.QualityDescription}";

            // Characteristics tab summary
            SummaryRaRms    = $"{s.RaRms:F2}\"";
            SummaryDecRms   = $"{s.DecRms:F2}\"";
            SummaryTotalRms = $"{s.TotalRms:F2}\"";
            SummaryFrames   = $"{s.FrameCount} frames ({s.ValidFramePercent:F0}% valid)";
            SummaryQuality  = $"{s.QualityGrade} — {s.QualityDescription}";

            // Drift on reconstructed curve (clean points only)
            var cleanMount = session.MountCurve.Where(p => !p.IsDitherOrSettling).ToList();
            if (cleanMount.Count > 10)
            {
                var drift = _statsService.ComputeDriftOnMountCurve(cleanMount);
                RaDriftText             = $"{drift.RaDriftArcsecPerMin:+0.00;-0.00;0.00}\"/min";
                DecDriftText            = $"{drift.DecDriftArcsecPerMin:+0.00;-0.00;0.00}\"/min";
                RaDriftConfidence       = drift.RaConfidenceLabel;
                DecDriftConfidence      = drift.DecConfidenceLabel;
                RaDriftConfidenceValue  = drift.RaConfidence;
                DecDriftConfidenceValue = drift.DecConfidence;
                SummaryRaDrift          = RaDriftText;
                SummaryDecDrift         = DecDriftText;
                SummaryRaDriftConf      = drift.RaConfidenceLabel;
                SummaryDecDriftConf     = drift.DecConfidenceLabel;

                // Robust amplitude on clean mount curve
                static (double slope, double intercept) LinReg(IList<double> y)
                {
                    double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
                    int cnt = y.Count;
                    for (int i = 0; i < cnt; i++)
                    { sumX += i; sumY += y[i]; sumXY += i * y[i]; sumX2 += (double)i * i; }
                    double d = cnt * sumX2 - sumX * sumX;
                    if (Math.Abs(d) < 1e-10) return (0, sumY / cnt);
                    double sl = (cnt * sumXY - sumX * sumY) / d;
                    return (sl, (sumY - sl * sumX) / cnt);
                }
                static double RobustAmplitude(IList<double> y)
                {
                    var sorted = y.OrderBy(v => v).ToList();
                    int n = sorted.Count;
                    int bucket = Math.Max(1, n / 10);
                    return sorted.Skip(n - bucket).Take(bucket).Average()
                         - sorted.Take(bucket).Average();
                }

                var raVals  = cleanMount.Select(p => p.RaMountPosition).ToList();
                var decVals = cleanMount.Select(p => p.DecMountPosition).ToList();
                var (raSlope,  raInt)  = LinReg(raVals);
                var (decSlope, decInt) = LinReg(decVals);
                var raDetrended  = raVals.Select( (v, i) => v - (raSlope  * i + raInt)).ToList();
                var decDetrended = decVals.Select((v, i) => v - (decSlope * i + decInt)).ToList();

                // Useful amplitude = oscillations after drift subtraction (residual)
                // Raw pk-pk = total extent including drift (secondary info)
                double raAmp   = RobustAmplitude(raDetrended);
                double decAmp  = RobustAmplitude(decDetrended);
                double raRaw   = RobustAmplitude(raVals);
                double decRaw  = RobustAmplitude(decVals);

                SummaryMountRaAmplitude  = $"{raAmp:F2}\"";
                SummaryMountDecAmplitude = $"{decAmp:F2}\"";
                SummaryMountRaResidual   = $"{raRaw:F2}\"";
                SummaryMountDecResidual  = $"{decRaw:F2}\"";

                int ditherCount = session.MountCurve.Count - cleanMount.Count;
                if (ditherCount > 0)
                {
                    SummaryMountRaAmplitude  += $" ({ditherCount} dither fr. excluded)";
                    SummaryMountDecAmplitude += $" ({ditherCount} dither fr. excluded)";
                }
            }
            else
            {
                RaDriftText        = $"{s.RaDriftArcsecPerMin:F2}\"/min";
                DecDriftText       = $"{s.DecDriftArcsecPerMin:F2}\"/min";
                RaDriftConfidence  = "N/A";
                DecDriftConfidence = "N/A";
                SummaryRaDrift     = "N/A (short session)";
                SummaryDecDrift    = "N/A (short session)";
            }

            // ── Advanced statistics ──────────────────────────────────────────
            SummaryRaSkewness       = FormatSkew(s.RaSkewness);
            SummaryDecSkewness      = FormatSkew(s.DecSkewness);
            SummaryRaKurtosis       = FormatKurt(s.RaKurtosis);
            SummaryDecKurtosis      = FormatKurt(s.DecKurtosis);
            SummaryRaDecCorrelation = FormatCorr(s.RaDecCorrelation);
            SummaryRaStability      = FormatStab(s.RaStabilityRatio);
            SummaryDecStability     = FormatStab(s.DecStabilityRatio);
        }

        private static string FormatSkew(double v) =>
            $"{v:+0.00;-0.00;0.00}  {(v switch { > 1 => "▲ strong", > 0.5 => "▲ moderate", < -1 => "▼ strong", < -0.5 => "▼ moderate", _ => "≈ symmetric" })}";
        private static string FormatKurt(double v) =>
            $"{v:+0.00;-0.00;0.00}  {(v switch { > 2 => "⚠ Frequent and large peaks — check balance, backlash or seeing", > 1 => "△ Some notable peaks — monitor min-move", < -1 => "✓ Very uniform distribution — very regular guiding", _ => "≈ Regular distribution — normal behaviour" })}";
        private static string FormatCorr(double v) =>
            $"{v:+0.00;-0.00;0.00}  {(Math.Abs(v) switch { > 0.7 => "⚠ likely flexure", > 0.5 => "moderate correlation", > 0.3 => "weak correlation", _ => "✓ independent" })}";
        private static string FormatStab(double v) =>
            $"{v:F2}×  {(v switch { > 2.0 => "⚠ strong degradation", > 1.5 => "⚠ degradation detected", < 0.5 => "↑ improvement", _ => "✓ stable" })}";

        private void UpdateAnomalies(GuidingSession session)
        {
            Anomalies.Clear();
            foreach (var a in session.Anomalies)
                Anomalies.Add(a);
        }

        private void UpdateAdvice(GuidingSession session)
        {
            Advice.Clear();
            foreach (var a in session.Advice)
                Advice.Add(a);
        }

        private void UpdatePlots(GuidingSession session)
        {
            GuidingCurvePlot = BuildGuidingCurvePlot(session);
            MountCurvePlot   = BuildMountCurvePlot(session);
            FftRaPlot        = BuildFftPlot(session.FftRa,             "FFT Spectrum — RA (Drift-corrected)",    OxyColors.SteelBlue);
            FftDecPlot       = BuildFftPlot(session.FftDec,            "FFT Spectrum — DEC (Drift-corrected)",   OxyColors.Orange);
            FftRaUncorrectedPlot  = BuildFftPlot(session.FftRaUncorrected,  "FFT Spectrum — RA (Corrections Removed)",  OxyColors.SteelBlue);
            FftDecUncorrectedPlot = BuildFftPlot(session.FftDecUncorrected, "FFT Spectrum — DEC (Corrections Removed)", OxyColors.Orange);
            CalibrationPlot  = BuildCalibrationPlot(session.Calibration);
        }

        /// <summary>
        /// Computes and displays the polar alignment error from all sessions
        /// loaded in AvailableSessions.
        /// Called after each load/session change.
        /// </summary>
        private void UpdatePolarAlignment()
        {
            var sessions = AvailableSessions.ToList();
            if (sessions.Count == 0)
            {
                HasPolarResult  = false;
                PolarErrorText  = "No session loaded.";
                HasPolarError   = true;
                return;
            }

            try
            {
                var result = _polarService.Compute(sessions, PolarLatitudeDeg, PolarArcminPerTurn);

                // ── Sessions table ────────────────────────────────────────────
                PolarSessionRows.Clear();
                foreach (var row in result.SessionRows)
                    PolarSessionRows.Add(row);

                if (!result.IsValid)
                {
                    HasPolarResult = false;
                    PolarErrorText = result.ErrorMessage;
                    HasPolarError  = true;
                    return;
                }

                HasPolarError  = false;
                HasPolarResult = true;

                // ── Azimuth errors ────────────────────────────────────────────
                PolarErrAzText       = $"{Math.Abs(result.ErrAzArcmin):F2} arcmin";
                PolarAzDirectionText  = result.AzDirection;
                PolarErrAzColor      = result.QualityColor;
                PolarAzTurnsText     = $"{result.AzTurns(PolarArcminPerTurn):F2} turn(s)";

                // ── Altitude errors ───────────────────────────────────────────
                PolarErrAltText       = $"{Math.Abs(result.ErrAltArcmin):F2} arcmin";
                PolarAltDirectionText  = result.AltDirection;
                PolarErrAltColor      = result.QualityColor;
                PolarAltTurnsText     = $"{result.AltTurns(PolarArcminPerTurn):F2} turn(s)";

                // ── Total error ───────────────────────────────────────────────
                PolarTotalText  = $"{result.TotalArcmin:F2} arcmin";
                PolarTotalColor = result.QualityColor;

                // ── Overall quality ───────────────────────────────────────────
                PolarQualityLabel = result.QualityLabel;
                PolarQualityColor = result.QualityColor;
                PolarQualityIcon  = result.Quality switch
                {
                    PolarAlignmentQuality.Excellent => "✓",
                    PolarAlignmentQuality.Good      => "●",
                    PolarAlignmentQuality.Fair      => "▲",
                    _                               => "✗"
                };

                // ── Conditioning ──────────────────────────────────────────────
                PolarConditioningText  = $"{result.Conditioning:F0}";
                PolarConditioningLabel = result.ConditioningLabel;
                PolarConditioningColor = result.ConditioningColor;

                // ── RMS residual ──────────────────────────────────────────────
                PolarResidualText = $"{result.ResidualRmsMasPerMin:F0} mas/min";

                // ── Session counter ───────────────────────────────────────────
                PolarSessionsText = result.ExcludedCount > 0
                    ? $"{result.SessionCount} sessions used ({result.ExcludedCount} excluded)"
                    : $"{result.SessionCount} sessions used";

                // ── Measurement advice ────────────────────────────────────────
                PolarAdviceText  = result.MeasurementAdvice;
                HasPolarAdvice   = !string.IsNullOrEmpty(result.MeasurementAdvice);

                // ── Latitude display ──────────────────────────────────────────
                PolarLatitudeDisplay = $"{PolarLatitudeDeg:F1}°N (user parameter)";

                StatusMessage = $"Polar alignment: total error {result.TotalArcmin:F2} arcmin — {result.QualityLabel}";
            }
            catch (Exception ex)
            {
                HasPolarResult = false;
                PolarErrorText = $"Calculation error: {ex.Message}";
                HasPolarError  = true;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // OXYPLOT CHART CONSTRUCTION
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Creates a scrollable and zoomable X axis.</summary>
        private static LinearAxis MakeXAxis(string title) => new()
        {
            Position           = AxisPosition.Bottom,
            Title              = title,
            TextColor          = OxyColors.LightGray,
            TitleColor         = OxyColors.LightGray,
            TicklineColor      = OxyColors.Gray,
            MajorGridlineStyle = LineStyle.Dot,
            MajorGridlineColor = OxyColor.FromAColor(40, OxyColors.LightGray),
            IsZoomEnabled      = true,
            IsPanEnabled       = true,
        };

        /// <summary>Creates a Y axis with dotted horizontal grid lines (major + minor).</summary>
        private static LinearAxis MakeYAxis(string title, bool positiveOnly = false) => new()
        {
            Position           = AxisPosition.Left,
            Title              = title,
            TextColor          = OxyColors.LightGray,
            TitleColor         = OxyColors.LightGray,
            TicklineColor      = OxyColors.Gray,
            MajorGridlineStyle = LineStyle.Dash,
            MajorGridlineColor = OxyColor.FromAColor(80, OxyColors.LightGray),
            MinorGridlineStyle = LineStyle.Dot,
            MinorGridlineColor = OxyColor.FromAColor(35, OxyColors.LightGray),
            MajorStep          = double.NaN,
            MinorStep          = double.NaN,
            IsZoomEnabled      = true,
            IsPanEnabled       = true,
            Minimum            = positiveOnly ? 0 : double.NaN,
        };

        /// <summary>Adds a white horizontal line at y=0.</summary>
        private static void AddZeroLine(PlotModel model) =>
            model.Annotations.Add(new LineAnnotation
            {
                Type            = LineAnnotationType.Horizontal,
                Y               = 0,
                Color           = OxyColor.FromAColor(180, OxyColors.White),
                LineStyle       = LineStyle.Solid,
                StrokeThickness = 1,
            });

        /// <summary>
        /// Creates the main X axis displaying both frame index AND real time.
        /// Uses a single axis with a LabelFormatter that converts frame indices
        /// to "Frame NNNN\nHH:mm" using an exact timestamp lookup from the log.
        /// </summary>
        private static LinearAxis MakeFrameTimeAxis(GuidingSession session)
        {
            var pts = session.RawPoints;
            if (pts.Count < 2)
                return MakeXAxis("Frame");

            var frameTimes = new Dictionary<int, double>(pts.Count);
            DateTime t0   = pts[0].Timestamp;
            int      frame0 = pts[0].FrameIndex;
            int      frameN = pts[^1].FrameIndex;
            double   totalSec = (pts[^1].Timestamp - t0).TotalSeconds;

            foreach (var p in pts)
                frameTimes[p.FrameIndex] = (p.Timestamp - t0).TotalSeconds;

            return new LinearAxis
            {
                Position           = AxisPosition.Bottom,
                Title              = "Frame  /  Time",
                TextColor          = OxyColors.LightGray,
                TitleColor         = OxyColors.LightGray,
                TicklineColor      = OxyColors.Gray,
                MajorGridlineStyle = LineStyle.Dot,
                MajorGridlineColor = OxyColor.FromAColor(40, OxyColors.LightGray),
                IsZoomEnabled      = true,
                IsPanEnabled       = true,
                MajorStep          = double.NaN,
                LabelFormatter     = frameVal =>
                {
                    int fi = (int)Math.Round(frameVal);

                    double sec;
                    if (frameTimes.TryGetValue(fi, out sec))
                    {
                        // exact frame
                    }
                    else
                    {
                        int lo = fi, hi = fi;
                        while (lo >= frame0 && !frameTimes.ContainsKey(lo)) lo--;
                        while (hi <= frameN && !frameTimes.ContainsKey(hi)) hi++;

                        if      (lo < frame0)  sec = 0;
                        else if (hi > frameN)  sec = totalSec;
                        else
                        {
                            double tLo = frameTimes[lo], tHi = frameTimes[hi];
                            double frac = (hi == lo) ? 0 : (double)(fi - lo) / (hi - lo);
                            sec = tLo + frac * (tHi - tLo);
                        }
                    }

                    string time = t0.AddSeconds(sec).ToString("HH:mm");
                    return $"{fi}\n{time}";
                },
            };
        }

        private PlotModel BuildGuidingCurvePlot(GuidingSession session)
        {
            var model = CreateDarkPlot("PHD2 Guiding Errors");
            model.Axes.Add(MakeFrameTimeAxis(session));
            model.Axes.Add(MakeYAxis("Error (arcsec)"));
            AddZeroLine(model);

            var raSeries      = new LineSeries { Title = "RA",  Color = OxyColors.SteelBlue, StrokeThickness = 1, MarkerType = MarkerType.None, IsVisible = ShowRa };
            var decSeries     = new LineSeries { Title = "DEC", Color = OxyColors.Orange,    StrokeThickness = 1, MarkerType = MarkerType.None, IsVisible = ShowDec };
            var ditherSeries  = new LineSeries { Title = "Dither/Settling", Color = OxyColors.Gray, StrokeThickness = 1, MarkerType = MarkerType.None, LineStyle = LineStyle.Dot };

            foreach (var p in session.RawPoints)
            {
                if (p.IsDitherOrSettling)
                {
                    ditherSeries.Points.Add(new DataPoint(p.FrameIndex, p.RaError));
                    raSeries.Points.Add(new DataPoint(p.FrameIndex, double.NaN));
                    decSeries.Points.Add(new DataPoint(p.FrameIndex, double.NaN));
                }
                else
                {
                    raSeries.Points.Add(new DataPoint(p.FrameIndex, p.RaError));
                    decSeries.Points.Add(new DataPoint(p.FrameIndex, p.DecError));
                    ditherSeries.Points.Add(new DataPoint(p.FrameIndex, double.NaN));
                }
            }

            model.Series.Add(raSeries);
            model.Series.Add(decSeries);
            model.Series.Add(ditherSeries);

            CenterYAxis(model);
            return model;
        }

        private PlotModel BuildMountCurvePlot(GuidingSession session)
        {
            var model = CreateDarkPlot("Reconstructed Mount Curve");
            model.Axes.Add(MakeFrameTimeAxis(session));
            model.Axes.Add(MakeYAxis("Position (arcsec)"));
            AddZeroLine(model);

            var raSeries     = new LineSeries { Title = "RA mount",        Color = OxyColors.CornflowerBlue, StrokeThickness = 1, MarkerType = MarkerType.None, IsVisible = ShowRa };
            var decSeries    = new LineSeries { Title = "DEC mount",       Color = OxyColors.Coral,          StrokeThickness = 1, MarkerType = MarkerType.None, IsVisible = ShowDec };
            var ditherSeries = new LineSeries { Title = "Dither/Settling", Color = OxyColors.Gray,           StrokeThickness = 1, MarkerType = MarkerType.None, LineStyle = LineStyle.Dot };

            int n = session.MountCurve.Count;
            if (n == 0) return model;

            // ── Optional drift correction ─────────────────────────────────────
            double raSlopePerClean = 0, raIntercept = 0;
            double decSlopePerClean = 0, decIntercept = 0;
            if (ShowDriftCorrected)
            {
                var cleanPoints = session.MountCurve.Where(p => !p.IsDitherOrSettling).ToList();
                if (cleanPoints.Count > 2)
                {
                    static (double slope, double intercept) LinReg(IList<MountCurvePoint> pts, Func<MountCurvePoint, double> selector)
                    {
                        double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
                        int cnt = pts.Count;
                        for (int k = 0; k < cnt; k++)
                        {
                            double x = k, y = selector(pts[k]);
                            sumX += x; sumY += y; sumXY += x * y; sumX2 += x * x;
                        }
                        double denom = cnt * sumX2 - sumX * sumX;
                        if (Math.Abs(denom) < 1e-10) return (0, sumY / cnt);
                        double sl = (cnt * sumXY - sumX * sumY) / denom;
                        return (sl, (sumY - sl * sumX) / cnt);
                    }
                    (raSlopePerClean,  raIntercept)  = LinReg(cleanPoints, p => p.RaMountPosition);
                    (decSlopePerClean, decIntercept) = LinReg(cleanPoints, p => p.DecMountPosition);
                }
            }

            // ── Populate series ───────────────────────────────────────────────
            int cleanIdx = 0;
            double lastRaVal = 0;

            foreach (var p in session.MountCurve)
            {
                if (p.IsDitherOrSettling)
                {
                    ditherSeries.Points.Add(new DataPoint(p.FrameIndex, lastRaVal));
                    raSeries.Points.Add(new DataPoint(p.FrameIndex, double.NaN));
                    decSeries.Points.Add(new DataPoint(p.FrameIndex, double.NaN));
                }
                else
                {
                    double rv = p.RaMountPosition;
                    double dv = p.DecMountPosition;
                    if (ShowDriftCorrected)
                    {
                        rv -= raSlopePerClean  * cleanIdx + raIntercept;
                        dv -= decSlopePerClean * cleanIdx + decIntercept;
                    }
                    raSeries.Points.Add(new DataPoint(p.FrameIndex, rv));
                    decSeries.Points.Add(new DataPoint(p.FrameIndex, dv));
                    ditherSeries.Points.Add(new DataPoint(p.FrameIndex, double.NaN));
                    lastRaVal = rv;
                    cleanIdx++;
                }
            }

            model.Series.Add(raSeries);
            model.Series.Add(decSeries);
            model.Series.Add(ditherSeries);

            CenterYAxis(model);
            return model;
        }

        private static PlotModel BuildFftPlot(FftResult? fft, string title, OxyColor color)
        {
            var model = CreateDarkPlot(title);

            // ── X axis: periods in seconds, LOGARITHMIC scale ─────────────────
            var xAxis = new OxyPlot.Axes.LogarithmicAxis
            {
                Position           = AxisPosition.Bottom,
                Title              = "Period (seconds)",
                Minimum            = 5,
                Maximum            = 6000,
                TextColor          = OxyColors.LightGray,
                TitleColor         = OxyColors.LightGray,
                TicklineColor      = OxyColors.Gray,
                MajorGridlineStyle = LineStyle.Dot,
                MajorGridlineColor = OxyColor.FromAColor(40, OxyColors.LightGray),
                MinorGridlineStyle = LineStyle.Dot,
                MinorGridlineColor = OxyColor.FromAColor(20, OxyColors.LightGray),
                IsZoomEnabled      = true,
                IsPanEnabled       = true,
                MajorStep          = double.NaN,
            };
            model.Axes.Add(xAxis);

            // ── Y axis: amplitude in arcsec, ZOOM DISABLED ────────────────────
            var yAxis = new LinearAxis
            {
                Position           = AxisPosition.Left,
                Title              = "Amplitude (\")",
                Minimum            = 0,
                TextColor          = OxyColors.LightGray,
                TitleColor         = OxyColors.LightGray,
                TicklineColor      = OxyColors.Gray,
                MajorGridlineStyle = LineStyle.Dot,
                MajorGridlineColor = OxyColor.FromAColor(40, OxyColors.LightGray),
                IsZoomEnabled      = false,
                IsPanEnabled       = false,
            };
            model.Axes.Add(yAxis);

            if (fft == null || fft.Frequencies.Length == 0) return model;

            // ── Main series in period (seconds) ───────────────────────────────
            var series = new AreaSeries
            {
                Color           = color,
                Fill            = OxyColor.FromAColor(55, color),
                StrokeThickness = 1.2,
            };

            for (int i = 1; i < fft.Frequencies.Length; i++)
            {
                double freqCph = fft.Frequencies[i];
                if (freqCph <= 0) continue;
                double periodSec = 3600.0 / freqCph;
                if (periodSec < 4 || periodSec > 7200) continue;
                series.Points.Add(new DataPoint(periodSec, fft.Amplitudes[i]));
            }
            model.Series.Add(series);

            // ── Significant peak annotations ──────────────────────────────────
            foreach (var peak in fft.SignificantPeaks)
            {
                if (peak.FrequencyCph <= 0) continue;
                double periodSec = 3600.0 / peak.FrequencyCph;
                if (periodSec < 4 || periodSec > 7200) continue;

                model.Annotations.Add(new LineAnnotation
                {
                    Type            = LineAnnotationType.Vertical,
                    X               = periodSec,
                    Color           = OxyColors.Yellow,
                    LineStyle       = LineStyle.Dash,
                    StrokeThickness = 1,
                    Text            = periodSec >= 60
                        ? $"{periodSec / 60:F1}min  {peak.AmplitudeArcsec:F2}\""
                        : $"{periodSec:F0}s  {peak.AmplitudeArcsec:F2}\"",
                    TextColor       = OxyColors.Yellow,
                    FontSize        = 9,
                    TextOrientation = AnnotationTextOrientation.Vertical,
                    TextPosition    = new DataPoint(0, 0.92),
                });
            }

            // ── Reference lines for known periods ─────────────────────────────
            void AddRefLine(double periodSec, string label, OxyColor refColor)
            {
                if (periodSec < xAxis.Minimum || periodSec > xAxis.Maximum) return;
                model.Annotations.Add(new LineAnnotation
                {
                    Type            = LineAnnotationType.Vertical,
                    X               = periodSec,
                    Color           = OxyColor.FromAColor(50, refColor),
                    LineStyle       = LineStyle.Dot,
                    StrokeThickness = 1,
                    Text            = label,
                    TextColor       = OxyColor.FromAColor(120, refColor),
                    FontSize        = 8,
                    TextOrientation = AnnotationTextOrientation.Vertical,
                    TextPosition    = new DataPoint(0, 0.05),
                });
            }
            if (fft.SamplingPeriodSeconds > 0)
            {
                AddRefLine(480,  "8 min",  OxyColors.LightBlue);
                AddRefLine(600,  "10 min", OxyColors.LightBlue);
                AddRefLine(300,  "5 min",  OxyColors.LightGreen);
            }

            return model;
        }

        private static PlotModel BuildCalibrationPlot(CalibrationSession? cal)
        {
            var model = CreateDarkPlot("");
            model.PlotType = PlotType.Cartesian;
            model.Axes.Add(MakeXAxis("dx (pixels)"));
            var yAxis = MakeYAxis("dy (pixels)");
            yAxis.MinimumPadding = 0.10;
            yAxis.MaximumPadding = 0.20;
            model.Axes.Add(yAxis);
            AddZeroLine(model);
            model.Annotations.Add(new LineAnnotation
            {
                Type = LineAnnotationType.Vertical, X = 0,
                Color = OxyColor.FromAColor(180, OxyColors.White),
                LineStyle = LineStyle.Solid, StrokeThickness = 1
            });

            if (cal == null) return model;

            void AddCalSeries(string seriesTitle, OxyColor color,
                IList<CalibrationPoint> points, bool dashed = false)
            {
                if (points.Count == 0) return;

                var scatter = new ScatterSeries
                {
                    Title = seriesTitle, MarkerType = MarkerType.Circle,
                    MarkerSize = 4, MarkerFill = color
                };
                foreach (var p in points)
                    scatter.Points.Add(new ScatterPoint(p.Dx, p.Dy));
                model.Series.Add(scatter);

                if (points.Count >= 2)
                {
                    double n = points.Count;
                    double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
                    foreach (var p in points)
                    {
                        double dy = p.Dy;
                        sumX  += p.Dx; sumY  += dy;
                        sumXY += p.Dx * dy; sumX2 += p.Dx * p.Dx;
                    }
                    double denom = n * sumX2 - sumX * sumX;
                    if (Math.Abs(denom) > 1e-10)
                    {
                        double a = (n * sumXY - sumX * sumY) / denom;
                        double b = (sumY - a * sumX) / n;
                        double xMin = double.MaxValue, xMax = double.MinValue;
                        foreach (var p in points)
                        {
                            if (p.Dx < xMin) xMin = p.Dx;
                            if (p.Dx > xMax) xMax = p.Dx;
                        }
                        var trend = new LineSeries
                        {
                            Color = color, StrokeThickness = 1.5,
                            LineStyle = dashed ? LineStyle.Dash : LineStyle.Solid,
                            MarkerType = MarkerType.None
                        };
                        trend.Points.Add(new DataPoint(xMin, a * xMin + b));
                        trend.Points.Add(new DataPoint(xMax, a * xMax + b));
                        model.Series.Add(trend);
                    }
                }
            }

            AddCalSeries("West (RA)",  OxyColors.SteelBlue,                                      cal.RaWestPoints);
            AddCalSeries("East (RA)",  OxyColor.FromAColor(180, OxyColors.SteelBlue),             cal.RaEastPoints,   true);
            AddCalSeries("North (DEC)", OxyColors.Orange,                                         cal.DecNorthPoints);
            AddCalSeries("South (DEC)", OxyColor.FromAColor(180, OxyColors.Orange),               cal.DecSouthPoints, true);
            if (cal.BacklashPoints.Count > 0)
                AddCalSeries("Backlash", OxyColors.Red, cal.BacklashPoints, true);

            return model;
        }

        // ─────────────────────────────────────────────────────────────────────
        // MULTI-SESSION COMMANDS
        // ─────────────────────────────────────────────────────────────────────

        [RelayCommand]
        private async Task AddComparisonFile()
        {
            var dlg = new OpenFileDialog
            {
                Title       = "Add a PHD2 log for comparison",
                Filter      = "PHD2 logs (*.txt)|*.txt|All files (*.*)|*.*",
                Multiselect = true,
                InitialDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PHD2")
            };

            bool? result = Application.Current?.Dispatcher.Invoke(
                () => dlg.ShowDialog(Application.Current.MainWindow));
            if (result != true) return;

            IsLoadingComparison = true;
            try
            {
                foreach (string file in dlg.FileNames)
                {
                    if (_multiSessionService.LoadedFilePaths.Contains(file)) continue;
                    await _multiSessionService.AddFileAsync(file);
                    string fname = Path.GetFileName(file);
                    if (!LoadedComparisonFiles.Contains(fname))
                        LoadedComparisonFiles.Add(fname);
                }
                await RefreshSummariesAndComparisonAsync();
                StatusMessage = $"Comparison: {AllSessionSummaries.Count} sessions across {LoadedComparisonFiles.Count} file(s).";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Comparison load error: {ex.Message}";
            }
            finally
            {
                IsLoadingComparison = false;
            }
        }

        [RelayCommand]
        private async Task RemoveComparisonFile(string fileName)
        {
            string? fullPath = _multiSessionService.LoadedFilePaths
                .FirstOrDefault(p => Path.GetFileName(p) == fileName);
            if (fullPath == null) return;

            _multiSessionService.RemoveFile(fullPath);
            LoadedComparisonFiles.Remove(fileName);
            await RefreshSummariesAndComparisonAsync();
        }

        [RelayCommand]
        private void RemoveSessionFromComparison(SessionSummary summary)
        {
            summary.IsIncludedInComparison = false;
            RefreshComparison();
        }

        [RelayCommand]
        private void RestoreSessionToComparison(SessionSummary summary)
        {
            summary.IsIncludedInComparison = true;
            RefreshComparison();
        }

        /// <summary>
        /// Manually recomputes polar alignment error (useful when the user
        /// changes latitude or arcmin/turn parameter).
        /// </summary>
        [RelayCommand]
        private void RefreshPolarAlignment()
        {
            UpdatePolarAlignment();
        }

        /// <summary>Called by binding when PolarLatitudeDeg or PolarArcminPerTurn change.</summary>
        partial void OnPolarLatitudeDegChanged(double value)   => UpdatePolarAlignment();
        partial void OnPolarArcminPerTurnChanged(double value) => UpdatePolarAlignment();

        // ─────────────────────────────────────────────────────────────────────
        // MULTI-SESSION — MAIN FILE SYNC
        // ─────────────────────────────────────────────────────────────────────

        private async Task SyncMainFileToComparisonAsync(string filePath, List<GuidingSession> sessions)
        {
            // Inject already-computed sessions (FFT + stats present after AnalyzeSessionAsync)
            // without reparsing the file, to preserve computed data
            _multiSessionService.SetSessions(filePath, sessions);

            string fileName = Path.GetFileName(filePath);
            if (!LoadedComparisonFiles.Contains(fileName))
                LoadedComparisonFiles.Insert(0, fileName);

            await RefreshSummariesAndComparisonAsync();
        }

        // ─────────────────────────────────────────────────────────────────────
        // MULTI-SESSION — REFRESH
        // ─────────────────────────────────────────────────────────────────────

        private async Task RefreshSummariesAndComparisonAsync()
        {
            var summaries = await Task.Run(() => _multiSessionService.BuildAllSummaries());

            AllSessionSummaries.Clear();
            foreach (var s in summaries) AllSessionSummaries.Add(s);

            HasMultiSessionData = AllSessionSummaries.Count >= 2;
            RefreshComparison();
        }

        private void RefreshComparison()
        {
            if (AllSessionSummaries.Count < 2)
            {
                MultiSessionComparison = null;
                return;
            }

            var refSession = AllSessionSummaries
                .FirstOrDefault(s => s.Session == CurrentSession);

            var comparison = _multiSessionService.BuildComparison(
                AllSessionSummaries.ToList(), _comparisonScope, refSession);

            MultiSessionComparison = comparison;
            UpdateMultiSessionFftPlots(comparison);
        }

        // ─────────────────────────────────────────────────────────────────────
        // MULTI-SESSION — FFT CHARTS
        // ─────────────────────────────────────────────────────────────────────

        private void UpdateMultiSessionFftPlots(MultiSessionComparison comparison)
        {
            MultiSessionFftRaPlot  = BuildMultiSessionFftPlot(
                comparison.FftRaSpectra,  "FFT RA — Multi-session comparison");
            MultiSessionFftDecPlot = BuildMultiSessionFftPlot(
                comparison.FftDecSpectra, "FFT DEC — Multi-session comparison");
        }

        private static PlotModel BuildMultiSessionFftPlot(
            List<FftComparisonResult> spectra, string title)
        {
            var model = CreateDarkPlot(title);
            model.Legends.Clear();

            model.Axes.Add(new LogarithmicAxis
            {
                Position           = AxisPosition.Bottom,
                Title              = "Period (s)",
                TextColor          = OxyColors.LightGray,
                TicklineColor      = OxyColors.Gray,
                Minimum            = 10,
                Maximum            = 7200,
                MajorGridlineStyle = LineStyle.Dot,
                MajorGridlineColor = OxyColor.FromAColor(40, OxyColors.Gray),
            });
            model.Axes.Add(new LinearAxis
            {
                Position           = AxisPosition.Left,
                Title              = "Amplitude (\")",
                TextColor          = OxyColors.LightGray,
                TicklineColor      = OxyColors.Gray,
                Minimum            = 0,
                MajorGridlineStyle = LineStyle.Dot,
                MajorGridlineColor = OxyColor.FromAColor(40, OxyColors.Gray),
            });

            if (spectra.Count == 0) return model;

            var palette = new[]
            {
                OxyColor.FromRgb(0x64, 0xB5, 0xF6),
                OxyColor.FromRgb(0xFF, 0xB7, 0x4D),
                OxyColor.FromRgb(0x81, 0xC7, 0x84),
                OxyColor.FromRgb(0xCE, 0x93, 0xD8),
                OxyColor.FromRgb(0xFF, 0xCC, 0x02),
                OxyColor.FromRgb(0xEF, 0x53, 0x50),
                OxyColor.FromRgb(0x4D, 0xD0, 0xE1),
                OxyColor.FromRgb(0xFF, 0x80, 0xAB),
            };

            for (int i = 0; i < spectra.Count; i++)
            {
                var spec   = spectra[i];
                var color  = palette[i % palette.Length];
                var series = new LineSeries
                {
                    Title           = spec.SessionLabel,
                    Color           = color,
                    StrokeThickness = 1.2,
                    RenderInLegend  = false,
                    TrackerFormatString = "{0}\nPeriod: {2:F0} s\nAmplitude: {4:F3}\"",
                };

                for (int j = 0; j < spec.Frequencies.Length; j++)
                {
                    double freqHz = spec.Frequencies[j];
                    if (freqHz <= 0) continue;
                    double periodS = 1.0 / freqHz;
                    if (periodS < 10 || periodS > 7200) continue;
                    series.Points.Add(new DataPoint(periodS, spec.Amplitudes[j]));
                }

                model.Series.Add(series);
            }

            return model;
        }

        // ─────────────────────────────────────────────────────────────────────
        // ALTITUDE ANALYSIS
        // ─────────────────────────────────────────────────────────────────────

        private void UpdateAltitudeAnalysis(GuidingSession session)
        {
            var allSessions = AvailableSessions.ToList();

            double? lat = _altitudeService.EstimateSiteLatitude(allSessions);
            if (lat == null)
            {
                HasAltitudeData      = false;
                AltitudeLatitudeText = "Latitude cannot be computed (insufficient data)";
                return;
            }

            var result = _altitudeService.Analyze(session, allSessions, lat.Value);

            AltitudeLatitudeText   = $"Estimated latitude: {lat.Value:F1}° N";
            AltitudeMeridianFlip   = result.MeridianFlipDetected;
            HasAltitudeData        = result.ScatterPoints.Count >= 2 || result.SlidingPoints.Count >= 2;
            AltitudeWindowLabel    = result.WindowLabel;
            AltitudeWindowFromWorm = result.WindowFromWorm;

            // ── Degradation diagnosis ─────────────────────────────────────────
            var deg = result.Degradation;
            DegradationIcon        = deg.DiagnosisIcon;
            DegradationMain        = deg.MainMessage;
            DegradationDetail      = deg.DetailMessage;
            DegradationColor       = deg.DiagnosisColor;
            DegradationPearsonText = $"r = {deg.PearsonAltRms:+0.00;-0.00;0.00}";
            DegradationTrendText   = Math.Abs(deg.TrendArcsecPerHour) > 0.05
                ? $"{deg.TrendArcsecPerHour:+0.00;-0.00} \"/h"
                : "Stable";

            DegradationHasFlipData = result.MeridianFlipDetected
                                  && deg.RmsWestMean > 0 && deg.RmsEastMean > 0;
            if (DegradationHasFlipData)
            {
                DegradationWestText = $"{deg.RmsWestMean:F3}\"";
                DegradationEastText = $"{deg.RmsEastMean:F3}\"  (Δ {deg.FlipDeltaPercent:F0} %)";
            }

            // ── Charts ────────────────────────────────────────────────────────
            if (result.ScatterPoints.Count >= 2)
                RmsVsAltitudePlot = BuildRmsVsAltitudePlot(result);

            if (result.SlidingPoints.Count >= 2)
                SlidingRmsPlot = BuildSlidingRmsPlot(result);

            // ── Worm gear ─────────────────────────────────────────────────────
            var worm = result.WormPeriod;
            HasWormData = worm != null;
            if (worm != null)
            {
                WormPeriodText       = $"{worm.PeriodSec:F0} s  ({worm.PeriodSec / 60:F1} min)";
                WormAmplitudeText    = $"{worm.AmplitudeArcsec:F3}\"";
                WormSnrText          = $"SNR = {worm.SnrRatio:F1}×";
                WormCyclesText       = $"{worm.CyclesCovered:F2} cycle(s) covered";
                WormConfidenceText   = $"{worm.ConfidenceLabel}  ({worm.ConfidencePercent:F0} %)";
                WormConfidenceColor  = worm.ConfidenceColor;
                WormConfidenceDetail = worm.ConfidenceDetail;
                WormSourceSession    = worm.SourceSessionLabel;
                WormConfidencePercent = worm.ConfidencePercent;
                WormConsistentPhd2   = worm.ConsistentWithPhd2;
                WormPhd2Text = worm.Phd2PeriodSec > 0
                    ? $"{worm.Phd2PeriodSec:F0} s  ({worm.Phd2PeriodSec / 60:F1} min)"
                      + (worm.ConsistentWithPhd2 ? "  ✓ consistent" : "  ⚠ divergent")
                    : "Not available";
            }
        }

        private static PlotModel BuildRmsVsAltitudePlot(AltitudeAnalysisResult result)
        {
            var model = CreateDarkPlot("Total RMS vs Altitude");
            model.Legends.Clear();

            model.Axes.Add(new LinearAxis
            {
                Position           = AxisPosition.Bottom,
                Title              = "Altitude (°)",
                TextColor          = OxyColors.LightGray,
                TicklineColor      = OxyColors.Gray,
                Minimum            = 0,
                MajorGridlineStyle = LineStyle.Dot,
                MajorGridlineColor = OxyColor.FromAColor(40, OxyColors.Gray),
            });
            model.Axes.Add(new LinearAxis
            {
                Position           = AxisPosition.Left,
                Title              = "Total RMS (\")",
                TextColor          = OxyColors.LightGray,
                TicklineColor      = OxyColors.Gray,
                Minimum            = 0,
                MajorGridlineStyle = LineStyle.Dot,
                MajorGridlineColor = OxyColor.FromAColor(40, OxyColors.Gray),
            });

            var westSeries = new ScatterSeries
            {
                Title               = "West (●)",
                MarkerType          = MarkerType.Circle,
                MarkerFill          = OxyColor.FromRgb(0x64, 0xB5, 0xF6),
                MarkerSize          = 5,
                RenderInLegend      = true,
                TrackerFormatString = "{0}\nAlt: {2:F1}°\nRMS: {4:F3}\"",
            };
            var eastSeries = new ScatterSeries
            {
                Title               = "East (▲)",
                MarkerType          = MarkerType.Triangle,
                MarkerFill          = OxyColor.FromRgb(0xFF, 0xB7, 0x4D),
                MarkerSize          = 5,
                RenderInLegend      = true,
                TrackerFormatString = "{0}\nAlt: {2:F1}°\nRMS: {4:F3}\"",
            };
            var unknownSeries = new ScatterSeries
            {
                MarkerType     = MarkerType.Diamond,
                MarkerFill     = OxyColors.LightGray,
                MarkerSize     = 4,
                RenderInLegend = false,
            };

            foreach (var pt in result.ScatterPoints)
            {
                if (pt.TotalRms <= 0 || pt.TotalRms > 15) continue;
                var tag  = $"t={pt.ElapsedMin:F0} min\nAlt: {pt.AltitudeDeg:F1}°\nRMS: {pt.TotalRms:F3}\"";
                var side = pt.PierSide?.ToUpperInvariant() ?? "";
                if      (side.Contains("WEST")) westSeries.Points.Add(new ScatterPoint(pt.AltitudeDeg, pt.TotalRms, double.NaN, double.NaN, tag));
                else if (side.Contains("EAST")) eastSeries.Points.Add(new ScatterPoint(pt.AltitudeDeg, pt.TotalRms, double.NaN, double.NaN, tag));
                else                            unknownSeries.Points.Add(new ScatterPoint(pt.AltitudeDeg, pt.TotalRms, double.NaN, double.NaN, tag));
            }

            AddTrendLine(model, result.ScatterPoints.Where(p => p.PierSide?.ToUpperInvariant().Contains("WEST") == true && p.TotalRms > 0 && p.TotalRms < 15).ToList(), OxyColor.FromAColor(120, OxyColor.FromRgb(0x64, 0xB5, 0xF6)));
            AddTrendLine(model, result.ScatterPoints.Where(p => p.PierSide?.ToUpperInvariant().Contains("EAST") == true && p.TotalRms > 0 && p.TotalRms < 15).ToList(), OxyColor.FromAColor(120, OxyColor.FromRgb(0xFF, 0xB7, 0x4D)));

            if (westSeries.Points.Count > 0) model.Series.Add(westSeries);
            if (eastSeries.Points.Count > 0) model.Series.Add(eastSeries);
            if (unknownSeries.Points.Count > 0) model.Series.Add(unknownSeries);

            if (westSeries.Points.Count > 0 && eastSeries.Points.Count > 0)
                model.Legends.Add(new OxyPlot.Legends.Legend
                {
                    LegendBackground = OxyColor.FromAColor(180, OxyColor.FromRgb(0x25, 0x25, 0x35)),
                    LegendBorder     = OxyColors.Gray,
                    LegendTextColor  = OxyColors.LightGray,
                    LegendPosition   = OxyPlot.Legends.LegendPosition.TopRight,
                });

            return model;
        }

        private static void AddTrendLine(PlotModel model, List<RmsVsAltitudePoint> pts, OxyColor color)
        {
            if (pts.Count < 3) return;
            var xs = pts.Select(p => p.AltitudeDeg).ToList();
            var ys = pts.Select(p => p.TotalRms).ToList();
            double mx = xs.Average(), my = ys.Average();
            double num = xs.Zip(ys, (x, y) => (x - mx) * (y - my)).Sum();
            double den = xs.Sum(x => (x - mx) * (x - mx));
            if (Math.Abs(den) < 1e-10) return;
            double slope = num / den, intercept = my - slope * mx;
            double xMin = xs.Min(), xMax = xs.Max();
            var s = new LineSeries { Color = color, StrokeThickness = 1.5, LineStyle = LineStyle.Dash, RenderInLegend = false };
            s.Points.Add(new DataPoint(xMin, slope * xMin + intercept));
            s.Points.Add(new DataPoint(xMax, slope * xMax + intercept));
            model.Series.Add(s);
        }

        private static PlotModel BuildSlidingRmsPlot(AltitudeAnalysisResult result)
        {
            var model = CreateDarkPlot("Sliding RMS and altitude — full night");
            model.Legends.Clear();

            model.Axes.Add(new LinearAxis
            {
                Position           = AxisPosition.Bottom,
                Title              = "Time (min since guiding start)",
                TextColor          = OxyColors.LightGray,
                TicklineColor      = OxyColors.Gray,
                MajorGridlineStyle = LineStyle.Dot,
                MajorGridlineColor = OxyColor.FromAColor(40, OxyColors.Gray),
            });
            model.Axes.Add(new LinearAxis
            {
                Key                = "rms",
                Position           = AxisPosition.Left,
                Title              = "RMS (\")",
                TextColor          = OxyColors.LightGray,
                TicklineColor      = OxyColors.Gray,
                Minimum            = 0,
                MajorGridlineStyle = LineStyle.Dot,
                MajorGridlineColor = OxyColor.FromAColor(40, OxyColors.Gray),
            });
            model.Axes.Add(new LinearAxis
            {
                Key           = "alt",
                Position      = AxisPosition.Right,
                Title         = "Altitude (°)",
                TextColor     = OxyColor.FromAColor(150, OxyColors.LightGreen),
                TicklineColor = OxyColors.Gray,
            });

            var colorRaWest  = OxyColor.FromRgb(0x64, 0xB5, 0xF6);
            var colorDecWest = OxyColor.FromRgb(0x42, 0x8E, 0xC8);
            var colorRaEast  = OxyColor.FromRgb(0xFF, 0xD5, 0x4F);
            var colorDecEast = OxyColor.FromRgb(0xFF, 0x70, 0x43);

            bool hasWest    = result.SlidingPoints.Any(p => p.PierSide?.ToUpperInvariant().Contains("WEST") == true);
            bool hasEast    = result.SlidingPoints.Any(p => p.PierSide?.ToUpperInvariant().Contains("EAST") == true);
            bool hasUnknown = result.SlidingPoints.Any(p => string.IsNullOrEmpty(p.PierSide));

            // Ghost series for legend
            if (hasWest)
            {
                model.Series.Add(new LineSeries { Title = "RMS RA (West)",  Color = colorRaWest,  StrokeThickness = 1.5, RenderInLegend = true, YAxisKey = "rms" });
                model.Series.Add(new LineSeries { Title = "RMS DEC (West)", Color = colorDecWest, StrokeThickness = 1.2, RenderInLegend = true, YAxisKey = "rms", LineStyle = LineStyle.Dash });
            }
            if (hasEast)
            {
                model.Series.Add(new LineSeries { Title = "RMS RA (East)",  Color = colorRaEast,  StrokeThickness = 1.5, RenderInLegend = true, YAxisKey = "rms" });
                model.Series.Add(new LineSeries { Title = "RMS DEC (East)", Color = colorDecEast, StrokeThickness = 1.2, RenderInLegend = true, YAxisKey = "rms", LineStyle = LineStyle.Dash });
            }
            if (hasUnknown && !hasWest && !hasEast)
            {
                model.Series.Add(new LineSeries { Title = "RMS RA",  Color = colorRaWest,  StrokeThickness = 1.5, RenderInLegend = true, YAxisKey = "rms" });
                model.Series.Add(new LineSeries { Title = "RMS DEC", Color = colorDecWest, StrokeThickness = 1.2, RenderInLegend = true, YAxisKey = "rms", LineStyle = LineStyle.Dash });
            }
            model.Series.Add(new LineSeries { Title = "Altitude", Color = OxyColor.FromAColor(140, OxyColors.LightGreen), StrokeThickness = 2.0, LineStyle = LineStyle.Dot, RenderInLegend = true, YAxisKey = "alt" });

            // Real data series (RenderInLegend = false)
            var groups = result.SlidingPoints
                .GroupBy(p => p.SessionIndex)
                .OrderBy(g => g.Key)
                .ToList();

            foreach (var group in groups)
            {
                var pts  = group.OrderBy(p => p.ElapsedMin).ToList();
                string side = pts.First().PierSide?.ToUpperInvariant() ?? "";
                bool isEast = side.Contains("EAST");

                var colorRa  = isEast ? colorRaEast  : colorRaWest;
                var colorDec = isEast ? colorDecEast : colorDecWest;

                var raLine = new LineSeries { YAxisKey = "rms", Color = colorRa,  StrokeThickness = 1.5, RenderInLegend = false };
                var decLine = new LineSeries { YAxisKey = "rms", Color = colorDec, StrokeThickness = 1.2, RenderInLegend = false };

                foreach (var p in pts)
                {
                    raLine.Points.Add(new DataPoint(p.ElapsedMin, p.RaRms));
                    decLine.Points.Add(new DataPoint(p.ElapsedMin, p.DecRms));
                }
                model.Series.Add(decLine);
                model.Series.Add(raLine);
            }

            // Continuous altitude line
            var altLine = new LineSeries
            {
                YAxisKey        = "alt",
                Color           = OxyColor.FromAColor(140, OxyColors.LightGreen),
                StrokeThickness = 2.0,
                LineStyle       = LineStyle.Dot,
                RenderInLegend  = false,
            };
            int prevIdx = -1;
            foreach (var p in result.SlidingPoints.OrderBy(p => p.ElapsedMin))
            {
                if (prevIdx >= 0 && p.SessionIndex != prevIdx && p.SessionIndex != prevIdx + 1)
                    altLine.Points.Add(DataPoint.Undefined);
                altLine.Points.Add(new DataPoint(p.ElapsedMin, p.AltitudeDeg));
                prevIdx = p.SessionIndex;
            }
            model.Series.Add(altLine);

            // Meridian flip annotation
            if (result.MeridianFlipDetected)
            {
                var flipPt = result.SlidingPoints.FirstOrDefault(p => p.PierSide?.ToUpperInvariant().Contains("EAST") == true);
                if (flipPt != null)
                {
                    model.Annotations.Add(new OxyPlot.Annotations.LineAnnotation
                    {
                        Type            = OxyPlot.Annotations.LineAnnotationType.Vertical,
                        X               = flipPt.ElapsedMin,
                        Color           = OxyColor.FromAColor(120, OxyColors.Yellow),
                        StrokeThickness = 1.5,
                        LineStyle       = LineStyle.Dash,
                        Text            = "Meridian flip",
                        TextColor       = OxyColors.Yellow,
                    });
                }
            }

            model.Legends.Add(new OxyPlot.Legends.Legend
            {
                LegendBackground = OxyColor.FromAColor(180, OxyColor.FromRgb(0x25, 0x25, 0x35)),
                LegendBorder     = OxyColors.Gray,
                LegendTextColor  = OxyColors.LightGray,
                LegendPosition   = OxyPlot.Legends.LegendPosition.TopRight,
            });

            return model;
        }

        // ─────────────────────────────────────────────────────────────────────
        // OXYPLOT HELPERS
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Forces the Y axis to be symmetric around 0 with a 10% margin.
        /// Zero is centred in the plot area on initial display.
        /// </summary>
        private static void CenterYAxis(PlotModel model)
        {
            double maxAbs = 0;
            foreach (var series in model.Series)
            {
                if (series is LineSeries ls)
                    foreach (var pt in ls.Points)
                        if (!double.IsNaN(pt.Y))
                            maxAbs = Math.Max(maxAbs, Math.Abs(pt.Y));
            }
            double margin = maxAbs > 0 ? maxAbs * 1.10 : 1.0;
            foreach (var ax in model.Axes)
                if (ax.Position == AxisPosition.Left || ax.Position == AxisPosition.Right)
                {
                    ax.Minimum = -margin;
                    ax.Maximum =  margin;
                }
        }

        private static PlotModel CreateEmptyPlot(string title)
        {
            var m = CreateDarkPlot(title);
            m.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom,
                TextColor = OxyColors.LightGray, TicklineColor = OxyColors.Gray });
            m.Axes.Add(new LinearAxis { Position = AxisPosition.Left,
                TextColor = OxyColors.LightGray, TicklineColor = OxyColors.Gray });
            return m;
        }

        private static PlotModel CreateDarkPlot(string title)
        {
            var m = new PlotModel
            {
                Title               = title,
                TitleColor          = OxyColors.White,
                PlotAreaBorderColor = OxyColors.Gray,
                Background          = OxyColor.FromRgb(0x1E, 0x1E, 0x2E),
                TextColor           = OxyColors.LightGray,
            };
            m.Legends.Add(new OxyPlot.Legends.Legend
            {
                LegendBackground = OxyColor.FromAColor(180, OxyColor.FromRgb(0x25, 0x25, 0x35)),
                LegendBorder     = OxyColors.Gray,
                LegendTextColor  = OxyColors.LightGray,
                LegendPosition   = OxyPlot.Legends.LegendPosition.TopRight,
            });
            m.PlotType = PlotType.XY;
            return m;
        }
    }
}
