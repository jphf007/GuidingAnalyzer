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
                    Unit = "arcsec amplitude"
                });
            }
        }

        private void DetectPolarAlignmentError(GuidingSession session, List<GuidingAnomaly> anomalies)
        {
            if (session.Statistics == null) return;
            double decDrift = Math.Abs(session.Statistics.DecDriftArcsecPerMin);
            if (decDrift > 0.5)
            {
                anomalies.Add(new GuidingAnomaly {
                    Type = AnomalyType.PolarAlignmentError,
                    Severity = decDrift > 2.0 ? AnomalySeverity.Error : AnomalySeverity.Warning,
                    Title = "DEC Drift - Probable polar alignment error",
                    Description = $"Estimated unguided DEC drift = {decDrift:F2} arcsec/min. Polar error, flexure and backlash can all contribute.",
                    Recommendation = "Use the NINA polar alignment procedure (PA Wizard) or PoleMaster. Aim for < 0.1 arcsec/min.",
                    MeasuredValue = decDrift,
                    Unit = "arcsec/min"
                });
            }
        }

        private void DetectBacklash(GuidingSession session, List<GuidingAnomaly> anomalies)
        {
            if (session.RawPoints.Count(GuidingDataQuality.IsUsable) < 20) return;
            var (directionChanges, avgDelay) = GuidingDataQuality.DecReversals(session.RawPoints, .5);
            if (directionChanges > 3)
            {
                anomalies.Add(new GuidingAnomaly {
                    Type = AnomalyType.Backlash,
                    Severity = avgDelay > 1.5 ? AnomalySeverity.Error : AnomalySeverity.Warning,
                    Title = "Possible DEC backlash — reversal residuals",
                    Description = string.Format("Residuals after {0} DEC pulse reversals: mean {1:F2} arcsec. This is not a backlash measurement.", directionChanges, avgDelay),
                    Recommendation = "Inspect these reversals and confirm with the PHD2 Guiding Assistant backlash test before changing compensation.",
                    MeasuredValue = avgDelay,
                    Unit = "arcsec"
                });
            }
        }

        private void DetectAggressiveCorrections(GuidingSession session, List<GuidingAnomaly> anomalies)
        {
            if (session.Statistics == null) return;
            var points = session.RawPoints.Where(GuidingDataQuality.IsUsable).ToList();
            if (points.Count < 10) return;
            double avgError = points.Average(p => p.TotalError);
            int oscillations = 0, correctionTransitions = 0;
            foreach (var segment in GuidingDataQuality.Segments(session.RawPoints))
            for (int i = 1; i < segment.Count; i++)
            {
                var previous = segment[i - 1]; var current = segment[i];
                bool raPulse = double.IsFinite(previous.RaCorrection) && previous.RaCorrection != 0;
                bool decPulse = double.IsFinite(previous.DecCorrection) && previous.DecCorrection != 0;
                if (!raPulse && !decPulse) continue;
                correctionTransitions++;
                bool raFlip = raPulse && Math.Sign(current.RaError) != Math.Sign(previous.RaError);
                bool decFlip = decPulse && Math.Sign(current.DecError) != Math.Sign(previous.DecError);
                if (raFlip || decFlip) oscillations++;
            }
            double oscillationRate = correctionTransitions > 0 ? (double)oscillations / correctionTransitions : 0;
            if (correctionTransitions >= 10 && oscillationRate > 0.6 && avgError < 0.8)
            {
                anomalies.Add(new GuidingAnomaly {
                    Type = AnomalyType.AggressiveCorrection,
                    Severity = AnomalySeverity.Warning,
                    Title = "Possible over-correction",
                    Description = $"{oscillationRate:P0} of corrected frame transitions change error sign. Noise can also contribute.",
                    Recommendation = "Review the pulse/response sequence and compare a small aggressiveness adjustment under similar conditions.",
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
                    Title = "RA drift with limited DEC drift",
                    Description = $"Estimated combined drift: {combinedDrift:F2} arcsec/min. A guide log alone cannot establish differential flexure.",
                    Recommendation = "Check tracking rate and compare guide-star motion with the main-camera images to investigate the cause.",
                    MeasuredValue = combinedDrift,
                    Unit = "arcsec/min"
                });
            }
        }
    }
}
