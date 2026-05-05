using System;
using System.Collections.Generic;
using System.Linq;
using GuidingAnalyzer.Models;

namespace GuidingAnalyzer.Services
{
    // Detects anomalies in guiding data.
    // Detected types: periodic error, flexure, backlash, polar alignment,
    //                  vibrations, aggressive corrections, poor guide star.
    public class AnomalyDetector
    {
        public List<GuidingAnomaly> Detect(GuidingSession session)
        {
            var anomalies = new List<GuidingAnomaly>();
            if (session.RawPoints.Count == 0) return anomalies;

            DetectPeriodicError(session, anomalies);
            DetectPolarAlignmentError(session, anomalies);
            DetectBacklash(session, anomalies);
            DetectAggressiveCorrections(session, anomalies);
            DetectPoorGuideStar(session, anomalies);
            DetectDifferentialFlexure(session, anomalies);

            return anomalies.OrderByDescending(a => (int)a.Severity).ToList();
        }

        private void DetectPeriodicError(GuidingSession session, List<GuidingAnomaly> anomalies)
        {
            if (session.FftRa == null) return;
            var pePeak = session.FftRa.SignificantPeaks
                .FirstOrDefault(p => p.PeriodMinutes >= 6 && p.PeriodMinutes <= 12);
            if (pePeak != null && pePeak.AmplitudeArcsec > 2.0)
            {
                anomalies.Add(new GuidingAnomaly {
                    Type = AnomalyType.PeriodicError,
                    Severity = pePeak.AmplitudeArcsec > 5 ? AnomalySeverity.Error : AnomalySeverity.Warning,
                    Title = "Significant periodic error",
                    Description = string.Format("Periodic error detected: amplitude {0:F1} arcsec at {1:F1} min", pePeak.AmplitudeArcsec, pePeak.PeriodMinutes),
                    Recommendation = "Enable PEC correction on your mount. Record PEC with PEMPro or PHD2 PEC Trainer.",
                    MeasuredValue = pePeak.AmplitudeArcsec,
                    Unit = "arcsec peak-to-peak"
                });
            }
        }

        private void DetectPolarAlignmentError(GuidingSession session, List<GuidingAnomaly> anomalies)
        {
            if (session.Statistics == null) return;
            double decDrift = Math.Abs(session.Statistics.DecDriftArcsecPerMin);
            if (decDrift > 0.5)
            {
                string direction = session.Statistics.DecDriftArcsecPerMin > 0 ? "North" : "South";
                anomalies.Add(new GuidingAnomaly {
                    Type = AnomalyType.PolarAlignmentError,
                    Severity = decDrift > 2.0 ? AnomalySeverity.Error : AnomalySeverity.Warning,
                    Title = "DEC Drift - Probable polar alignment error",
                    Description = $"DEC drift = {decDrift:F2} arcsec/min towards {direction}",
                    Recommendation = "Use the NINA polar alignment procedure (PA Wizard) or PoleMaster. Aim for < 0.1 arcsec/min.",
                    MeasuredValue = decDrift,
                    Unit = "arcsec/min"
                });
            }
        }

        private void DetectBacklash(GuidingSession session, List<GuidingAnomaly> anomalies)
        {
            var points = session.RawPoints;
            if (points.Count < 20) return;
            int directionChanges = 0;
            double totalDelay = 0;
            for (int i = 2; i < points.Count - 1; i++)
            {
                bool corrChanged = Math.Sign(points[i].DecCorrection) != Math.Sign(points[i - 1].DecCorrection)
                                   && Math.Abs(points[i].DecCorrection) > 50;
                if (corrChanged)
                {
                    double errorAfter = Math.Abs(points[i + 1].DecError);
                    if (errorAfter > 0.5) { directionChanges++; totalDelay += errorAfter; }
                }
            }
            if (directionChanges > 3)
            {
                double avgDelay = totalDelay / directionChanges;
                anomalies.Add(new GuidingAnomaly {
                    Type = AnomalyType.Backlash,
                    Severity = avgDelay > 1.5 ? AnomalySeverity.Error : AnomalySeverity.Warning,
                    Title = "Probable mechanical backlash in DEC",
                    Description = string.Format("Play detected during DEC direction changes. Mean residual error: {0:F2} arcsec", avgDelay),
                    Recommendation = "Adjust backlash compensation in PHD2 (Brain → DEC Backlash) or tighten belts/screws.",
                    MeasuredValue = avgDelay,
                    Unit = "arcsec"
                });
            }
        }

