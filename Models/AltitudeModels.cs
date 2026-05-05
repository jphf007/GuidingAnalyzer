/*
 * AltitudeModels.cs
 * =================
 * Data models for RMS vs Altitude analysis.
 */

using System;
using System.Collections.Generic;

namespace GuidingAnalyzer.Models
{
    // ═══════════════════════════════════════════════════════════════════════════
    // SCATTER POINT: RMS vs ALTITUDE PER SUB-SESSION
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// One point of the RMS vs Altitude scatter plot.
    /// Each point represents a sliding window (size = worm period or 5 min).
    /// </summary>
    public class RmsVsAltitudePoint
    {
        public int      SessionIndex  { get; set; }
        public string   SessionLabel  { get; set; } = string.Empty;
        public DateTime WindowCenter  { get; set; }
        public double   ElapsedMin    { get; set; }   // minutes since session start

        public double AltitudeDeg    { get; set; }
        public double RaRms          { get; set; }
        public double DecRms         { get; set; }
        public double TotalRms       { get; set; }

        /// <summary>"West", "East" or "" if unknown</summary>
        public string PierSide       { get; set; } = string.Empty;

        public int    FrameCount     { get; set; }

        /// <summary>True if this session is after a meridian flip</summary>
        public bool   IsAfterFlip    { get; set; } = false;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // SLIDING RMS POINT (temporal curve)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Sliding RMS result at a given instant, with computed altitude.
    /// </summary>
    public class SlidingRmsPoint
    {
        public DateTime Timestamp     { get; set; }
        public double   ElapsedMin    { get; set; }   // minutes since start of file

        public double   RaRms         { get; set; }
        public double   DecRms        { get; set; }
        public double   TotalRms      { get; set; }

        public double   AltitudeDeg   { get; set; }
        public string   PierSide      { get; set; } = string.Empty;

        /// <summary>PHD2 sub-session index (for colouring transitions)</summary>
        public int      SessionIndex  { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // DEGRADATION DIAGNOSTIC
    // ═══════════════════════════════════════════════════════════════════════════

    public enum DegradationCause
    {
        None,
        Altitude,          // RMS correlated with altitude (seeing / refraction)
        MeridianFlip,      // RMS change after meridian flip
        GimbalLock,        // High altitude near zenith (HA close to 0)
        Trend,             // Progressive degradation without clear correlation
        Unknown
    }

    public class DegradationAnalysis
    {
        public bool   DegradationDetected    { get; set; }
        public DegradationCause Cause        { get; set; }

        /// <summary>Pearson correlation RMS vs Altitude (valid sessions)</summary>
        public double PearsonAltRms          { get; set; }

        /// <summary>Mean RMS West side (arcsec)</summary>
        public double RmsWestMean            { get; set; }
        /// <summary>Mean RMS East side (arcsec)</summary>
        public double RmsEastMean            { get; set; }
        /// <summary>West–East difference / West (percentage)</summary>
        public double FlipDeltaPercent       { get; set; }

        /// <summary>RMS trend over time (arcsec/hour)</summary>
        public double TrendArcsecPerHour     { get; set; }

        /// <summary>Main diagnostic message</summary>
        public string MainMessage            { get; set; } = string.Empty;

        /// <summary>Secondary details</summary>
        public string DetailMessage          { get; set; } = string.Empty;

        /// <summary>Diagnostic badge colour</summary>
        public string DiagnosisColor         { get; set; } = "#AAAACC";

        /// <summary>Diagnostic emoji icon</summary>
        public string DiagnosisIcon          { get; set; } = "➡";
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // FULL ANALYSIS RESULT
    // ═══════════════════════════════════════════════════════════════════════════

    public class AltitudeAnalysisResult
    {
        /// <summary>Site latitude estimated from log data (degrees)</summary>
        public double  EstimatedLatitudeDeg { get; set; }

        /// <summary>Meridian flip detected during the night</summary>
        public bool    MeridianFlipDetected { get; set; }

        /// <summary>Window size used (frames)</summary>
        public int     WindowFrames         { get; set; }

        /// <summary>True if the window comes from the worm period (vs 5-min default)</summary>
        public bool    WindowFromWorm       { get; set; }

        /// <summary>Label describing the window used</summary>
        public string  WindowLabel          { get; set; } = string.Empty;

        /// <summary>Scatter plot points RMS vs Alt (one entry per window)</summary>
        public List<RmsVsAltitudePoint> ScatterPoints { get; set; } = new();

        /// <summary>Continuous sliding RMS points over the whole night</summary>
        public List<SlidingRmsPoint> SlidingPoints { get; set; } = new();

        /// <summary>Degradation analysis and cause diagnosis</summary>
        public DegradationAnalysis Degradation { get; set; } = new();

        /// <summary>Worm period estimate (null if not computable)</summary>
        public WormPeriodEstimate? WormPeriod { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // WORM PERIOD ESTIMATE
    // ═══════════════════════════════════════════════════════════════════════════

    public class WormPeriodEstimate
    {
        /// <summary>Period estimated by our FFT (seconds)</summary>
        public double PeriodSec          { get; set; }

        /// <summary>Period provided by PHD2 PredictivePEC (seconds), 0 if absent</summary>
        public double Phd2PeriodSec      { get; set; }

        /// <summary>FFT peak amplitude (arcsec)</summary>
        public double AmplitudeArcsec    { get; set; }

        /// <summary>FFT peak signal-to-noise ratio (amplitude / median noise floor)</summary>
        public double SnrRatio           { get; set; }

        /// <summary>Number of complete cycles covered by the longest session</summary>
        public double CyclesCovered      { get; set; }

        /// <summary>
        /// Overall confidence score 0–100%.
        /// Combination of: SNR, cycles covered, consistency with PHD2.
        /// </summary>
        public double ConfidencePercent  { get; set; }

        /// <summary>Qualitative label: "High", "Medium", "Low", "Insufficient"</summary>
        public string ConfidenceLabel    { get; set; } = string.Empty;

        /// <summary>Colour associated with the confidence label</summary>
        public string ConfidenceColor    { get; set; } = "#AAAACC";

        /// <summary>Explanation of the confidence score (limiting factors)</summary>
        public string ConfidenceDetail   { get; set; } = string.Empty;

        /// <summary>True if our estimate is consistent with PHD2's estimate (±15%)</summary>
        public bool   ConsistentWithPhd2 { get; set; }

        /// <summary>Session used for the estimate (the longest one)</summary>
        public string SourceSessionLabel { get; set; } = string.Empty;
    }
}
