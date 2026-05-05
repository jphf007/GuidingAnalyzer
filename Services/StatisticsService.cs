using System;
using System.Collections.Generic;
using System.Linq;
using GuidingAnalyzer.Models;

namespace GuidingAnalyzer.Services
{
    /// <summary>
    /// Computes guiding quality statistics: RMS, peak, drift, grade.
    /// </summary>
    public class StatisticsService
    {
        public GuidingStatistics ComputeStatistics(List<GuidingPoint> points)
        {
            var stats = new GuidingStatistics();
            if (points.Count == 0) return stats;
            var valid = points.Where(p => p.IsGuideStep).ToList();
            if (valid.Count == 0) return stats;

            stats.FrameCount        = valid.Count;
            stats.ValidFramePercent = 100.0 * valid.Count / points.Count;
            if (valid.Count > 1)
                stats.SessionDuration = valid.Last().Timestamp - valid.First().Timestamp;

            var raErr = valid.Select(p => p.RaError).ToArray();
            stats.RaRms              = ComputeRms(raErr);
            stats.RaPeak             = raErr.Max(e => Math.Abs(e));
            stats.RaPeakToPeak       = raErr.Max() - raErr.Min();
            stats.RaDriftArcsecPerMin = ComputeDriftRate(valid.Select(p => p.Timestamp).ToArray(), raErr);

            var decErr = valid.Select(p => p.DecError).ToArray();
            stats.DecRms              = ComputeRms(decErr);
            stats.DecPeak             = decErr.Max(e => Math.Abs(e));
            stats.DecPeakToPeak       = decErr.Max() - decErr.Min();
            stats.DecDriftArcsecPerMin = ComputeDriftRate(valid.Select(p => p.Timestamp).ToArray(), decErr);

            AssignQualityGrade(stats);

            // ── Advanced statistics ──────────────────────────────────────────
            ComputeAdvancedStats(stats, raErr, decErr);

            return stats;
        }

        private void ComputeAdvancedStats(GuidingStatistics stats, double[] raErr, double[] decErr)
        {
            if (raErr.Length < 4) return;

            // ── Skewness and Kurtosis ────────────────────────────────────────
            stats.RaSkewness  = ComputeSkewness(raErr);
            stats.DecSkewness = ComputeSkewness(decErr);
            stats.RaKurtosis  = ComputeKurtosis(raErr);
            stats.DecKurtosis = ComputeKurtosis(decErr);

            // ── Pearson RA/DEC correlation ───────────────────────────────────
            stats.RaDecCorrelation = ComputePearson(raErr, decErr);

            // ── Stationarity: variance ratio 2nd / 1st half ──────────────────
            int half = raErr.Length / 2;
            double[] raFirst   = raErr.Take(half).ToArray();
            double[] raSecond  = raErr.Skip(half).ToArray();
            double[] decFirst  = decErr.Take(half).ToArray();
            double[] decSecond = decErr.Skip(half).ToArray();

            double varRaFirst   = ComputeVariance(raFirst);
            double varRaSecond  = ComputeVariance(raSecond);
            double varDecFirst  = ComputeVariance(decFirst);
            double varDecSecond = ComputeVariance(decSecond);

            stats.RaStabilityRatio  = varRaFirst  > 1e-10 ? varRaSecond  / varRaFirst  : 1.0;
            stats.DecStabilityRatio = varDecFirst > 1e-10 ? varDecSecond / varDecFirst : 1.0;
        }

        /// <summary>Computes PEC harmonics from the RA FFT — called after FFT is available</summary>
        public void ComputePecHarmonics(GuidingStatistics stats, FftResult fftRa)
        {
            if (fftRa == null || fftRa.Frequencies.Length < 2) return;

            // Build list (period in s, amplitude) — ignore DC (index 0)
            // Frequencies[] is in cycles/hour → period = 3600 / freq (seconds)
            var peaks = new List<(double PeriodSec, double AmplitudeArcsec)>();
            for (int i = 1; i < fftRa.Frequencies.Length; i++)
            {
                if (fftRa.Frequencies[i] < 1e-6) continue;
                double periodSec = 3600.0 / fftRa.Frequencies[i];
                if (periodSec < 5 || periodSec > 6000) continue;
                peaks.Add((periodSec, fftRa.Amplitudes[i]));
            }

            if (peaks.Count == 0) return;

            // Top 5 harmonics by descending amplitude
            stats.RaHarmonics = peaks
                .OrderByDescending(p => p.AmplitudeArcsec)
                .Take(5)
                .OrderBy(p => p.PeriodSec)  // re-display by ascending period
                .ToList();

            // Worm = strongest peak in the 200–2000 s range (typical amateur mount)
            var wormCandidate = peaks
                .Where(p => p.PeriodSec >= 200 && p.PeriodSec <= 2000)
                .OrderByDescending(p => p.AmplitudeArcsec)
                .FirstOrDefault();

            if (wormCandidate != default)
            {
                stats.WormPeriodSeconds   = wormCandidate.PeriodSec;
                stats.WormAmplitudeArcsec = wormCandidate.AmplitudeArcsec;
            }
        }

        private static double ComputeSkewness(double[] values)
        {
            int n = values.Length;
            if (n < 3) return 0;
            double mean = values.Average();
            double std  = Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / n);
            if (std < 1e-10) return 0;
            return values.Sum(v => Math.Pow((v - mean) / std, 3)) / n;
        }

