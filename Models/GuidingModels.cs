/*
 * Models/GuidingModels.cs
 * ========================
 * Defines all data structures for the plugin.
 *
 * A "Model" in MVVM represents raw data with no display logic.
 * These classes are populated by Services and read by ViewModels.
 */

using System;
using System.Collections.Generic;

namespace GuidingAnalyzer.Models
{
    // ═══════════════════════════════════════════════════════════════════════════
    // RAW GUIDING DATA
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Represents ONE guiding data point (one line from a PHD2 log).
    ///
    /// PHD2 records for each frame:
    ///   - The measured error (in pixels or arcsec) for RA and DEC
    ///   - The correction sent to the mount for RA and DEC
    ///   - Whether guiding skipped this frame (low SNR, etc.)
    /// </summary>
    public class GuidingPoint
    {
        /// <summary>Sequential frame index (1, 2, 3…)</summary>
        public int FrameIndex { get; set; }

        /// <summary>Absolute timestamp of the measurement</summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Measured error in Right Ascension (arcsec).
        /// Deviation between the guide star and its reference position.
        /// Positive = East, Negative = West (per PHD2 configuration)
        /// </summary>
        public double RaError { get; set; }

        /// <summary>
        /// Measured error in Declination (arcsec).
        /// Positive = North, Negative = South (per PHD2 configuration)
        /// </summary>
        public double DecError { get; set; }

        /// <summary>
        /// Correction pulse sent to the mount in RA (milliseconds).
        /// What PHD2 commanded to correct the error.
        /// </summary>
        public double RaCorrection { get; set; }

        /// <summary>
        /// Correction pulse sent to the mount in DEC (milliseconds).
        /// </summary>
        public double DecCorrection { get; set; }

        /// <summary>
        /// Signal-to-noise ratio of the guide star on this frame.
        /// Below ~10, PHD2 may skip the correction.
        /// </summary>
        public double StarSNR { get; set; }

        /// <summary>
        /// True if PHD2 used this frame for guiding (ErrorCode == 0).
        /// False = star lost, low SNR, etc. — excluded from statistics but kept for display.
        /// </summary>
        public bool IsGuideStep { get; set; } = true;

        /// <summary>
        /// True if this frame belongs to a dither or post-dither settling phase.
        /// These frames contain intentional large jumps and must be excluded
        /// from all amplitude and drift calculations.
        /// </summary>
        public bool IsDitherOrSettling { get; set; } = false;

        /// <summary>Total error = √(RA² + DEC²) in arcsec</summary>
        public double TotalError => Math.Sqrt(RaError * RaError + DecError * DecError);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // RECONSTRUCTED MOUNT CURVE
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Represents the REAL behaviour of the mount, reconstructed by accumulating
    /// the measured error plus the corrections already applied.
    ///
    /// RECONSTRUCTION PRINCIPLE:
    /// ─────────────────────────
    /// PHD2 measures the error AFTER guiding. To recover the intrinsic mount
    /// behaviour (without corrections), we accumulate:
    ///
    ///   MountPos[n] = MountPos[n-1]
    ///               + Error[n]              (measured drift)
    ///               + Correction[n-1]       (correction sent at previous step)
    ///
    /// This curve reveals periodic error (PE), backlash, vibrations and other
    /// mechanical behaviours.
    /// </summary>
    public class MountCurvePoint
    {
        public int FrameIndex { get; set; }
        public DateTime Timestamp { get; set; }

        /// <summary>Cumulative mount position in RA (arcsec)</summary>
        public double RaMountPosition { get; set; }

        /// <summary>Cumulative mount position in DEC (arcsec)</summary>
        public double DecMountPosition { get; set; }

        /// <summary>True if this point corresponds to a dither or settling — exclude from calculations</summary>
        public bool IsDitherOrSettling { get; set; } = false;

        /// <summary>Raw PHD2 error at this point (for reference)</summary>
        public double RaRawError { get; set; }
        public double DecRawError { get; set; }

