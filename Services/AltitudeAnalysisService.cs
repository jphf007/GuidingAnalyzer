/*
 * AltitudeAnalysisService.cs — revu entièrement
 * - Adaptive window: worm period (confidence ≥70%) or 5 min
 * - Alt computed frame by frame by HA propagation
 * - Scatter = 2-min sub-sampling of the continuous sliding RMS
 * - Diagnosis: correlation, flip, gimbal lock, trend
 */

using System;
using System.Collections.Generic;
using System.Linq;
using GuidingAnalyzer.Models;

namespace GuidingAnalyzer.Services
{
    public class AltitudeAnalysisService
    {
        private const double DefaultWindowMinutes    = 5.0;
        private const double WormConfidenceThreshold = 70.0;
        private const int    MinFramesPerWindow      = 20;
        private const int    MinScatterPoints        = 4;

        // ── Latitude ─────────────────────────────────────────────────────────
        public double? EstimateSiteLatitude(List<GuidingSession> sessions)
        {
            var obs = sessions
                .Select(s => s.SessionInfo)
                .Where(i => i != null && i.AltDeg > 5 && i.AltDeg < 89 && Math.Abs(i.HourAngle) < 5.5)
                .Select(i => (A: ToRad(i!.AltDeg), D: ToRad(i.DecDeg), H: ToRad(i.HourAngle * 15.0)))
                .ToList();
            if (obs.Count < 3) return null;

            double bestLat = 48.0, bestErr = double.MaxValue;
            for (int k = 200; k <= 700; k++)
            {
                double lat = ToRad(k / 10.0);
                double err = obs.Sum(o => { double p = Math.Sin(o.D)*Math.Sin(lat)+Math.Cos(o.D)*Math.Cos(lat)*Math.Cos(o.H); double d = Math.Sin(o.A)-p; return d*d; });
                if (err < bestErr) { bestErr = err; bestLat = k / 10.0; }
            }
            return bestLat;
        }

        // ── Altitude at a given instant ─────────────────────────────────────────────
        public static double ComputeAltitude(double latDeg, double decDeg, double ha0Deg, DateTime t0, DateTime t)
        {
            double ha    = ha0Deg + (t - t0).TotalHours * 15.0;
            double sinAlt = Math.Sin(ToRad(decDeg)) * Math.Sin(ToRad(latDeg))
                          + Math.Cos(ToRad(decDeg)) * Math.Cos(ToRad(latDeg)) * Math.Cos(ToRad(ha));
            return ToDeg(Math.Asin(Math.Max(-1.0, Math.Min(1.0, sinAlt))));
        }

        // ── Adaptive window ────────────────────────────────────────────────
        private static (int frames, bool fromWorm, string label) ComputeWindow(
            List<GuidingSession> sessions, WormPeriodEstimate? worm)
        {
            double interval = EstimateFrameInterval(sessions);
            if (worm != null && worm.ConfidencePercent >= WormConfidenceThreshold && worm.PeriodSec > 0)
            {
                int f = Math.Max(MinFramesPerWindow, (int)Math.Round(worm.PeriodSec / interval));
                return (f, true, $"Worm: {worm.PeriodSec:F0} s  ({worm.ConfidencePercent:F0} % confidence)");
            }
            int fd = Math.Max(MinFramesPerWindow, (int)Math.Round(DefaultWindowMinutes * 60 / interval));
            string lbl = worm != null
                ? $"5 min default (worm confidence: {worm.ConfidencePercent:F0} % < 70 %)"
                : "5 min default (worm not detected)";
            return (fd, false, lbl);
        }

        private static double EstimateFrameInterval(List<GuidingSession> sessions)
        {
            var s = sessions.Where(s => s.RawPoints.Count > 10).OrderByDescending(s => s.RawPoints.Count).FirstOrDefault();
            if (s == null) return 1.5;
            var pts = s.RawPoints.Where(p => !p.IsDitherOrSettling).ToList();
            if (pts.Count < 2) return 1.5;
            return (pts[^1].Timestamp - pts[0].Timestamp).TotalSeconds / (pts.Count - 1);
        }

        // ── Main analysis ────────────────────────────────────────────────
        public AltitudeAnalysisResult Analyze(GuidingSession currentSession, List<GuidingSession> allSessions, double latDeg)
        {
            var result = new AltitudeAnalysisResult
            {
                EstimatedLatitudeDeg = latDeg,
                MeridianFlipDetected = allSessions
                    .Select(s => s.SessionInfo?.PierSide ?? "").Where(p => p != "").Distinct().Count() >= 2,
            };

            result.WormPeriod = EstimateWormPeriod(allSessions);

            var (wf, fw, wl) = ComputeWindow(allSessions, result.WormPeriod);
            result.WindowFrames   = wf;
            result.WindowFromWorm = fw;
            result.WindowLabel    = wl;

            result.SlidingPoints  = ComputeSlidingAll(allSessions, latDeg, wf);
            result.ScatterPoints  = BuildScatter(result.SlidingPoints);
            result.Degradation    = AnalyzeDegradation(result.ScatterPoints, result.MeridianFlipDetected);
            return result;
        }

