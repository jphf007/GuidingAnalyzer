/*
 * MultiSessionModels.cs
 * =====================
 * Data classes for multi-session / multi-file comparison.
 *
 * These classes complement GuidingModels.cs without modifying it.
 */

using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using GuidingAnalyzer.Models;

namespace GuidingAnalyzer.Models
{
    // ═══════════════════════════════════════════════════════════════════════════
    // SESSION SUMMARY FOR COMPARISON
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Synthetic view of a session, intended for the comparison list.
    /// Aggregates essential stats without duplicating raw data.
    /// </summary>
    public partial class SessionSummary : ObservableObject
    {
        // ── Identity ─────────────────────────────────────────────────────────

        /// <summary>Reference to the full session (raw data + FFT + stats)</summary>
        public GuidingSession Session { get; set; } = null!;

        /// <summary>
        /// Short display label: "04/07 22:14 — Session 2 — 1435 fr."
        /// Computed once at creation, stable.
        /// </summary>
        public string DisplayLabel { get; set; } = string.Empty;

        /// <summary>Short source file name (no path)</summary>
        public string SourceFileName { get; set; } = string.Empty;

        // ── Selection for comparison ──────────────────────────────────────────

        /// <summary>
        /// True if this session is included in the active comparison.
        /// Observable so XAML bindings react to changes.
        /// </summary>
        [ObservableProperty] private bool _isIncludedInComparison = true;

        /// <summary>Updated by BuildComparison — true if this session has the best (lowest) total RMS.</summary>
        [ObservableProperty] private bool _isBestSession  = false;
        /// <summary>Updated by BuildComparison — true if this session has the worst (highest) total RMS.</summary>
        [ObservableProperty] private bool _isWorstSession = false;

        // ── Calibration grouping ──────────────────────────────────────────────

        /// <summary>
        /// Stable calibration group identifier.
        /// Computed from RaAngleDeg + DecAngleDeg + PixelScale, rounded.
        /// Sessions with the same GroupId share the same mechanical setup → reliable comparison.
        /// Sessions from different groups = repointing or remounting.
        /// Empty if no calibration available.
        /// </summary>
        public string CalibrationGroupId { get; set; } = string.Empty;

        /// <summary>
        /// Human-readable group label: "RA=47° DEC=21° 1.23\"/px"
        /// Displayed as tooltip and in the table.
        /// </summary>
        public string CalibrationGroupLabel { get; set; } = string.Empty;

        // ── Pre-computed statistics (for the table) ───────────────────────────

        public double RaRms     { get; set; }
        public double DecRms    { get; set; }
        public double TotalRms  { get; set; }
        public double RaDrift   { get; set; }   // arcsec/min
        public double DecDrift  { get; set; }
        public int    FrameCount { get; set; }
        public TimeSpan Duration { get; set; }
        public string QualityGrade { get; set; } = "—";

        /// <summary>Telescope altitude at session start (degrees). -1 if unknown.</summary>
        public double AltitudeDeg { get; set; } = -1;

        /// <summary>Pier side: "East", "West" or "" if unknown</summary>
        public string PierSide { get; set; } = string.Empty;

        // ── Computed properties ───────────────────────────────────────────────

        public DateTime StartTime  => Session.StartTime;
        public bool HasCalibration => Session.Calibration != null;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // FFT RESULT FOR MULTI-SESSION COMPARISON
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// FFT spectrum of a session, packaged for multi-session overlay.
    /// Contains the data needed to plot an OxyPlot series.
    /// </summary>
    public class FftComparisonResult
    {
        /// <summary>Series label in the OxyPlot legend</summary>
        public string SessionLabel { get; set; } = string.Empty;

        /// <summary>Frequencies in Hz (same layout as FftResult.Frequencies)</summary>
        public double[] Frequencies { get; set; } = Array.Empty<double>();

        /// <summary>Amplitudes in arcsec</summary>
        public double[] Amplitudes  { get; set; } = Array.Empty<double>();

        /// <summary>Significant peaks detected on this spectrum</summary>
        public List<FftPeak> Peaks  { get; set; } = new();

        /// <summary>Analysed axis (RA or DEC)</summary>
        public GuidingAxis Axis { get; set; } = GuidingAxis.RA;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // RECURRING FFT PEAKS (common to multiple sessions)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// An FFT peak detected in at least N sessions — signature of a mechanical defect.
    /// </summary>
    public class RecurringFftPeak
    {
        /// <summary>Central frequency of the peak (Hz), average of occurrences</summary>
        public double FrequencyHz { get; set; }