        /// <summary>Correction applied at this point (arcsec, converted)</summary>
        public double RaCorrectionArcsec { get; set; }
        public double DecCorrectionArcsec { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // FFT ANALYSIS RESULTS
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Results of the FFT (Fast Fourier Transform) analysis.
    ///
    /// The FFT decomposes the mount signal into frequencies.
    /// Each peak in the FFT spectrum corresponds to an error source:
    ///   - Periodic error (PE): frequency = 1/(worm period), typically ~8 min
    ///   - PE harmonics: 2×, 3× the fundamental frequency
    ///   - Mechanical vibrations: higher frequencies
    ///   - Differential flexure: very low frequency (slow drift)
    /// </summary>
    public class FftResult
    {
        /// <summary>Axis on which the FFT was computed</summary>
        public GuidingAxis Axis { get; set; }

        /// <summary>
        /// Frequency array (in cycles/hour or Hz depending on parameter).
        /// Frequencies[i] corresponds to amplitude Amplitudes[i].
        /// </summary>
        public double[] Frequencies { get; set; } = Array.Empty<double>();

        /// <summary>
        /// Corresponding amplitudes (arcsec).
        /// A high peak = significant error source at that frequency.
        /// </summary>
        public double[] Amplitudes { get; set; } = Array.Empty<double>();

        /// <summary>
        /// List of automatically detected significant peaks.
        /// A peak is "significant" if it exceeds the configurable threshold.
        /// </summary>
        public List<FftPeak> SignificantPeaks { get; set; } = new();

        /// <summary>Sampling period used (seconds between points)</summary>
        public double SamplingPeriodSeconds { get; set; }

        /// <summary>Number of points used for the FFT calculation</summary>
        public int PointCount { get; set; }
    }

    /// <summary>
    /// A peak identified in the FFT spectrum = a detected error source.
    /// </summary>
    public class FftPeak
    {
        /// <summary>Peak frequency (cycles/hour)</summary>
        public double FrequencyCph { get; set; }

        /// <summary>Corresponding period (minutes)</summary>
        public double PeriodMinutes => FrequencyCph > 0 ? 60.0 / FrequencyCph : double.MaxValue;

        /// <summary>Corresponding period (seconds) — for log-scale X axis display</summary>
        public double PeriodSeconds => FrequencyCph > 0 ? 3600.0 / FrequencyCph : double.MaxValue;

        /// <summary>Peak amplitude (arcsec)</summary>
        public double AmplitudeArcsec { get; set; }

        /// <summary>
        /// Automatic interpretation of the peak.
        /// E.g.: "Periodic error (worm ~8 min)", "PE harmonic", "Vibration"
        /// </summary>
        public string Interpretation { get; set; } = string.Empty;

        /// <summary>Confidence level of the interpretation (0–1)</summary>
        public double Confidence { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // GUIDING STATISTICS
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Complete statistics for a guiding session.
    /// These values allow global quality assessment of the guiding.
    /// </summary>
    public class GuidingStatistics
    {
        // ── RA statistics ────────────────────────────────────────────────────
        /// <summary>RA RMS = standard deviation of RA errors (arcsec). Ideal: &lt; 0.5"</summary>
        public double RaRms { get; set; }
        /// <summary>Maximum absolute RA error (arcsec)</summary>
        public double RaPeak { get; set; }
        /// <summary>RA peak-to-peak = total oscillation amplitude (arcsec)</summary>
        public double RaPeakToPeak { get; set; }
        /// <summary>Linear RA drift (arcsec/minute). Close to 0 = good polar alignment</summary>
        public double RaDriftArcsecPerMin { get; set; }

        // ── DEC statistics ───────────────────────────────────────────────────
        public double DecRms { get; set; }
        public double DecPeak { get; set; }
        public double DecPeakToPeak { get; set; }
        /// <summary>
        /// DEC drift (arcsec/minute).
        /// Persistent DEC drift = polar alignment error.
        /// </summary>
        public double DecDriftArcsecPerMin { get; set; }

        // ── Global statistics ────────────────────────────────────────────────
        /// <summary>Total RMS = √(RMS_RA² + RMS_DEC²). Global quality indicator.</summary>
        public double TotalRms => Math.Sqrt(RaRms * RaRms + DecRms * DecRms);
        /// <summary>Number of guided frames analysed</summary>
        public int FrameCount { get; set; }
        /// <summary>Total duration of the analysed session</summary>
        public TimeSpan SessionDuration { get; set; }
        /// <summary>Percentage of valid frames (not rejected)</summary>
        public double ValidFramePercent { get; set; }

        // ── Qualitative evaluation ───────────────────────────────────────────
        /// <summary>
        /// Automatically computed overall grade (A+ to F).
        /// Based on total RMS, drift and stability.
        /// </summary>
        public string QualityGrade { get; set; } = "N/A";
        public string QualityDescription { get; set; } = string.Empty;

        // ── Advanced statistics ──────────────────────────────────────────────
        /// <summary>RA skewness: 0 = symmetric distribution, >1 = notable asymmetry</summary>
        public double RaSkewness { get; set; }
        /// <summary>DEC skewness</summary>
        public double DecSkewness { get; set; }
        /// <summary>RA excess kurtosis: 0 = normal, >0 = heavy tails (rare but strong peaks)</summary>
        public double RaKurtosis { get; set; }
        /// <summary>DEC excess kurtosis</summary>
        public double DecKurtosis { get; set; }
        /// <summary>Pearson RA/DEC correlation: >0.5 = likely flexure</summary>
        public double RaDecCorrelation { get; set; }
        /// <summary>Variance ratio 2nd half / 1st half: >1.5 = degradation detected</summary>
        public double RaStabilityRatio { get; set; }
        /// <summary>DEC variance ratio</summary>
        public double DecStabilityRatio { get; set; }

        // ── RA PEC harmonic analysis ─────────────────────────────────────────
        /// <summary>Dominant worm period estimated from RA FFT (seconds)</summary>
        public double WormPeriodSeconds { get; set; }
        /// <summary>Amplitude of the worm peak (arcsec)</summary>
        public double WormAmplitudeArcsec { get; set; }
        /// <summary>Top 5 RA harmonics: (period in s, amplitude in arcsec)</summary>
        public List<(double PeriodSec, double AmplitudeArcsec)> RaHarmonics { get; set; } = new();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // DETECTED ANOMALIES
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Represents an anomaly detected in the guiding data.
    /// Anomalies are abnormal patterns that indicate a problem.
    /// </summary>
    public class GuidingAnomaly
    {
        public AnomalyType Type { get; set; }
        public AnomalySeverity Severity { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        /// <summary>Recommendation to fix the problem</summary>
        public string Recommendation { get; set; } = string.Empty;
        /// <summary>Frame where the anomaly begins (-1 if global)</summary>
        public int StartFrame { get; set; } = -1;
        public int EndFrame { get; set; } = -1;
        /// <summary>Measured value associated (e.g. PE amplitude, flexure angle)</summary>
        public double MeasuredValue { get; set; }
        public string Unit { get; set; } = string.Empty;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // ENUMERATIONS
    // ═══════════════════════════════════════════════════════════════════════════

    public enum GuidingAxis { RA, Dec, Both }

    public enum AnomalyType
    {
        /// <summary>Strong periodic error (worm or main gear)</summary>
        PeriodicError,
        /// <summary>Differential flexure (non-linear drift between OTA and guide scope)</summary>
        DifferentialFlexure,
        /// <summary>Mechanical backlash (gear play)</summary>
        Backlash,
        /// <summary>Poor polar alignment (systematic DEC drift)</summary>
        PolarAlignmentError,
        /// <summary>Vibrations (high-frequency FFT peaks)</summary>
        Vibration,
        /// <summary>Aggressive correction (oscillation induced by PHD2)</summary>
        AggressiveCorrection,
        /// <summary>Poor guide star quality (low or variable SNR)</summary>
        PoorGuideStar,
        /// <summary>Atmospheric turbulence (seeing)</summary>
        AtmosphericTurbulence
    }

    public enum AnomalySeverity
    {
        /// <summary>Information only — nothing to correct</summary>
        Info,
        /// <summary>Warning — may slightly impact image quality</summary>
        Warning,
        /// <summary>Significant problem — correction recommended</summary>
        Error,
        /// <summary>Critical problem — guiding severely degraded</summary>
        Critical
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // COMPLETE SESSION
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Main container gathering all analysis results for a complete guiding session.
    /// </summary>
    public class GuidingSession
    {
        public string   SourceFile   { get; set; } = string.Empty;
        public DateTime LoadedAt     { get; set; } = DateTime.Now;

        /// <summary>Session number within the file (1, 2, 3…)</summary>
        public int      SessionIndex { get; set; }

        /// <summary>Session start timestamp (extracted from "Guiding Begins at …")</summary>
        public DateTime StartTime    { get; set; }

        // Raw data
        public List<GuidingPoint> RawPoints { get; set; } = new();

        // Reconstructed mount curve
        public List<MountCurvePoint> MountCurve { get; set; } = new();

        // Analysis results
        public GuidingStatistics? Statistics { get; set; }
        /// <summary>FFT of mount curve RA — "Drift-corrected" (with PHD2 corrections)</summary>
        public FftResult?         FftRa      { get; set; }
        /// <summary>FFT of mount curve DEC — "Drift-corrected"</summary>
        public FftResult?         FftDec     { get; set; }
        /// <summary>FFT RA without PHD2 corrections — "RA Corrections Removed"</summary>
        public FftResult?         FftRaUncorrected  { get; set; }
        /// <summary>FFT DEC without PHD2 corrections</summary>
        public FftResult?         FftDecUncorrected { get; set; }
        public List<GuidingAnomaly> Anomalies { get; set; } = new();
        public List<GuidingAdvice>  Advice    { get; set; } = new();

        // Session parameters (extracted from PHD2 log)
        public double PixelScale             { get; set; }   // arcsec/pixel
        public double FocalLength            { get; set; }   // mm
        public double GuidingRateArcsecPerMs { get; set; }   // ms → arcsec conversion
        public string MountName              { get; set; } = string.Empty;
        public string CameraName             { get; set; } = string.Empty;

        /// <summary>
        /// Last CalibrationSession preceding this guiding session.
        /// Null if no calibration was found before this session in the log.
        /// </summary>
        public CalibrationSession? Calibration { get; set; }

        /// <summary>Detailed information extracted from the PHD2 header</summary>
        public GuidingSessionInfo? SessionInfo { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // DRIFT ANALYSIS (reconstructed mount curve)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Result of drift calculation on the reconstructed mount curve.
    /// Drift is the slope of the linear regression on the signal.
    /// R² measures the quality of the linear fit (close to 1 = very linear drift).
    /// </summary>
    public class DriftAnalysis
    {
        public double RaDriftArcsecPerMin  { get; set; }
        public double DecDriftArcsecPerMin { get; set; }
        public double RaRSquared           { get; set; }
        public double DecRSquared          { get; set; }
        /// <summary>Confidence index 0–1: R² weighted by session duration</summary>
        public double RaConfidence         { get; set; }
        public double DecConfidence        { get; set; }
        public string RaConfidenceLabel  => RaConfidence  > 0.8 ? "High"   : RaConfidence  > 0.5 ? "Medium" : "Low";
        public string DecConfidenceLabel => DecConfidence > 0.8 ? "High"   : DecConfidence > 0.5 ? "Medium" : "Low";
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // PHD2 CALIBRATION
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>One calibration point (dx, dy in pixels for a given direction)</summary>
    public class CalibrationPoint
    {
        public int    Step { get; set; }
        public double Dx   { get; set; }
        public double Dy   { get; set; }
        public double X    { get; set; }
        public double Y    { get; set; }
        public double Dist => Math.Sqrt(Dx * Dx + Dy * Dy);
    }

    /// <summary>One PHD2 calibration session (block "Calibration Begins…")</summary>
    public class CalibrationSession
    {
        public DateTime StartTime          { get; set; }
        public string   MountName          { get; set; } = string.Empty;
        public double   PixelScaleArcSecPx { get; set; }
        public double   FocalLengthMm      { get; set; }

        // RA results
        public double RaAngleDeg    { get; set; }
        public double RaRatePxSec   { get; set; }
        public string RaParity      { get; set; } = string.Empty;

        // DEC results
        public double DecAngleDeg   { get; set; }
        public double DecRatePxSec  { get; set; }
        public string DecParity     { get; set; } = string.Empty;

        // Parameters used
        public int    CalibrationStepMs     { get; set; }
        public double CalibrationDistancePx { get; set; }
        // Telescope position during calibration
        public double RaHours              { get; set; }
        public double DecDeg               { get; set; }
        public double HourAngle            { get; set; }
        public double AltDeg               { get; set; }
        public double AzDeg                { get; set; }
        public string PierSide             { get; set; } = string.Empty;
        // Guide speeds
        public double RaGuideSpeedArcsecS  { get; set; }
        public double DecGuideSpeedArcsecS { get; set; }

        // Calibration points by direction
        public List<CalibrationPoint> RaWestPoints   { get; set; } = new();
        public List<CalibrationPoint> RaEastPoints   { get; set; } = new();
        public List<CalibrationPoint> DecNorthPoints { get; set; } = new();
        public List<CalibrationPoint> DecSouthPoints { get; set; } = new();
        public List<CalibrationPoint> BacklashPoints { get; set; } = new();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // SESSION INFORMATION (PHD2 header)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Complete metadata extracted from a PHD2 session header</summary>
    public class GuidingSessionInfo
    {
        // ── Identity ─────────────────────────────────────────────────────────
        public string  EquipmentProfile    { get; set; } = string.Empty;
        public string  MountName           { get; set; } = string.Empty;
        public string  CameraName          { get; set; } = string.Empty;

        // ── Optics / camera ──────────────────────────────────────────────────
        public double  PixelScaleArcSecPx  { get; set; }
        public double  FocalLengthMm       { get; set; }
        public int     Binning             { get; set; } = 1;
        public int     ExposureMs          { get; set; }
        public int     CameraGain          { get; set; }
        public string  CameraFullSize      { get; set; } = string.Empty;
        public double  CameraPixelSizeUm   { get; set; }
        public bool    CameraHasDark       { get; set; }
        public bool    CameraHasDefectMap  { get; set; }

        // ── Dither / star search ──────────────────────────────────────────────
        public string  DitherAxes          { get; set; } = string.Empty;
        public double  DitherScale         { get; set; }
        public string  NoiseReduction      { get; set; } = string.Empty;
        public int     GuideFrameTimeLapse { get; set; }
        public int     SearchRegionPx      { get; set; }
        public bool    StarMassToleranceEnabled { get; set; }
        public bool    MultiStarMode       { get; set; }
        public int     MultiStarListSize   { get; set; }

        // ── Mount angles/rates (line "guiding enabled") ───────────────────────
        public double  MountXAngle         { get; set; }
        public double  MountXRate          { get; set; }
        public double  MountYAngle         { get; set; }
        public double  MountYRate          { get; set; }
        public string  MountParity         { get; set; } = string.Empty;

        // ── Guide star ───────────────────────────────────────────────────────
        public double  StarHfd             { get; set; }
        public double  LockPositionX       { get; set; }
        public double  LockPositionY       { get; set; }

        // ── RA algorithm ─────────────────────────────────────────────────────
        public string  GuideAlgoRA         { get; set; } = string.Empty;
        public string  RaAlgorithm         => GuideAlgoRA;
        public double  RaControlGain       { get; set; }
        public double  MinMotionRA         { get; set; }
        public double  RaMinMove           => MinMotionRA;
        public double  RaPredictionGain    { get; set; }
        // Predictive PEC hyperparameters
        public double  PecPeriodLength     { get; set; }   // "Period length periodic kernel"
        public double  PecFftAfterCycles   { get; set; }   // "FFT called after = N worm cycles"
        public string  PecAutoAdjustPeriod { get; set; } = string.Empty;

        // ── DEC algorithm ────────────────────────────────────────────────────
        public string  GuideAlgoDEC        { get; set; } = string.Empty;
        public string  DecAlgorithm        => GuideAlgoDEC;
        public double  MinMotionDEC        { get; set; }
        public double  DecMinMove          => MinMotionDEC;
        public double  DecAggression       { get; set; }   // %
        public bool    DecFastSwitch       { get; set; }

        // ── Guiding parameters ───────────────────────────────────────────────
        public string  DecGuideMode        { get; set; } = string.Empty;
        public int     MaxRaDuration       { get; set; }
        public int     MaxDecDuration      { get; set; }
        public string  BacklashCompEnabled { get; set; } = string.Empty;
        public int     BacklashPulseMs     { get; set; }

        // ── Guide speeds ─────────────────────────────────────────────────────
        public double  GuideRateRA         { get; set; }
        public double  GuideRateDEC        { get; set; }
        public double  RaGuideSpeedArcsecS  => GuideRateRA;
        public double  DecGuideSpeedArcsecS => GuideRateDEC;
        public double  CalDec              { get; set; }
        public string  LastCalIssue        { get; set; } = string.Empty;
        public string  CalTimestamp        { get; set; } = string.Empty;

        // ── Normalised rates ─────────────────────────────────────────────────
        public string  NormRatesRa         { get; set; } = string.Empty;
        public string  NormRatesDec        { get; set; } = string.Empty;
        public double  OrthoErrorDeg       { get; set; }

        // ── Sky position ─────────────────────────────────────────────────────
        public double  RaHours             { get; set; }
        public double  DecDeg              { get; set; }
        public double  HourAngle           { get; set; }
        public double  AltDeg              { get; set; }
        public double  AzDeg               { get; set; }
        public string  PierSide            { get; set; } = string.Empty;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // IMPROVEMENT ADVICE
    // ═══════════════════════════════════════════════════════════════════════════

    public enum AdvicePriority { Critical = 4, High = 3, Medium = 2, Low = 1, Info = 0 }
    public enum AdviceCategory
    {
        PolarAlignment, PeriodicError, Backlash, GuidingParameters,
        GuideStar, Flexure, Calibration, Equipment, Statistics, Stability
    }

    public class GuidingAdvice
    {
        public AdvicePriority Priority    { get; set; }
        public AdviceCategory Category    { get; set; }
        public string Icon               { get; set; } = "";
        public string Title              { get; set; } = "";
        public string Detail             { get; set; } = "";
        public string Action             { get; set; } = "";
        public string? Context           { get; set; }   // formatted measured value

        /// Border colour per priority
        public string BorderColor => Priority switch
        {
            AdvicePriority.Critical => "#F44336",
            AdvicePriority.High     => "#FF5722",
            AdvicePriority.Medium   => "#FFC107",
            AdvicePriority.Low      => "#64B5F6",
            _                       => "#555577",
        };
        public string BackgroundColor => Priority switch
        {
            AdvicePriority.Critical => "#3A0808",
            AdvicePriority.High     => "#2E1008",
            AdvicePriority.Medium   => "#2E2008",
            AdvicePriority.Low      => "#08152E",
            _                       => "#1A1A2E",
        };
        public string PriorityLabel => Priority switch
        {
            AdvicePriority.Critical => "CRITICAL",
            AdvicePriority.High     => "IMPORTANT",
            AdvicePriority.Medium   => "WARNING",
            AdvicePriority.Low      => "ADVICE",
            _                       => "INFO",
        };
        public string PriorityColor => Priority switch
        {
            AdvicePriority.Critical => "#F44336",
            AdvicePriority.High     => "#FF5722",
            AdvicePriority.Medium   => "#FFC107",
            AdvicePriority.Low      => "#64B5F6",
            _                       => "#888888",
        };
    }
}