        // ── Continuous sliding RMS ──────────────────────────────────────────────
        private static List<SlidingRmsPoint> ComputeSlidingAll(List<GuidingSession> sessions, double latDeg, int wf)
        {
            var frames = new List<(GuidingPoint pt, GuidingSession sess)>();
            foreach (var sess in sessions.OrderBy(s => s.StartTime))
                if (sess.SessionInfo != null)
                    foreach (var pt in sess.RawPoints.Where(p => !p.IsDitherOrSettling))
                        frames.Add((pt, sess));

            if (frames.Count < wf) return new();

            var result = new List<SlidingRmsPoint>();
            DateTime t0 = frames[0].pt.Timestamp;
            int half = wf / 2;

            for (int i = half; i < frames.Count - half; i++)
            {
                var win = frames.Skip(i - half).Take(wf).ToList();
                double raRms  = Rms(win.Select(w => w.pt.RaError));
                double decRms = Rms(win.Select(w => w.pt.DecError));
                if (raRms <= 0 && decRms <= 0) continue;

                var (ptM, sessM) = frames[i];
                double ha0 = sessM.SessionInfo!.HourAngle * 15.0;
                double alt = ComputeAltitude(latDeg, sessM.SessionInfo.DecDeg, ha0, sessM.StartTime, ptM.Timestamp);

                result.Add(new SlidingRmsPoint
                {
                    Timestamp    = ptM.Timestamp,
                    ElapsedMin   = (ptM.Timestamp - t0).TotalMinutes,
                    RaRms        = raRms,
                    DecRms       = decRms,
                    TotalRms     = Math.Sqrt(raRms * raRms + decRms * decRms),
                    AltitudeDeg  = alt,
                    PierSide     = sessM.SessionInfo.PierSide,
                    SessionIndex = sessM.SessionIndex,
                });
            }
            return result;
        }

        // ── Scatter (2-min sub-sampling) ─────────────────────────────
        private static List<RmsVsAltitudePoint> BuildScatter(List<SlidingRmsPoint> sliding)
        {
            if (sliding.Count == 0) return new();
            var result = new List<RmsVsAltitudePoint>();
            string? firstSide = sliding[0].PierSide;
            bool flipped = false;
            double lastElapsed = double.MinValue;

            foreach (var pt in sliding)
            {
                if (!flipped && pt.PierSide != firstSide) flipped = true;
                if (pt.ElapsedMin - lastElapsed < 2.0) continue;
                if (pt.TotalRms <= 0) continue;
                lastElapsed = pt.ElapsedMin;

                result.Add(new RmsVsAltitudePoint
                {
                    SessionIndex = pt.SessionIndex,
                    SessionLabel = $"t={pt.ElapsedMin:F0} min",
                    WindowCenter = pt.Timestamp,
                    ElapsedMin   = pt.ElapsedMin,
                    AltitudeDeg  = pt.AltitudeDeg,
                    RaRms        = pt.RaRms,
                    DecRms       = pt.DecRms,
                    TotalRms     = pt.TotalRms,
                    PierSide     = pt.PierSide,
                    IsAfterFlip  = flipped && pt.PierSide != firstSide,
                });
            }
            return result;
        }