        private void DetectAggressiveCorrections(GuidingSession session, List<GuidingAnomaly> anomalies)
        {
            if (session.Statistics == null) return;
            var points = session.RawPoints.Where(p => p.IsGuideStep).ToList();
            if (points.Count < 10) return;
            double avgError = points.Average(p => p.TotalError);
            int oscillations = 0;
            for (int i = 1; i < points.Count - 1; i++)
            {
                bool raFlip = Math.Sign(points[i].RaError) != Math.Sign(points[i - 1].RaError);
                bool decFlip = Math.Sign(points[i].DecError) != Math.Sign(points[i - 1].DecError);
                if (raFlip || decFlip) oscillations++;
            }
            double oscillationRate = (double)oscillations / points.Count;
            if (oscillationRate > 0.6 && avgError < 0.8)
            {
                anomalies.Add(new GuidingAnomaly {
                    Type = AnomalyType.AggressiveCorrection,
                    Severity = AnomalySeverity.Warning,
                    Title = "Over-aggressive corrections (hunting)",
                    Description = $"PHD2 is hunting: {oscillationRate:P0} of frames change sign. Aggressiveness too high.",
                    Recommendation = "Reduce RA/DEC aggressiveness in PHD2 (Brain → 60-70% instead of 100%). Consider the Hysteresis algorithm.",
                    MeasuredValue = oscillationRate * 100,
                    Unit = "%"
                });
            }
        }

        private void DetectPoorGuideStar(GuidingSession session, List<GuidingAnomaly> anomalies)
        {
            var points = session.RawPoints;
            if (points.Count == 0) return;
            double avgSnr = points.Average(p => p.StarSNR);
            double invalidRate = 100.0 * points.Count(p => !p.IsGuideStep) / points.Count;
            if (avgSnr < 15 || invalidRate > 5)
            {
                anomalies.Add(new GuidingAnomaly {
                    Type = AnomalyType.PoorGuideStar,
                    Severity = invalidRate > 10 ? AnomalySeverity.Error : AnomalySeverity.Warning,
                    Title = "Insufficient guide star quality",
                    Description = $"Mean SNR: {avgSnr:F0} (ideal > 25). Lost frames: {invalidRate:F1}%",
                    Recommendation = "Choose a brighter guide star (mag 8-11) with PHD2 Star Selection. Check guide scope focus.",
                    MeasuredValue = avgSnr,
                    Unit = "Mean SNR"
                });
            }
        }

        private void DetectDifferentialFlexure(GuidingSession session, List<GuidingAnomaly> anomalies)
        {
            if (session.Statistics == null) return;
            double raDrift = Math.Abs(session.Statistics.RaDriftArcsecPerMin);
            double decDrift = Math.Abs(session.Statistics.DecDriftArcsecPerMin);
            double combinedDrift = Math.Sqrt(raDrift * raDrift + decDrift * decDrift);
            if (combinedDrift > 0.3 && decDrift < 0.2)
            {
                anomalies.Add(new GuidingAnomaly {
                    Type = AnomalyType.DifferentialFlexure,
                    Severity = combinedDrift > 1.0 ? AnomalySeverity.Error : AnomalySeverity.Warning,
                    Title = "Possible differential flexure",
                    Description = $"Non-linear RA drift suggesting setup flexure: {combinedDrift:F2} arcsec/min",
                    Recommendation = "Check guide scope mounting rigidity. Use an OAG (Off-Axis Guider) to eliminate flexure.",
                    MeasuredValue = combinedDrift,
                    Unit = "arcsec/min"
                });
            }
        }
    }
}