        /// <summary>Corresponding period (seconds)</summary>
        public double PeriodSeconds => FrequencyHz > 0 ? 1.0 / FrequencyHz : 0;

        /// <summary>Period in minutes</summary>
        public double PeriodMinutes => PeriodSeconds / 60.0;

        /// <summary>Mean amplitude of the peak across all sessions where it appears (arcsec)</summary>
        public double MeanAmplitudeArcsec { get; set; }

        /// <summary>Number of sessions where this peak was detected</summary>
        public int SessionCount { get; set; }

        /// <summary>Proportion of sessions where the peak is present (0–1)</summary>
        public double Prevalence { get; set; }

        /// <summary>Interpretation: "Periodic error (worm ~8 min)", "Vibration", etc.</summary>
        public string Interpretation { get; set; } = string.Empty;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // FULL MULTI-SESSION COMPARISON RESULT
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Container for all computed results for the comparison view.
    /// Produced by MultiSessionService.BuildComparison().
    /// </summary>
    public class MultiSessionComparison
    {
        // ── Comparison parameters ─────────────────────────────────────────────

        /// <summary>Scope: all loaded sessions, or only those from the same calibration group</summary>
        public ComparisonScope Scope { get; set; } = ComparisonScope.AllLoaded;

        /// <summary>
        /// Sessions included in this comparison (IsIncludedInComparison = true).
        /// Same order as in the summary table.
        /// </summary>
        public List<SessionSummary> IncludedSessions { get; set; } = new();

        /// <summary>
        /// Reference session for delta calculations (optional).
        /// If null, deltas are not computed.
        /// </summary>
        public SessionSummary? ReferenceSession { get; set; }

        // ── Global statistics ─────────────────────────────────────────────────

        /// <summary>Median RA RMS across all included sessions</summary>
        public double MedianRaRms   { get; set; }
        /// <summary>Median DEC RMS</summary>
        public double MedianDecRms  { get; set; }
        /// <summary>Median Total RMS</summary>
        public double MedianTotalRms { get; set; }

        /// <summary>Best session (lowest Total RMS)</summary>
        public SessionSummary? BestSession  { get; set; }
        /// <summary>Worst session (highest Total RMS)</summary>
        public SessionSummary? WorstSession { get; set; }

        // ── Trends ───────────────────────────────────────────────────────────

        /// <summary>
        /// True if the mean total RMS progresses significantly over time
        /// (progressive degradation → suspect: polar alignment drift, worsening backlash).
        /// </summary>
        public bool HasDegradationTrend { get; set; }

        /// <summary>
        /// Slope of the RMS vs time regression (arcsec/day).
        /// Positive = progressive degradation.
        /// </summary>
        public double RmsTrendArcsecPerDay { get; set; }

        // ── Compared FFT spectra ──────────────────────────────────────────────

        /// <summary>One spectrum per included session, RA axis</summary>
        public List<FftComparisonResult> FftRaSpectra  { get; set; } = new();
        /// <summary>One spectrum per included session, DEC axis</summary>
        public List<FftComparisonResult> FftDecSpectra { get; set; } = new();

        /// <summary>
        /// Recurring FFT peaks on the RA axis (present in ≥ MinSessionsForRecurrence sessions).
        /// Sorted by descending amplitude.
        /// </summary>
        public List<RecurringFftPeak> RecurringRaPeaks  { get; set; } = new();
        public List<RecurringFftPeak> RecurringDecPeaks { get; set; } = new();

        // ── Cross-session alerts ──────────────────────────────────────────────

        /// <summary>
        /// Global warning messages: mixed calibrations, very heterogeneous sessions, etc.
        /// Displayed at the top of the comparison panel.
        /// </summary>
        public List<string> Warnings { get; set; } = new();

        // ── Calibration groups present ────────────────────────────────────────

        /// <summary>
        /// Number of distinct calibration groups among the included sessions.
        /// > 1 = sessions from different setups mixed → warning displayed.
        /// </summary>
        public int CalibrationGroupCount { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // COMPARISON SCOPE
    // ═══════════════════════════════════════════════════════════════════════════

    public enum ComparisonScope
    {
        /// <summary>All loaded sessions (multiple files possible)</summary>
        AllLoaded,

        /// <summary>
        /// Only sessions sharing the same calibration group
        /// as the session selected in the main ComboBox.
        /// </summary>
        SameCalibrationGroup
    }
}