        // ── Degradation diagnosis ────────────────────────────────────────────
        private static DegradationAnalysis AnalyzeDegradation(List<RmsVsAltitudePoint> scatter, bool flipDetected)
        {
            var d = new DegradationAnalysis();
            var valid = scatter.Where(p => p.TotalRms > 0 && p.TotalRms < 20).ToList();
            if (valid.Count < MinScatterPoints) return d;

            var alts  = valid.Select(p => p.AltitudeDeg).ToList();
            var rmss  = valid.Select(p => p.TotalRms).ToList();
            var times = valid.Select(p => p.ElapsedMin).ToList();
            d.PearsonAltRms       = PearsonR(alts, rmss);
            d.TrendArcsecPerHour  = LinearSlope(times, rmss) * 60.0;

            var west = valid.Where(p => p.PierSide.ToUpperInvariant().Contains("WEST")).ToList();
            var east = valid.Where(p => p.PierSide.ToUpperInvariant().Contains("EAST")).ToList();
            if (west.Count > 0) d.RmsWestMean = west.Average(p => p.TotalRms);
            if (east.Count > 0) d.RmsEastMean = east.Average(p => p.TotalRms);
            if (west.Count > 0 && east.Count > 0)
            {
                double hi = Math.Max(d.RmsWestMean, d.RmsEastMean);
                double lo = Math.Min(d.RmsWestMean, d.RmsEastMean);
                d.FlipDeltaPercent = hi > 0 ? (hi - lo) / hi * 100 : 0;
            }

            double maxAlt = valid.Max(p => p.AltitudeDeg);
            bool nearZenith = maxAlt > 78;
            double r = d.PearsonAltRms;

            d.DegradationDetected = Math.Abs(r) > 0.4 || d.FlipDeltaPercent > 20 || Math.Abs(d.TrendArcsecPerHour) > 0.3;

            if (r > 0.5)
            {
                d.Cause = nearZenith ? DegradationCause.GimbalLock : DegradationCause.Altitude;
                d.DiagnosisIcon = "📈"; d.DiagnosisColor = "#FF7043";
                d.MainMessage = nearZenith
                    ? $"RMS increasing near zenith ({maxAlt:F0}°)  —  r = {r:+0.00}"
                    : $"RMS positively correlated with altitude  —  r = {r:+0.00}";
                d.DetailMessage = nearZenith
                    ? "At high altitudes, polar alignment errors have an amplified angular effect. "
                      + "Cabling, flexion stress and DEC backlash behave differently near the zenith."
                    : "Optical flexure or imbalance accentuated at certain positions. "
                      + "Check focuser clamp tightness and balance.";
            }
            else if (r < -0.5)
            {
                d.Cause = DegradationCause.Altitude; d.DiagnosisIcon = "📉"; d.DiagnosisColor = "#64B5F6";
                d.MainMessage   = $"RMS increases at low altitudes  —  r = {r:+0.00}  (atmospheric effect)";
                d.DetailMessage = "Turbulence and differential refraction intensify below 40°. "
                                + "Normal behaviour, not mechanical.";
            }
            else if (flipDetected && d.FlipDeltaPercent > 20 && west.Count > 3 && east.Count > 3)
            {
                d.Cause = DegradationCause.MeridianFlip; d.DiagnosisIcon = "🔄"; d.DiagnosisColor = "#FFC107";
                string worse  = d.RmsWestMean > d.RmsEastMean ? "Ouest" : "Est";
                string better = worse == "Ouest" ? "Est" : "Ouest";
                d.MainMessage   = $"West/East asymmetry: +{d.FlipDeltaPercent:F0}% RMS after meridian flip";
                d.DetailMessage = $"RMS {worse} = {Math.Max(d.RmsWestMean, d.RmsEastMean):F3}\"  ·  "
                                + $"RMS {better} = {Math.Min(d.RmsWestMean, d.RmsEastMean):F3}\". "
                                + "Backlash Dec non compensé, flexion asymétrique, ou déséquilibre différent des deux côtés.";
            }
            else if (Math.Abs(d.TrendArcsecPerHour) > 0.3)
            {
                d.Cause = DegradationCause.Trend;
                d.DiagnosisIcon = d.TrendArcsecPerHour > 0 ? "📈" : "📉"; d.DiagnosisColor = "#CE93D8";
                string dir = d.TrendArcsecPerHour > 0 ? "dégradation" : "amélioration";
                d.MainMessage   = $"Tendance temporelle : {dir} de {Math.Abs(d.TrendArcsecPerHour):F2}\"/h";
                d.DetailMessage = d.TrendArcsecPerHour > 0
                    ? "Mise en température, condensation progressive, ou dégradation du seeing en cours de nuit."
                    : "Amélioration progressive — stabilisation thermique ou amélioration du seeing.";
            }
            else
            {
                d.Cause = DegradationCause.None; d.DiagnosisIcon = "✓"; d.DiagnosisColor = "#81C784";
                d.MainMessage   = $"Stable guiding  —  r = {r:+0.00}  (no significant degradation)";
                d.DetailMessage = "RMS is independent of altitude and meridian position.";
            }
            return d;
        }

