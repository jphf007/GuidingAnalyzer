/*
 * MultiSessionService.cs
 * ======================
 * Service responsible for:
 *   1. Maintaining the collection of all loaded sessions (multi-file)
 *   2. Computing CalibrationGroupId to group sessions with identical setup
 *   3. Building the MultiSessionComparison (summary table + overlaid FFT + recurring peaks)
 *
 * Design: no shared mutable state → thread-safe for calculations.
 * The _loadedSessions list is the only stateful data, updated from the UI thread.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GuidingAnalyzer.Models;

namespace GuidingAnalyzer.Services
{
    public class MultiSessionService
    {
        // ── Configuration ─────────────────────────────────────────────────────

        /// <summary>
        /// Angular tolerance to group two calibrations as "identical".
        /// 3° covers small polar alignment variations between sessions.
        /// </summary>
        private const double CalibrationAngleTolerance = 3.0;

        /// <summary>
        /// Tolerance on pixel scale (arcsec/px) for grouping.
        /// 0.05 covers rounding artefacts in the log.
        /// </summary>
        private const double CalibrationScaleTolerance = 0.05;

        /// <summary>
        /// An FFT peak is "recurring" if it appears in at least this proportion of sessions.
        /// </summary>
        private const double RecurrenceThreshold = 0.5;   // 50 %

        /// <summary>
        /// Frequency tolerance to consider two peaks as "the same" (Hz).
        /// Corresponds to ~±30 s on an 8-min period.
        /// </summary>
        private const double FrequencyMatchTolerance = 0.0001;

        // ── State ──────────────────────────────────────────────────────────────

        /// <summary>
        /// All loaded sessions, across all files.
        /// Key = full path of the source file.
        /// </summary>
        private readonly Dictionary<string, List<GuidingSession>> _sessionsByFile = new();

        private readonly GuidingDataService _dataService  = new();
        private readonly FftAnalysisService  _fftService   = new();
        private readonly StatisticsService   _statsService = new();

        // ─────────────────────────────────────────────────────────────────────
        // FILE MANAGEMENT
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Loads a PHD2 log file and adds its sessions to the collection.
        /// If the file was already loaded, reloads it (refresh).
        /// Returns the list of sessions from the loaded file.
        /// </summary>
        public async Task<List<GuidingSession>> AddFileAsync(string filePath)
        {
            var sessions = await _dataService.LoadAllSessionsAsync(filePath);
            await Task.Run(() => ComputeSessionData(sessions));
            _sessionsByFile[filePath] = sessions;
            return sessions;
        }

        /// <summary>
        /// Directly injects already-computed sessions (FFT + stats present).
        /// Used by the main VM to avoid reparsing the current file.
        /// </summary>
        public void SetSessions(string filePath, List<GuidingSession> sessions)
        {
            _sessionsByFile[filePath] = sessions;
        }

        /// <summary>
        /// Computes stats + FFT for sessions that do not yet have this data.
        /// </summary>
        private void ComputeSessionData(List<GuidingSession> sessions)
        {
            foreach (var session in sessions)
            {
                // Statistiques RMS
                if (session.Statistics == null)
                {
                    var cleanPoints = session.RawPoints.Where(p => !p.IsDitherOrSettling).ToList();
                    if (cleanPoints.Count >= 4)
                        session.Statistics = _statsService.ComputeStatistics(cleanPoints);
                }

                // FFT (si pas déjà calculée par le VM principal)
                if (session.FftRa == null && session.RawPoints.Count >= 16)
                {
                    double samplingPeriod = session.RawPoints.Count > 1
                        ? (session.RawPoints[^1].Timestamp - session.RawPoints[0].Timestamp)
                            .TotalSeconds / (session.RawPoints.Count - 1)
                        : 1.0;
                    if (samplingPeriod <= 0) samplingPeriod = 1.0;

                    var raSignal  = session.RawPoints.Select(f => f.RaError).ToArray();
                    var decSignal = session.RawPoints.Select(f => f.DecError).ToArray();

                    session.FftRa  = _fftService.ComputeFft(raSignal,  samplingPeriod, GuidingAxis.RA);
                    session.FftDec = _fftService.ComputeFft(decSignal, samplingPeriod, GuidingAxis.Dec);
                }
            }
        }

        /// <summary>
        /// Removes all sessions of a file from the collection.
        /// </summary>
        public void RemoveFile(string filePath)
        {
            _sessionsByFile.Remove(filePath);
        }

        /// <summary>Liste des chemins de fichiers actuellement chargés.</summary>
        public IReadOnlyList<string> LoadedFilePaths =>
            _sessionsByFile.Keys.ToList();

        /// <summary>
        /// All sessions from all loaded files, sorted by start date.
        /// </summary>
        public IReadOnlyList<GuidingSession> AllSessions =>
            _sessionsByFile.Values
                           .SelectMany(s => s)
                           .OrderBy(s => s.StartTime)
                           .ToList();

        // ─────────────────────────────────────────────────────────────────────
        // CALIBRATION GROUPING
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Computes a stable group identifier for a session.
        /// Two sessions with the same GroupId share the same mechanical setup.
        ///
        /// Algorithm: quantise each calibration value by tolerance step,
        /// then concatenate the quantised values as a string.
        /// Exemple : "RA47_DEC21_SC123" (angles arrondis à 3°, scale à 0.05"/px)
        /// </summary>
        public static string ComputeCalibrationGroupId(CalibrationSession? cal)
        {
            if (cal == null) return string.Empty;

            // Quantification : on divise par la tolérance et on arrondit à l'entier
            int raQ  = (int)Math.Round(cal.RaAngleDeg  / CalibrationAngleTolerance);
            int decQ = (int)Math.Round(cal.DecAngleDeg / CalibrationAngleTolerance);
            int scQ  = (int)Math.Round(cal.PixelScaleArcSecPx / CalibrationScaleTolerance);

            return $"RA{raQ}_DEC{decQ}_SC{scQ}";
        }

        /// <summary>
        /// Produces a human-readable label for the UI : "RA=47° DEC=21° 1.23\"/px"
        /// </summary>
        public static string ComputeCalibrationGroupLabel(CalibrationSession? cal)
        {
            if (cal == null) return "Sans calibration";
            return $"RA={cal.RaAngleDeg:F0}°  DEC={cal.DecAngleDeg:F0}°  {cal.PixelScaleArcSecPx:F2}\"/px";
        }

        // ─────────────────────────────────────────────────────────────────────
        // BUILDING SUMMARIES
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Builds the SessionSummary list for all loaded sessions.
        /// Call after each file addition/removal.
        /// </summary>
        public List<SessionSummary> BuildAllSummaries()
        {
            var result = new List<SessionSummary>();

            foreach (var (filePath, sessions) in _sessionsByFile)
            {
                string fileName = Path.GetFileName(filePath);
                foreach (var session in sessions)
                    result.Add(BuildSummary(session, fileName));
            }

            return result.OrderBy(s => s.StartTime).ToList();
        }

        /// <summary>
        /// Builds the summary for an individual session.
        /// </summary>
        public static SessionSummary BuildSummary(GuidingSession session, string? fileNameOverride = null)
        {
            var stats = session.Statistics;
            var cal   = session.Calibration;
            var info  = session.SessionInfo;

            string fileName = fileNameOverride ?? Path.GetFileName(session.SourceFile);

            // Short label: "07/04 22h14 — Sess.2 — 1435 fr."
            string label = $"{session.StartTime:dd/MM HH:mm}  —  Sess.{session.SessionIndex}  —  {session.RawPoints.Count} fr.";

            return new SessionSummary
            {
                Session               = session,
                DisplayLabel          = label,
                SourceFileName        = fileName,
                IsIncludedInComparison = true,

                CalibrationGroupId    = ComputeCalibrationGroupId(cal),
                CalibrationGroupLabel = ComputeCalibrationGroupLabel(cal),

                RaRms       = stats?.RaRms    ?? 0,
                DecRms      = stats?.DecRms   ?? 0,
                TotalRms    = stats?.TotalRms ?? 0,
                RaDrift     = stats?.RaDriftArcsecPerMin  ?? 0,
                DecDrift    = stats?.DecDriftArcsecPerMin ?? 0,
                FrameCount  = stats?.FrameCount  ?? session.RawPoints.Count,
                Duration    = stats?.SessionDuration ?? TimeSpan.Zero,
                QualityGrade = stats?.QualityGrade ?? "—",

                AltitudeDeg = info?.AltDeg ?? -1,
                PierSide    = info?.PierSide ?? string.Empty,
            };
        }

        // ─────────────────────────────────────────────────────────────────────
        // BUILDING THE COMPARISON
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Builds the MultiSessionComparison from the provided summaries.
        /// Sessions with IsIncludedInComparison = false are ignored.
        /// </summary>
        public MultiSessionComparison BuildComparison(
            List<SessionSummary> summaries,
            ComparisonScope scope,
            SessionSummary? referenceSession = null)
        {
            var result = new MultiSessionComparison
            {
                Scope            = scope,
                ReferenceSession = referenceSession,
            };

            // Filtrer selon scope et flag de sélection
            var included = summaries
                .Where(s => s.IsIncludedInComparison)
                .ToList();

            if (scope == ComparisonScope.SameCalibrationGroup && referenceSession != null)
            {
                string groupId = referenceSession.CalibrationGroupId;
                included = included
                    .Where(s => s.CalibrationGroupId == groupId || string.IsNullOrEmpty(groupId))
                    .ToList();
            }

            result.IncludedSessions = included;

            if (included.Count == 0) return result;

            // ── Statistiques globales ────────────────────────────────────────
            var rmsList = included.Select(s => s.TotalRms).OrderBy(v => v).ToList();
            result.MedianRaRms    = Median(included.Select(s => s.RaRms));
            result.MedianDecRms   = Median(included.Select(s => s.DecRms));
            result.MedianTotalRms = Median(included.Select(s => s.TotalRms));

            result.BestSession  = included.MinBy(s => s.TotalRms);
            result.WorstSession = included.MaxBy(s => s.TotalRms);

            // Mettre à jour les flags sur chaque SessionSummary pour binding XAML simple
            foreach (var s in included)
            {
                s.IsBestSession  = s == result.BestSession;
                s.IsWorstSession = s == result.WorstSession;
            }

            // ── Tendance temporelle ──────────────────────────────────────────
            ComputeRmsTrend(included, result);

            // ── Groupes de calibration ───────────────────────────────────────
            result.CalibrationGroupCount = included
                .Select(s => s.CalibrationGroupId)
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct()
                .Count();

            // ── Alertes ──────────────────────────────────────────────────────
            BuildWarnings(included, result);

            // ── Spectres FFT ─────────────────────────────────────────────────
            BuildFftComparisons(included, result);

            return result;
        }

        // ─────────────────────────────────────────────────────────────────────
        // RMS TREND
        // ─────────────────────────────────────────────────────────────────────

        private static void ComputeRmsTrend(List<SessionSummary> sessions, MultiSessionComparison result)
        {
            if (sessions.Count < 3) return;

            // Régression linéaire RMS_total ~ temps (en jours depuis la 1ère session)
            var t0   = sessions.Min(s => s.StartTime);
            var xs   = sessions.Select(s => (s.StartTime - t0).TotalDays).ToArray();
            var ys   = sessions.Select(s => s.TotalRms).ToArray();
            double slope = LinearRegressionSlope(xs, ys);

            result.RmsTrendArcsecPerDay = slope;

            // Dégradation significative : >0.05 arcsec/jour et sens positif
            result.HasDegradationTrend = slope > 0.05;
        }

        // ─────────────────────────────────────────────────────────────────────
        // ALERTS
        // ─────────────────────────────────────────────────────────────────────

        private static void BuildWarnings(List<SessionSummary> sessions, MultiSessionComparison result)
        {
            // Calibrations mixtes
            if (result.CalibrationGroupCount > 1)
                result.Warnings.Add(
                    $"⚠ {result.CalibrationGroupCount} distinct calibration groups detected — " +
                    "sessions likely come from different setups. " +
                    "Activez le filtre \"Même calibration\" pour une comparaison fiable.");

            // Sessions sans calibration
            int noCalCount = sessions.Count(s => string.IsNullOrEmpty(s.CalibrationGroupId));
            if (noCalCount > 0)
                result.Warnings.Add(
                    $"ℹ {noCalCount} session(s) without preceding calibration in the log.");

            // Dégradation progressive
            if (result.HasDegradationTrend)
                result.Warnings.Add(
                    $"📈 Dégradation progressive détectée : +{result.RmsTrendArcsecPerDay:F3}\"/jour. " +
                    "Possible causes: drifting polar alignment, increasing backlash, flexure.");
        }

        // ─────────────────────────────────────────────────────────────────────
        // FFT COMPARISON
        // ─────────────────────────────────────────────────────────────────────

        private static void BuildFftComparisons(List<SessionSummary> sessions, MultiSessionComparison result)
        {
            foreach (var summary in sessions)
            {
                var session = summary.Session;
                string label = summary.DisplayLabel;

                // RA
                var fftRa = session.FftRa;
                if (fftRa != null && fftRa.Frequencies.Length > 0)
                {
                    result.FftRaSpectra.Add(new FftComparisonResult
                    {
                        SessionLabel = label,
                        Frequencies  = fftRa.Frequencies,
                        Amplitudes   = fftRa.Amplitudes,
                        Peaks        = fftRa.SignificantPeaks,
                        Axis         = GuidingAxis.RA,
                    });
                }

                // DEC
                var fftDec = session.FftDec;
                if (fftDec != null && fftDec.Frequencies.Length > 0)
                {
                    result.FftDecSpectra.Add(new FftComparisonResult
                    {
                        SessionLabel = label,
                        Frequencies  = fftDec.Frequencies,
                        Amplitudes   = fftDec.Amplitudes,
                        Peaks        = fftDec.SignificantPeaks,
                        Axis         = GuidingAxis.Dec,
                    });
                }
            }

            // Pics récurrents
            result.RecurringRaPeaks  = DetectRecurringPeaks(result.FftRaSpectra,  sessions.Count);
            result.RecurringDecPeaks = DetectRecurringPeaks(result.FftDecSpectra, sessions.Count);
        }

        /// <summary>
        /// Identifies FFT peaks present in multiple sessions.
        /// A peak is "the same" if its frequency is within FrequencyMatchTolerance.
        /// </summary>
        private static List<RecurringFftPeak> DetectRecurringPeaks(
            List<FftComparisonResult> spectra, int totalSessions)
        {
            if (spectra.Count < 2) return new();

            // Collecter tous les pics de tous les spectres
            var allPeaks = spectra
                .SelectMany(s => s.Peaks.Select(p => (
                    FreqHz:      p.FrequencyCph / 3600.0,   // CPH → Hz
                    AmplArcsec:  p.AmplitudeArcsec,
                    Interpretation: p.Interpretation
                )))
                .ToList();

            if (allPeaks.Count == 0) return new();

            // Regrouper les pics proches en fréquence
            var groups = new List<List<(double FreqHz, double Ampl, string Interp)>>();

            foreach (var peak in allPeaks.OrderBy(p => p.FreqHz))
            {
                var matched = groups.FirstOrDefault(g =>
                    Math.Abs(g.Average(p => p.FreqHz) - peak.FreqHz) <= FrequencyMatchTolerance);

                if (matched != null)
                    matched.Add(peak);
                else
                    groups.Add(new() { peak });
            }

            // Ne garder que les groupes récurrents
            int minSessions = Math.Max(2, (int)Math.Ceiling(totalSessions * RecurrenceThreshold));

            return groups
                .Where(g => g.Count >= minSessions)
                .Select(g => new RecurringFftPeak
                {
                    FrequencyHz          = g.Average(p => p.FreqHz),
                    MeanAmplitudeArcsec  = g.Average(p => p.Ampl),
                    SessionCount         = g.Count,
                    Prevalence           = (double)g.Count / totalSessions,
                    Interpretation       = g.GroupBy(p => p.Interp)
                                           .OrderByDescending(gr => gr.Count())
                                           .First().Key,
                })
                .OrderByDescending(p => p.MeanAmplitudeArcsec)
                .ToList();
        }

        // ─────────────────────────────────────────────────────────────────────
        // MATHEMATICAL HELPERS
        // ─────────────────────────────────────────────────────────────────────

        private static double Median(IEnumerable<double> values)
        {
            var sorted = values.OrderBy(v => v).ToArray();
            if (sorted.Length == 0) return 0;
            int mid = sorted.Length / 2;
            return sorted.Length % 2 == 0
                ? (sorted[mid - 1] + sorted[mid]) / 2.0
                : sorted[mid];
        }

        private static double LinearRegressionSlope(double[] x, double[] y)
        {
            if (x.Length < 2) return 0;
            double xMean = x.Average();
            double yMean = y.Average();
            double num   = x.Zip(y, (xi, yi) => (xi - xMean) * (yi - yMean)).Sum();
            double den   = x.Select(xi => (xi - xMean) * (xi - xMean)).Sum();
            return den < 1e-12 ? 0 : num / den;
        }
    }
}