        private static double ComputeKurtosis(double[] values)
        {
            int n = values.Length;
            if (n < 4) return 0;
            double mean = values.Average();
            double std  = Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / n);
            if (std < 1e-10) return 0;
            // Excess kurtosis (normal distribution = 0)
            return values.Sum(v => Math.Pow((v - mean) / std, 4)) / n - 3.0;
        }

        private static double ComputePearson(double[] x, double[] y)
        {
            int n = Math.Min(x.Length, y.Length);
            if (n < 2) return 0;
            double meanX = x.Take(n).Average(), meanY = y.Take(n).Average();
            double num = 0, denX = 0, denY = 0;
            for (int i = 0; i < n; i++)
            {
                double dx = x[i] - meanX, dy = y[i] - meanY;
                num  += dx * dy;
                denX += dx * dx;
                denY += dy * dy;
            }
            double denom = Math.Sqrt(denX * denY);
            return denom < 1e-10 ? 0 : num / denom;
        }

        private static double ComputeVariance(double[] values)
        {
            if (values.Length == 0) return 0;
            double mean = values.Average();
            return values.Sum(v => (v - mean) * (v - mean)) / values.Length;
        }

        private double ComputeRms(double[] values)
        {
            if (values.Length == 0) return 0;
            return Math.Sqrt(values.Sum(v => v * v) / values.Length);
        }

        private double ComputeDriftRate(DateTime[] timestamps, double[] values)
        {
            if (timestamps.Length < 2) return 0;
            int n = timestamps.Length;
            double[] x   = timestamps.Select(t => (t - timestamps[0]).TotalMinutes).ToArray();
            double sumX  = x.Sum(), sumY = values.Sum();
            double sumXY = x.Zip(values, (xi, yi) => xi * yi).Sum();
            double sumX2 = x.Sum(xi => xi * xi);
            double denom = n * sumX2 - sumX * sumX;
            if (Math.Abs(denom) < 1e-10) return 0;
            return (n * sumXY - sumX * sumY) / denom;
        }

        // ── Drift on reconstructed mount curve ───────────────────────────────
        // The reconstructed curve represents the real mount position.
        // Its slope = pure mechanical drift (polar, flexure).
        // R² measures whether the drift is truly linear.
        public DriftAnalysis ComputeDriftOnMountCurve(List<MountCurvePoint> curve)
        {
            var result = new DriftAnalysis();
            var clean  = curve.Where(p => !p.IsDitherOrSettling).ToList();
            if (clean.Count < 10) return result;

            double durationMin    = (clean.Last().Timestamp - clean.First().Timestamp).TotalMinutes;
            double durationFactor = Math.Min(1.0, durationMin / 20.0); // reliable after 20 min

            void ComputeAxis(
                IList<double> y,
                out double driftPerMin, out double rSquared, out double confidence)
            {
                int n = y.Count;
                double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
                for (int i = 0; i < n; i++)
                {
                    sumX += i; sumY += y[i];
                    sumXY += i * y[i]; sumX2 += (double)i * i;
                }
                double d = n * sumX2 - sumX * sumX;
                if (Math.Abs(d) < 1e-10) { driftPerMin = 0; rSquared = 0; confidence = 0; return; }

                double slope     = (n * sumXY - sumX * sumY) / d;
                double intercept = (sumY - slope * sumX) / n;

                // Convert slope (arcsec/frame) → arcsec/min
                double frameIntervalMin = durationMin / Math.Max(1, n - 1);
                driftPerMin = frameIntervalMin > 0 ? slope / frameIntervalMin : 0;

                // R² = 1 − SS_res / SS_tot
                double mean  = sumY / n;
                double ssTot = y.Sum(v => (v - mean) * (v - mean));
                double ssRes = 0;
                for (int i = 0; i < n; i++)
                    ssRes += Math.Pow(y[i] - (slope * i + intercept), 2);
                rSquared  = ssTot > 1e-10 ? Math.Max(0, 1.0 - ssRes / ssTot) : 0;
                confidence = rSquared * durationFactor;
            }

            var raVals  = clean.Select(p => p.RaMountPosition).ToList();
            var decVals = clean.Select(p => p.DecMountPosition).ToList();

            ComputeAxis(raVals,  out double raDrift,  out double raR2,  out double raConf);
            ComputeAxis(decVals, out double decDrift, out double decR2, out double decConf);

            result.RaDriftArcsecPerMin  = raDrift;
            result.DecDriftArcsecPerMin = decDrift;
            result.RaRSquared           = raR2;
            result.DecRSquared          = decR2;
            result.RaConfidence         = raConf;
            result.DecConfidence        = decConf;
            return result;
        }

        private void AssignQualityGrade(GuidingStatistics stats)
        {
            double rms   = stats.TotalRms;
            string grade = rms < 0.3 ? "A+" : rms < 0.5 ? "A" : rms < 0.7 ? "B+" :
                           rms < 1.0 ? "B"  : rms < 1.5 ? "C" : rms < 2.5 ? "D"  : "F";
            string desc  = grade switch {
                "A+" => "Exceptional guiding — optimal conditions",
                "A"  => "Excellent guiding — suitable for all imaging",
                "B+" => "Very good guiding — suitable for long focal lengths",
                "B"  => "Good guiding — suitable for most setups",
                "C"  => "Acceptable guiding — optimisation recommended",
                "D"  => "Poor guiding — problem needs correcting",
                _    => "Failed guiding — intervention required"
            };
            double maxDrift = Math.Max(Math.Abs(stats.RaDriftArcsecPerMin), Math.Abs(stats.DecDriftArcsecPerMin));
            if (maxDrift > 1.0 && grade[0] < 'C') grade = "C";
            stats.QualityGrade       = grade;
            stats.QualityDescription = desc;
        }
    }
}
