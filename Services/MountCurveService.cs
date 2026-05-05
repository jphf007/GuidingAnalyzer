using System;
using System.Collections.Generic;
using System.Linq;
using GuidingAnalyzer.Models;

namespace GuidingAnalyzer.Services
{
    /// <summary>
    /// Reconstruction of the real behavioural curve of the mount.
    /// PRINCIPLE: Position[n] = Position[n-1] + Error[n] + Correction[n-1]
    /// Guide rate is read directly from the PHD2 log (RA Guide Speed).
    /// </summary>
    public class MountCurveService
    {
        // Reference sidereal rate = 15.04 arcsec/s = 0.01504 arcsec/ms
        // Used ONLY as fallback if the log does not contain RA Guide Speed
        private const double SIDEREAL_ARCSEC_PER_MS    = 0.01504;
        private const double DEFAULT_GUIDE_RATE_FACTOR  = 0.5;
        private const double FALLBACK_RATE_ARCSEC_PER_MS = SIDEREAL_ARCSEC_PER_MS * DEFAULT_GUIDE_RATE_FACTOR;

        /// <param name="guideRateArcsecPerMs">
        /// RA guide rate in arcsec/ms, read from PHD2 (RA Guide Speed / 1000).
        /// E.g. PHD2 "7.5 a-s/s" → 0.0075 arcsec/ms.
        /// If 0, falls back to 0.5× sidereal.
        /// </param>
        /// <param name="decGuideRateArcsecPerMs">
        /// DEC guide rate in arcsec/ms, read from PHD2 (Dec Guide Speed / 1000).
        /// If 0, uses the same value as RA.
        /// </param>
        public List<MountCurvePoint> ReconstructMountCurve(
            List<GuidingPoint> points, double pixelScale,
            double guideRateArcsecPerMs    = 0,
            double decGuideRateArcsecPerMs = 0)
        {
            var curve = new List<MountCurvePoint>(points.Count);
            if (points.Count == 0) return curve;

            // Use rate from PHD2 log; fall back if not parsed
            double raRate  = guideRateArcsecPerMs > 0
                ? guideRateArcsecPerMs
                : FALLBACK_RATE_ARCSEC_PER_MS;
            double decRate = decGuideRateArcsecPerMs > 0
                ? decGuideRateArcsecPerMs
                : raRate;

            double raCumulative = 0.0, decCumulative = 0.0;
            double prevRaCorrArcsec = 0.0, prevDecCorrArcsec = 0.0;

            foreach (var p in points)
            {
                double raCorrArcsec  = p.RaCorrection  * raRate;
                double decCorrArcsec = p.DecCorrection * decRate;

                if (p.IsDitherOrSettling)
                {
                    // During a dither/settling: integrate NEITHER the error NOR the correction
                    // into the cumulative. The mount makes a deliberate jump, not a mechanical defect.
                    // We "freeze" the position and do not propagate the correction from this step.
                    curve.Add(new MountCurvePoint {
                        FrameIndex          = p.FrameIndex,
                        Timestamp           = p.Timestamp,
                        RaMountPosition     = raCumulative,
                        DecMountPosition    = decCumulative,
                        IsDitherOrSettling  = true,
                        RaRawError          = p.RaError,
                        DecRawError         = p.DecError,
                        RaCorrectionArcsec  = raCorrArcsec,
                        DecCorrectionArcsec = decCorrArcsec
                    });
                    // Do not propagate prevRaCorr/prevDecCorr either
                    prevRaCorrArcsec  = 0.0;
                    prevDecCorrArcsec = 0.0;
                }
                else
                {
                    raCumulative  += p.RaError  + prevRaCorrArcsec;
                    decCumulative += p.DecError + prevDecCorrArcsec;
                    curve.Add(new MountCurvePoint {
                        FrameIndex          = p.FrameIndex,
                        Timestamp           = p.Timestamp,
                        RaMountPosition     = raCumulative,
                        DecMountPosition    = decCumulative,
                        IsDitherOrSettling  = false,
                        RaRawError          = p.RaError,
                        DecRawError         = p.DecError,
                        RaCorrectionArcsec  = raCorrArcsec,
                        DecCorrectionArcsec = decCorrArcsec
                    });
                    prevRaCorrArcsec  = raCorrArcsec;
                    prevDecCorrArcsec = decCorrArcsec;
                }
            }
            CenterCurve(curve);
            return curve;
        }

        private void CenterCurve(List<MountCurvePoint> curve)
        {
            if (curve.Count == 0) return;
            double raMean  = curve.Average(p => p.RaMountPosition);
            double decMean = curve.Average(p => p.DecMountPosition);
            foreach (var p in curve)
            {
                p.RaMountPosition  -= raMean;
                p.DecMountPosition -= decMean;
            }
        }

        /// <summary>RA signal of the mount curve — dithers excluded (for "Drift-corrected" FFT).</summary>
        public double[] GetRaSignal(List<MountCurvePoint> curve)
            => curve.Where(p => !p.IsDitherOrSettling).Select(p => p.RaMountPosition).ToArray();

        /// <summary>DEC signal of the mount curve — dithers excluded.</summary>
        public double[] GetDecSignal(List<MountCurvePoint> curve)
            => curve.Where(p => !p.IsDitherOrSettling).Select(p => p.DecMountPosition).ToArray();

        /// <summary>
        /// RA signal without applied corrections = raw mount behaviour.
        /// Cumulative reconstruction ignoring PHD2 corrections.
        /// Equivalent to the "RA Corrections Removed" mode in PHD2 Log Viewer.
        /// </summary>
        public double[] GetRaUncorrectedSignal(List<GuidingPoint> rawPoints)
        {
            var clean = rawPoints.Where(p => !p.IsDitherOrSettling && p.IsGuideStep).ToList();
            if (clean.Count == 0) return Array.Empty<double>();
            var result = new double[clean.Count];
            double cumul = 0;
            for (int i = 0; i < clean.Count; i++)
            {
                cumul += clean[i].RaError;   // accumulate error only, no correction
                result[i] = cumul;
            }
            // Centre
            double mean = result.Average();
            for (int i = 0; i < result.Length; i++) result[i] -= mean;
            return result;
        }

        /// <summary>DEC signal without corrections.</summary>
        public double[] GetDecUncorrectedSignal(List<GuidingPoint> rawPoints)
        {
            var clean = rawPoints.Where(p => !p.IsDitherOrSettling && p.IsGuideStep).ToList();
            if (clean.Count == 0) return Array.Empty<double>();
            var result = new double[clean.Count];
            double cumul = 0;
            for (int i = 0; i < clean.Count; i++)
            {
                cumul += clean[i].DecError;
                result[i] = cumul;
            }
            double mean = result.Average();
            for (int i = 0; i < result.Length; i++) result[i] -= mean;
            return result;
        }
    }
}