        // ── Worm gear ─────────────────────────────────────────────────────
        public WormPeriodEstimate? EstimateWormPeriod(List<GuidingSession> sessions)
        {
            var best = sessions.Where(s => s.FftRa != null && s.FftRa.Frequencies.Length > 4 && s.RawPoints.Count > 0)
                               .OrderByDescending(s => s.RawPoints.Count).FirstOrDefault();
            if (best?.FftRa == null) return null;
            var fft = best.FftRa;

            double bAmp = 0, bFreq = 0; int bIdx = -1;
            for (int i = 1; i < fft.Frequencies.Length; i++)
            {
                if (fft.Frequencies[i] < 1e-6) continue;
                double ps = 3600.0 / fft.Frequencies[i];
                if (ps < 200 || ps > 2500) continue;
                if (fft.Amplitudes[i] > bAmp) { bAmp = fft.Amplitudes[i]; bFreq = fft.Frequencies[i]; bIdx = i; }
            }
            if (bIdx < 0 || bAmp < 1e-6) return null;

            double period  = 3600.0 / bFreq;
            double noise   = Median(fft.Amplitudes.Skip(1).ToArray());
            double snr     = noise > 1e-10 ? bAmp / noise : 0;
            var    clean   = best.RawPoints.Where(p => !p.IsDitherOrSettling).ToList();
            double dur     = clean.Count > 1 ? (clean[^1].Timestamp - clean[0].Timestamp).TotalSeconds : 0;
            double cycles  = period > 0 ? dur / period : 0;
            double phd2    = best.SessionInfo?.PecPeriodLength ?? 0;
            bool   cohPec  = phd2 > 0 && period / phd2 is >= 0.85 and <= 1.15;

            double cs = cycles >= 2 ? 40 : cycles >= 1 ? 25+(cycles-1)*15 : cycles >= 0.5 ? 12+(cycles-0.5)*26 : cycles*24;
            double ss = snr >= 10 ? 40 : snr >= 5 ? 25+(snr-5)*3 : snr >= 2 ? 10+(snr-2)*5 : snr*5;
            double ps2 = phd2 > 0 ? (cohPec ? 20 : -10) : 0;
            double conf = Math.Max(0, Math.Min(100, cs + ss + ps2));

            string lbl, col;
            if      (conf >= 75) { lbl = "High";       col = "#81C784"; }
            else if (conf >= 50) { lbl = "Medium";      col = "#FFC107"; }
            else if (conf >= 25) { lbl = "Low";       col = "#FF7043"; }
            else                 { lbl = "Insufficient"; col = "#EF5350"; }

            var sb = new System.Text.StringBuilder();
            if (cycles < 1)       sb.Append($"Session too short ({cycles:F2} cycle). ");
            else if (cycles < 2)  sb.Append($"Only 1 cycle ({cycles:F1}×). ");
            if (snr < 5)          sb.Append($"Low SNR ({snr:F1}). ");
            if (phd2 > 0 && !cohPec) sb.Append($"Diverges from PHD2 ({phd2:F0} s). ");
            if (sb.Length == 0)   sb.Append("All criteria satisfied.");

            return new WormPeriodEstimate
            {
                PeriodSec = period, Phd2PeriodSec = phd2, AmplitudeArcsec = bAmp,
                SnrRatio = snr, CyclesCovered = cycles, ConfidencePercent = conf,
                ConfidenceLabel = lbl, ConfidenceColor = col, ConfidenceDetail = sb.ToString().Trim(),
                ConsistentWithPhd2 = cohPec,
                SourceSessionLabel = $"{best.StartTime:HH:mm} Sess.{best.SessionIndex} ({clean.Count} fr.)",
            };
        }

        // ── Utilities ───────────────────────────────────────────────────────
        private static double Rms(IEnumerable<double> v) { var l = v.ToList(); if (l.Count==0) return 0; double m=l.Average(); return Math.Sqrt(l.Sum(x=>(x-m)*(x-m))/l.Count); }
        private static double PearsonR(List<double> xs, List<double> ys) { if (xs.Count!=ys.Count||xs.Count<2) return 0; double mx=xs.Average(),my=ys.Average(); double n=xs.Zip(ys,(x,y)=>(x-mx)*(y-my)).Sum(),dx=Math.Sqrt(xs.Sum(x=>(x-mx)*(x-mx))),dy=Math.Sqrt(ys.Sum(y=>(y-my)*(y-my))); return (dx<1e-10||dy<1e-10)?0:n/(dx*dy); }
        private static double LinearSlope(List<double> xs, List<double> ys) { if (xs.Count<2) return 0; double mx=xs.Average(),my=ys.Average(); double n=xs.Zip(ys,(x,y)=>(x-mx)*(y-my)).Sum(),d=xs.Sum(x=>(x-mx)*(x-mx)); return d<1e-10?0:n/d; }
        private static double Median(double[] v) { if (v.Length==0) return 0; var s=v.OrderBy(x=>x).ToArray(); int m=s.Length/2; return s.Length%2==0?(s[m-1]+s[m])/2.0:s[m]; }
        private static double ToRad(double d) => d*Math.PI/180.0;
        private static double ToDeg(double r) => r*180.0/Math.PI;
    }
}
