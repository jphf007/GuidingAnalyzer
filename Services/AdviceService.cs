using System;
using System.Collections.Generic;
using System.Linq;
using GuidingAnalyzer.Models;

namespace GuidingAnalyzer.Services
{
    // ═══════════════════════════════════════════════════════════════════════════
    // ADVICE GENERATION SERVICE
    // ═══════════════════════════════════════════════════════════════════════════

    public class AdviceService
    {
        public List<GuidingAdvice> GenerateAdvice(GuidingSession session)
        {
            var advice = new List<GuidingAdvice>();
            if (session.RawPoints.Count == 0) return advice;

            var s    = session.Statistics;
            var info = session.SessionInfo;
            var cal  = session.Calibration;

            // ── 1. IMAGE SCALE / OPTICS FIT ─────────────────────────────────
            AnalyzeImageScale(session, s, info, cal, advice);

            // ── 2. POLAR ALIGNMENT ───────────────────────────────────────────
            AnalyzePolarAlignment(s, info, advice);

            // ── 3. PERIODIC ERROR / WORM ─────────────────────────────────────
            AnalyzePeriodicError(session, s, advice);

            // ── 4. DEC BACKLASH ──────────────────────────────────────────────
            AnalyzeBacklash(session, s, info, cal, advice);

            // ── 5. CALIBRATION QUALITY ───────────────────────────────────────
            AnalyzeCalibration(cal, info, advice);

            // ── 6. PHD2 ALGORITHM PARAMETERS ────────────────────────────────
            AnalyzeAlgorithmSettings(session, s, info, advice);

            // ── 7. GUIDE STAR ────────────────────────────────────────────────
            AnalyzeGuideStar(session, info, advice);

            // ── 8. DIFFERENTIAL FLEXURE ──────────────────────────────────────
            AnalyzeFlexure(s, advice);

            // ── 9. STATIONARITY / STABILITY ──────────────────────────────────
            AnalyzeStability(s, advice);

            // ── 10. ERROR DISTRIBUTION (skewness / kurtosis) ────────────────
            AnalyzeDistribution(s, advice);

            // ── 11. GLOBAL RMS vs IMAGING SCALE ─────────────────────────────
            AnalyzeRmsVsImagingScale(session, s, info, advice);

            // ── 12. MULTI-STAR / ADVANCED PARAMETERS ─────────────────────────
            AnalyzeAdvancedSettings(info, advice);

            // Sort by descending priority then category
            return advice
                .OrderByDescending(a => (int)a.Priority)
                .ThenBy(a => a.Category.ToString())
                .ToList();
        }

        // ── 1. IMAGE SCALE / OPTICS FIT ─────────────────────────────────────
        private static void AnalyzeImageScale(
            GuidingSession session, GuidingStatistics? s, GuidingSessionInfo? info,
            CalibrationSession? cal, List<GuidingAdvice> advice)
        {
            double guideScale = info?.PixelScaleArcSecPx ?? session.PixelScale;
            if (guideScale <= 0) return;

            // Ideal guide scale: 1.0 – 3.5 arcsec/px
            if (guideScale < 0.5)
                advice.Add(new GuidingAdvice {
                    Priority = AdvicePriority.High, Category = AdviceCategory.Equipment,
                    Icon = "🔬", Title = "Guide scale too fine",
                    Detail = $"Guide scope pixel scale is {guideScale:F2}\"/px. Below ~0.8\"/px PHD2 over-corrects seeing and worsens guiding.",
                    Action  = "Switch to 2× binning on the guide camera, or use a shorter focal-length guide scope.",
                    Context = $"{guideScale:F2} arcsec/px"
                });
            else if (guideScale > 4.5)
                advice.Add(new GuidingAdvice {
                    Priority = AdvicePriority.Medium, Category = AdviceCategory.Equipment,
                    Icon = "🔭", Title = "Guide scale too coarse",
                    Detail = $"Guide scope pixel scale is {guideScale:F2}\"/px. Above ~4\"/px centroid precision is insufficient for good guiding.",
                    Action  = "Use a longer focal-length guide scope, or disable binning.",
                    Context = $"{guideScale:F2} arcsec/px"
                });
        }

        // ── 2. POLAR ALIGNMENT ───────────────────────────────────────────────
        private static void AnalyzePolarAlignment(
            GuidingStatistics? s, GuidingSessionInfo? info, List<GuidingAdvice> advice)
        {
            if (s == null) return;
            double decDrift = Math.Abs(s.DecDriftArcsecPerMin);
            double raDrift  = Math.Abs(s.RaDriftArcsecPerMin);

            if (decDrift > 3.0)
                advice.Add(new GuidingAdvice {
                    Priority = AdvicePriority.Critical, Category = AdviceCategory.PolarAlignment,
                    Icon = "🧭", Title = "Major polar alignment error",
                    Detail = $"DEC drift of {decDrift:F2}\"/min is very high. Guiding constantly fights field rotation.",
                    Action  = "Redo a precise polar alignment with the NINA PA Wizard, SharpCap or PoleMaster. Aim for < 0.5\"/min.",
                    Context = $"DEC drift = {decDrift:F2}\"/min"
                });
            else if (decDrift > 1.0)
                advice.Add(new GuidingAdvice {
                    Priority = AdvicePriority.High, Category = AdviceCategory.PolarAlignment,
                    Icon = "🧭", Title = "Polar alignment needs improvement",
                    Detail = $"DEC drift of {decDrift:F2}\"/min. Above 1\"/min DEC guiding is constantly solicited.",
                    Action  = "Refine polar alignment. Below 0.5\"/min you can switch to uni-directional DEC guiding to eliminate backlash.",
                    Context = $"DEC drift = {decDrift:F2}\"/min"
                });
            else if (decDrift > 0.5)
                advice.Add(new GuidingAdvice {
                    Priority = AdvicePriority.Medium, Category = AdviceCategory.PolarAlignment,
                    Icon = "🧭", Title = "Slight DEC drift",
                    Detail = $"DEC drift of {decDrift:F2}\"/min — acceptable for planetary or short focal-length imaging, borderline for high-resolution work.",
                    Action  = "For long exposures (> 5 min) or high resolution, aim for < 0.3\"/min.",
                    Context = $"DEC drift = {decDrift:F2}\"/min"
                });

            // Strong residual RA drift (not related to periodic error)
            if (raDrift > 2.0 && decDrift < 0.5)
                advice.Add(new GuidingAdvice {
                    Priority = AdvicePriority.Medium, Category = AdviceCategory.PolarAlignment,
                    Icon = "⏱", Title = "Strong residual RA drift",
                    Detail = $"RA drift of {raDrift:F2}\"/min despite good DEC alignment. May indicate incorrect sidereal rate or wrong gear ratio.",
                    Action  = "Check the \"Guide Rate\" parameter in PHD2 and the sidereal tracking rate of your mount.",
                    Context = $"RA drift = {raDrift:F2}\"/min"
                });
        }

        // ── 3. PERIODIC ERROR ────────────────────────────────────────────────
        private static void AnalyzePeriodicError(
            GuidingSession session, GuidingStatistics? s, List<GuidingAdvice> advice)
        {
            if (s == null || session.FftRa == null) return;

            // Worm detected from advanced statistics
            if (s.WormPeriodSeconds > 0 && s.WormAmplitudeArcsec > 0)
            {
                double amp = s.WormAmplitudeArcsec;
                double periodMin = s.WormPeriodSeconds / 60.0;

                if (amp > 5.0)
                    advice.Add(new GuidingAdvice {
                        Priority = AdvicePriority.Critical, Category = AdviceCategory.PeriodicError,
                        Icon = "⚙", Title = "Very high periodic error",
                        Detail = $"Worm amplitude: {amp:F1}\" at {periodMin:F1} min. PHD2 is fighting hard against this error.",
                        Action  = "Enable PEC (Periodic Error Correction) on your mount. Record with PEMPro, PHD2 PEC Trainer, or the built-in NINA assistant. Cover several worm cycles for an accurate correction.",
                        Context = $"{amp:F1}\" peak-to-peak @ {periodMin:F1} min"
                    });
                else if (amp > 2.0)
                    advice.Add(new GuidingAdvice {
                        Priority = AdvicePriority.High, Category = AdviceCategory.PeriodicError,
                        Icon = "⚙", Title = "Significant periodic error",
                        Detail = $"Worm amplitude: {amp:F1}\" at {periodMin:F1} min. PEC would reduce the guiding load.",
                        Action  = "Consider enabling PEC. Use the \"Predictive PEC\" algorithm in PHD2 Brain if available.",
                        Context = $"{amp:F1}\" @ {periodMin:F1} min"
                    });
                else if (amp > 1.0)
                    advice.Add(new GuidingAdvice {
                        Priority = AdvicePriority.Low, Category = AdviceCategory.PeriodicError,
                        Icon = "⚙", Title = "Moderate periodic error",
                        Detail = $"Worm amplitude: {amp:F1}\" at {periodMin:F1} min — well managed by PHD2.",
                        Action  = "PEC is still beneficial if your mount supports it.",
                        Context = $"{amp:F1}\" @ {periodMin:F1} min"
                    });
            }

            // High-frequency harmonics (motor vibrations)
            var highFreqPeaks = session.FftRa.Amplitudes
                .Select((a, i) => (i, a))
                .Skip(1)
                .Where(x => x.i < session.FftRa.Frequencies.Length
                         && session.FftRa.Frequencies[x.i] > 0
                         && 3600.0 / session.FftRa.Frequencies[x.i] < 60)   // < 1 minute
                .Where(x => x.a > 0.5)
                .ToList();

            if (highFreqPeaks.Count >= 2)
                advice.Add(new GuidingAdvice {
                    Priority = AdvicePriority.Medium, Category = AdviceCategory.PeriodicError,
                    Icon = "〰", Title = "High-frequency vibrations detected",
                    Detail = "Multiple short-period harmonics (< 1 min) with significant amplitude. Likely source: motor vibrations, internal air turbulence, or a fan.",
                    Action  = "Check mechanical rigidity. Increase guide exposure time (3–5 s) to average out vibrations. Turn off nearby vibration sources (fans, HVAC).",
                    Context = $"{highFreqPeaks.Count} peaks < 60 s"
                });
        }

        // ── 4. DEC BACKLASH ──────────────────────────────────────────────────
        private static void AnalyzeBacklash(
            GuidingSession session, GuidingStatistics? s,
            GuidingSessionInfo? info, CalibrationSession? cal,
            List<GuidingAdvice> advice)
        {
            var points = session.RawPoints.Where(p => p.IsGuideStep).ToList();
            if (points.Count < 20) return;

            int dirChanges = 0;
            double totalLag = 0;
            for (int i = 2; i < points.Count - 1; i++)
            {
                bool changed = Math.Sign(points[i].DecCorrection) != Math.Sign(points[i - 1].DecCorrection)
                            && Math.Abs(points[i].DecCorrection) > 50;
                if (changed)
                {
                    double errAfter = Math.Abs(points[i + 1].DecError);
                    if (errAfter > 0.4) { dirChanges++; totalLag += errAfter; }
                }
            }

            if (dirChanges > 3)
            {
                double avgLag = totalLag / dirChanges;
                bool hasBacklashComp = info?.BacklashCompEnabled == "true" || info?.BacklashCompEnabled == "1";

                if (avgLag > 1.5)
                    advice.Add(new GuidingAdvice {
                        Priority = AdvicePriority.High, Category = AdviceCategory.Backlash,
                        Icon = "↕", Title = "Significant DEC backlash",
                        Detail = $"Mechanical play detected during DEC direction changes: mean residual error {avgLag:F2}\". PHD2 takes several frames to recover.",
                        Action  = hasBacklashComp
                            ? $"Backlash compensation is active ({info?.BacklashPulseMs} ms). Increase the value or switch to uni-directional DEC guiding. Check mechanics (belt and/or gears)."
                            : "Enable DEC backlash compensation in PHD2 Brain. Slightly detune polar alignment (< 10') for a constant DEC drift allowing uni-directional guiding.",
                        Context = $"{dirChanges} changes detected, mean lag {avgLag:F2}\""
                    });
                else
                    advice.Add(new GuidingAdvice {
                        Priority = AdvicePriority.Medium, Category = AdviceCategory.Backlash,
                        Icon = "↕", Title = "Moderate DEC backlash",
                        Detail = $"Slight play detected during DEC direction changes ({dirChanges} occurrences, lag {avgLag:F2}\").",
                        Action  = "Enable backlash compensation in PHD2 Brain if not already done. Check belt and/or gear tension.",
                        Context = $"mean lag {avgLag:F2}\""
                    });
            }

            // DEC guide mode
            if (info?.DecGuideMode == "Off")
                advice.Add(new GuidingAdvice {
                    Priority = AdvicePriority.High, Category = AdviceCategory.GuidingParameters,
                    Icon = "⛔", Title = "DEC guiding disabled",
                    Detail = "DEC guiding is OFF in PHD2. Declination errors are not corrected.",
                    Action  = "Enable DEC guiding in \"Auto\" in PHD2 Brain. Use \"North\" or \"South\" only if backlash is extreme.",
                    Context = "Dec Guide Mode = Off"
                });
        }

        // ── 5. CALIBRATION QUALITY ───────────────────────────────────────────
        private static void AnalyzeCalibration(
            CalibrationSession? cal, GuidingSessionInfo? info, List<GuidingAdvice> advice)
        {
            if (cal == null) return;

            // RA/DEC orthogonality (ideal < 5°, problematic > 10°)
            double orthoErr = info?.OrthoErrorDeg ?? 0;
            if (orthoErr > 10)
                advice.Add(new GuidingAdvice {
                    Priority = AdvicePriority.High, Category = AdviceCategory.Calibration,
                    Icon = "📐", Title = "High calibration orthogonality error",
                    Detail = $"The RA/DEC angle measured during calibration is {orthoErr:F1}° off perpendicularity. Above 10°, PHD2 cannot correct both axes efficiently.",
                    Action  = "Recalibrate near the celestial equator (Dec ≈ 0°) within ±1h of the meridian. Enable \"Assume Dec orthogonal to RA\" if hardware is correct but backlash is high.",
                    Context = $"Ortho error = {orthoErr:F1}°"
                });
            else if (orthoErr > 5)
                advice.Add(new GuidingAdvice {
                    Priority = AdvicePriority.Low, Category = AdviceCategory.Calibration,
                    Icon = "📐", Title = "Slight orthogonality error",
                    Detail = $"RA/DEC angle slightly off perpendicular ({orthoErr:F1}°). PHD2 handles this, but recalibration would improve precision.",
                    Action  = "Recalibrate if you observe correlation between RA and DEC errors.",
                    Context = $"Ortho error = {orthoErr:F1}°"
                });

            // Calibration position (ideal: Dec ≈ 0, ±1h meridian)
            double calDec = cal.DecDeg;
            double calHa  = cal.HourAngle;
            if (Math.Abs(calDec) > 30)
                advice.Add(new GuidingAdvice {
                    Priority = AdvicePriority.Medium, Category = AdviceCategory.Calibration,
                    Icon = "📍", Title = "Calibration done at high declination",
                    Detail = $"Calibration was performed at Dec = {calDec:F1}°. The cosine compression in RA reduces the accuracy of rate measurements.",
                    Action  = "Use the PHD2 calibration assistant and calibrate between Dec −20° and +20°, near the meridian.",
                    Context = $"Cal Dec = {calDec:F1}°"
                });

            if (Math.Abs(calHa) > 2.0 && Math.Abs(calHa) < 22)  // avoid 24h wrap
                advice.Add(new GuidingAdvice {
                    Priority = AdvicePriority.Low, Category = AdviceCategory.Calibration,
                    Icon = "📍", Title = "Calibration far from meridian",
                    Detail = $"Calibration was performed at hour angle {calHa:F1}h. Ideally calibrate within 2h of the meridian to minimise measurement errors.",
                    Action  = "Use the PHD2 Calibration Assistant to automatically choose the optimal position.",
                    Context = $"HA = {calHa:F1}h"
                });

            // Insufficient number of steps
            int raSteps  = cal.RaWestPoints.Count;
            int decSteps = cal.DecNorthPoints.Count;
            if (raSteps < 8 || decSteps < 8)
                advice.Add(new GuidingAdvice {
                    Priority = AdvicePriority.Medium, Category = AdviceCategory.Calibration,
                    Icon = "🔢", Title = "Insufficient calibration steps",
                    Detail = $"RA: {raSteps} steps, DEC: {decSteps} steps. PHD2 recommends ≥ 8 steps for reliable rate measurement.",
                    Action  = "Reduce the \"Calibration Step\" in PHD2 Brain to get more steps (aim for 12–20).",
                    Context = $"RA {raSteps} steps, DEC {decSteps} steps"
                });

            // Guide speed too low
            double raRate = cal.RaGuideSpeedArcsecS;
            if (raRate > 0 && raRate < 5.0)
                advice.Add(new GuidingAdvice {
                    Priority = AdvicePriority.Low, Category = AdviceCategory.Calibration,
                    Icon = "🐢", Title = "Low guide speed",
                    Detail = $"RA guide speed = {raRate:F1}\"/s (< 7.5\"/s for 0.5× sidereal). Too low a speed slows backlash and perturbation recovery.",
                    Action  = "Set guide speed between 0.5× and 1× sidereal in the mount controller (≈ 7.5–15\"/s).",
                    Context = $"RA guide rate = {raRate:F1}\"/s"
                });
        }

        // ── 6. PHD2 ALGORITHM PARAMETERS ────────────────────────────────────
        private static void AnalyzeAlgorithmSettings(
            GuidingSession session, GuidingStatistics? s,
            GuidingSessionInfo? info, List<GuidingAdvice> advice)
        {
            if (info == null || s == null) return;

            // DEC aggressiveness too high with oscillating guiding
            var points = session.RawPoints.Where(p => p.IsGuideStep).ToList();
            if (points.Count > 10)
            {
                int oscillations = 0;
                for (int i = 1; i < points.Count; i++)
                    if (Math.Sign(points[i].DecError) != Math.Sign(points[i - 1].DecError)) oscillations++;
                double oscRate = (double)oscillations / points.Count;

                if (oscRate > 0.55 && info.DecAggression > 80)
                    advice.Add(new GuidingAdvice {
                        Priority = AdvicePriority.Medium, Category = AdviceCategory.GuidingParameters,
                        Icon = "🎛", Title = "DEC aggressiveness too high — hunting",
                        Detail = $"DEC aggressiveness = {info.DecAggression:F0}% and {oscRate:P0} of frames oscillate. PHD2 is over-correcting and causing oscillations.",
                        Action  = "Reduce DEC aggressiveness to 60–70% in PHD2 Brain. Enable the \"Resist Switch\" algorithm for DEC.",
                        Context = $"Aggression {info.DecAggression:F0}%, oscillation {oscRate:P0}"
                    });

                // Min move too low (chasing seeing)
                double avgSnr = points.Average(p => p.StarSNR);
                if (info.MinMotionRA < 0.05 && s.RaRms < 0.5 && avgSnr > 20)
                    advice.Add(new GuidingAdvice {
                        Priority = AdvicePriority.Low, Category = AdviceCategory.GuidingParameters,
                        Icon = "🎛", Title = "Min Move RA very low — risk of chasing seeing",
                        Detail = $"Min Move RA = {info.MinMotionRA:F3}\" while your RA RMS = {s.RaRms:F2}\". Too frequent corrections can amplify seeing.",
                        Action  = "Increase Min Move RA to 0.1–0.15\" to filter out micro-movements caused by seeing.",
                        Context = $"MinMove RA = {info.MinMotionRA:F3}\""
                    });
            }

            // RA algorithm — Hysteresis vs Predictive PEC
            if (info.GuideAlgoRA == "Hysteresis" && s.WormAmplitudeArcsec > 2.0)
                advice.Add(new GuidingAdvice {
                    Priority = AdvicePriority.Low, Category = AdviceCategory.GuidingParameters,
                    Icon = "💡", Title = "RA algorithm — Predictive PEC recommended",
                    Detail = $"You are using Hysteresis with a periodic error of {s.WormAmplitudeArcsec:F1}\". Predictive PEC can reduce this error by 50–80% without hardware PEC.",
                    Action  = "In PHD2 Brain → RA, select \"Predictive PEC\" and let it learn over 3+ worm cycles.",
                    Context = $"Algo = {info.GuideAlgoRA}, PE = {s.WormAmplitudeArcsec:F1}\""
                });

            // Guide exposure time
            if (info.ExposureMs < 1000)
                advice.Add(new GuidingAdvice {
                    Priority = AdvicePriority.Low, Category = AdviceCategory.GuidingParameters,
                    Icon = "⏱", Title = "Short guide exposure",
                    Detail = $"Guide exposure = {info.ExposureMs} ms. Below 1 s PHD2 reacts strongly to seeing, which can degrade guiding.",
                    Action  = "Increase exposure to 2–5 s to smooth atmospheric turbulence. Ideal with good star SNR.",
                    Context = $"Exposure = {info.ExposureMs} ms"
                });
            else if (info.ExposureMs > 6000 && s.RaRms > 0.8)
                advice.Add(new GuidingAdvice {
                    Priority = AdvicePriority.Low, Category = AdviceCategory.GuidingParameters,
                    Icon = "⏱", Title = "Long guide exposure with high RMS",
                    Detail = $"Guide exposure = {info.ExposureMs / 1000.0:F0} s and RMS = {s.RaRms:F2}\". Too long an exposure delays corrections.",
                    Action  = "Try reducing to 3–4 s for more reactive corrections.",
                    Context = $"Exposure = {info.ExposureMs} ms, RMS = {s.RaRms:F2}\""
                });

            // Multi-star guiding
            if (!info.MultiStarMode)
                advice.Add(new GuidingAdvice {
                    Priority = AdvicePriority.Info, Category = AdviceCategory.GuideStar,
                    Icon = "⭐", Title = "Multi-star mode not enabled",
                    Detail = "PHD2 is guiding on a single star, making it sensitive to cloud passages, scintillation and unresolved double stars.",
                    Action  = "Enable multi-star guiding in PHD2 → Guiding tab. PHD2 will be more robust and more accurate.",
                    Context = "Multi-star = Off"
                });
        }

        // ── 7. GUIDE STAR QUALITY ────────────────────────────────────────────
        private static void AnalyzeGuideStar(
            GuidingSession session, GuidingSessionInfo? info, List<GuidingAdvice> advice)
        {
            var points = session.RawPoints;
            if (points.Count == 0) return;

            double avgSnr = points.Average(p => p.StarSNR);
            double lostRate = 100.0 * points.Count(p => !p.IsGuideStep) / points.Count;

            if (lostRate > 10)
                advice.Add(new GuidingAdvice {
                    Priority = AdvicePriority.High, Category = AdviceCategory.GuideStar,
                    Icon = "💫", Title = "Frequent guide star loss",
                    Detail = $"{lostRate:F1}% of frames are invalid (star lost). Causes: clouds, saturation, poor focus, insufficient SNR.",
                    Action  = "Check guide scope focus. Choose a star mag 8–11 with SNR > 25. Disable star mass detection if too strict.",
                    Context = $"{lostRate:F1}% frames lost"
                });
            else if (lostRate > 3)
                advice.Add(new GuidingAdvice {
                    Priority = AdvicePriority.Medium, Category = AdviceCategory.GuideStar,
                    Icon = "💫", Title = "Occasional guide star loss",
                    Detail = $"{lostRate:F1}% invalid frames. May be caused by dithers, light clouds or strong scintillation.",
                    Action  = "Monitor SNR. If < 15, choose a brighter star or reduce exposure.",
                    Context = $"{lostRate:F1}% frames lost"
                });

            if (avgSnr < 10)
                advice.Add(new GuidingAdvice {
                    Priority = AdvicePriority.High, Category = AdviceCategory.GuideStar,
                    Icon = "🔆", Title = "Guide star SNR too low",
                    Detail = $"Mean SNR = {avgSnr:F0}. Below 10, centroid measurement is imprecise and introduces noise into guiding.",
                    Action  = "Choose a brighter star. Increase gain or guide exposure. Check guide scope focus.",
                    Context = $"Mean SNR = {avgSnr:F0}"
                });
            else if (avgSnr < 20)
                advice.Add(new GuidingAdvice {
                    Priority = AdvicePriority.Low, Category = AdviceCategory.GuideStar,
                    Icon = "🔆", Title = "Borderline guide star SNR",
                    Detail = $"Mean SNR = {avgSnr:F0} (ideal > 25). Centroid precision is acceptable but may degrade in poor seeing.",
                    Action  = "Try a slightly brighter star or increase gain slightly.",
                    Context = $"Mean SNR = {avgSnr:F0}"
                });

            // Guide star HFD
            if (info?.StarHfd > 6)
                advice.Add(new GuidingAdvice {
                    Priority = AdvicePriority.Medium, Category = AdviceCategory.GuideStar,
                    Icon = "🔭", Title = "High guide star HFD — focus needs attention",
                    Detail = $"Guide star HFD is {info.StarHfd:F1} px. A high HFD means a defocused star, which reduces centroid precision.",
                    Action  = "Refine guide scope focus. Aim for an HFD of 2–4 px with a sharp profile.",
                    Context = $"HFD = {info.StarHfd:F1} px"
                });
        }

        // ── 8. DIFFERENTIAL FLEXURE ──────────────────────────────────────────
        private static void AnalyzeFlexure(GuidingStatistics? s, List<GuidingAdvice> advice)
        {
            if (s == null) return;

            double corr = Math.Abs(s.RaDecCorrelation);
            if (corr > 0.7)
                advice.Add(new GuidingAdvice {
                    Priority = AdvicePriority.High, Category = AdviceCategory.Flexure,
                    Icon = "🔗", Title = "Strong RA/DEC correlation — likely flexure",
                    Detail = $"Pearson correlation RA/DEC = {s.RaDecCorrelation:+0.00;-0.00}. A correlation > 0.7 indicates both axes move together, a typical sign of differential flexure.",
                    Action  = "Check rigidity of the focuser drawtube and guide scope rings. An OAG (Off-Axis Guider) eliminates differential flexure.",
                    Context = $"Pearson r = {s.RaDecCorrelation:+0.00;-0.00}"
                });
            else if (corr > 0.5)
                advice.Add(new GuidingAdvice {
                    Priority = AdvicePriority.Medium, Category = AdviceCategory.Flexure,
                    Icon = "🔗", Title = "Moderate RA/DEC correlation",
                    Detail = $"RA/DEC correlation = {s.RaDecCorrelation:+0.00;-0.00}. Slight covariance — possible minor mechanical flexure.",
                    Action  = "Check guide scope mounting rigidity. Tighten all fixing screws.",
                    Context = $"Pearson r = {s.RaDecCorrelation:+0.00;-0.00}"
                });
        }

        // ── 9. STATIONARITY ──────────────────────────────────────────────────
        private static void AnalyzeStability(GuidingStatistics? s, List<GuidingAdvice> advice)
        {
            if (s == null) return;

            double raRatio  = s.RaStabilityRatio;
            double decRatio = s.DecStabilityRatio;

            if (raRatio > 2.5 || decRatio > 2.5)
                advice.Add(new GuidingAdvice {
                    Priority = AdvicePriority.High, Category = AdviceCategory.Stability,
                    Icon = "📈", Title = "Strong guiding degradation during session",
                    Detail = $"2nd-half variance is {Math.Max(raRatio, decRatio):F1}× that of the 1st half. Guiding degrades significantly over time.",
                    Action  = "Frequent causes: mount heating/cooling, cables binding during rotation, guide focus drift, dew. Check your cable routing at the end of a session.",
                    Context = $"Ratio RA {raRatio:F1}×, DEC {decRatio:F1}×"
                });
            else if (raRatio > 1.5 || decRatio > 1.5)
                advice.Add(new GuidingAdvice {
                    Priority = AdvicePriority.Medium, Category = AdviceCategory.Stability,
                    Icon = "📈", Title = "Slight degradation during session",
                    Detail = $"2nd half / 1st half variance: RA {raRatio:F1}×, DEC {decRatio:F1}×. Guiding is less stable in the second part of the session.",
                    Action  = "Check cable management. A cable gradually becoming taut is often responsible.",
                    Context = $"Ratio RA {raRatio:F1}×, DEC {decRatio:F1}×"
                });
            else if (raRatio < 0.5 || decRatio < 0.5)
                advice.Add(new GuidingAdvice {
                    Priority = AdvicePriority.Info, Category = AdviceCategory.Stability,
                    Icon = "📉", Title = "Guiding improving during session",
                    Detail = "Variance decreases as the session progresses — the setup is stabilising (thermal equilibration, cables finding their position).",
                    Action  = "Let the setup thermally condition for 30–60 min before starting imaging.",
                    Context = $"Ratio RA {raRatio:F1}×, DEC {decRatio:F1}×"
                });
        }

        // ── 10. DISTRIBUTION ─────────────────────────────────────────────────
        private static void AnalyzeDistribution(GuidingStatistics? s, List<GuidingAdvice> advice)
        {
            if (s == null) return;

            if (s.RaKurtosis > 3.0 || s.DecKurtosis > 3.0)
            {
                string axis = s.RaKurtosis > s.DecKurtosis ? "RA" : "DEC";
                double k    = Math.Max(s.RaKurtosis, s.DecKurtosis);
                advice.Add(new GuidingAdvice {
                    Priority = AdvicePriority.Medium, Category = AdviceCategory.Statistics,
                    Icon = "📊", Title = $"{axis} distribution — heavy tails (high kurtosis)",
                    Detail = $"Kurtosis {axis} = {k:F1} (normal = 0). Rare but intense peaks skew the statistics. Likely cause: cloud passages, punctual mechanical disturbances, or vibrations.",
                    Action  = "Check the anomalies listed in this tab to identify problematic frames. Use sigma rejection in image stacking.",
                    Context = $"Kurtosis {axis} = {k:F1}"
                });
            }

            if (Math.Abs(s.RaSkewness) > 1.5)
                advice.Add(new GuidingAdvice {
                    Priority = AdvicePriority.Low, Category = AdviceCategory.Statistics,
                    Icon = "📊", Title = "Asymmetric RA distribution",
                    Detail = $"RA skewness = {s.RaSkewness:+0.00;-0.00}. PHD2 corrects better in one direction than the other — may indicate asymmetric RA backlash or correction saturation.",
                    Action  = "Check the Max RA Duration value in PHD2 Brain. A value that is too low may limit corrections in one direction.",
                    Context = $"Skewness RA = {s.RaSkewness:+0.00;-0.00}"
                });
        }

        // ── 11. RMS vs IMAGING SCALE ─────────────────────────────────────────
        private static void AnalyzeRmsVsImagingScale(
            GuidingSession session, GuidingStatistics? s,
            GuidingSessionInfo? info, List<GuidingAdvice> advice)
        {
            if (s == null) return;

            double rmsTotal = s.TotalRms;

            if (rmsTotal < 0.3)
                advice.Add(new GuidingAdvice {
                    Priority = AdvicePriority.Info, Category = AdviceCategory.Statistics,
                    Icon = "✅", Title = "Excellent guiding",
                    Detail = $"Total RMS = {rmsTotal:F2}\" — professional-grade guiding. At this precision, atmospheric seeing is the limiting factor.",
                    Action  = "Focus on focus quality and selection of nights with good seeing.",
                    Context = $"Total RMS = {rmsTotal:F2}\""
                });
            else if (rmsTotal < 0.6)
                advice.Add(new GuidingAdvice {
                    Priority = AdvicePriority.Info, Category = AdviceCategory.Statistics,
                    Icon = "✅", Title = "Good guiding",
                    Detail = $"Total RMS = {rmsTotal:F2}\" — good performance for most amateur setups.",
                    Action  = "To go further, follow the advice in this tab in order of priority.",
                    Context = $"Total RMS = {rmsTotal:F2}\""
                });
            else if (rmsTotal > 1.5)
                advice.Add(new GuidingAdvice {
                    Priority = AdvicePriority.High, Category = AdviceCategory.Statistics,
                    Icon = "⚠", Title = "High RMS — stars likely elongated",
                    Detail = $"Total RMS = {rmsTotal:F2}\". For most setups, RMS > 1.5\" causes visible star elongation.",
                    Action  = "Address problems in priority order in this tab. Start with polar alignment and calibration.",
                    Context = $"Total RMS = {rmsTotal:F2}\""
                });
        }

        // ── 12. ADVANCED PARAMETERS ──────────────────────────────────────────
        private static void AnalyzeAdvancedSettings(
            GuidingSessionInfo? info, List<GuidingAdvice> advice)
        {
            if (info == null) return;

            // No dark / defect map
            if (!info.CameraHasDark && !info.CameraHasDefectMap)
                advice.Add(new GuidingAdvice {
                    Priority = AdvicePriority.Low, Category = AdviceCategory.GuideStar,
                    Icon = "🌑", Title = "No dark library or defect map",
                    Detail = "PHD2 is guiding without a dark library or bad-pixel map. Hot pixels can bias centroid measurement.",
                    Action  = "Create a dark library in PHD2 (Tools → Dark Library) or a bad-pixel map to improve robustness.",
                    Context = "Dark = Off, BPM = Off"
                });

            // No backlash compensation but bi-directional guiding
            if (!string.Equals(info.BacklashCompEnabled, "enabled", StringComparison.OrdinalIgnoreCase)
                && info.DecGuideMode == "Auto")
                advice.Add(new GuidingAdvice {
                    Priority = AdvicePriority.Info, Category = AdviceCategory.Backlash,
                    Icon = "↕", Title = "Backlash compensation not enabled",
                    Detail = "DEC guiding is bi-directional but backlash compensation is disabled. If your mount has notable DEC play, some corrections will be lost.",
                    Action  = "If you observe DEC jumps on direction changes, enable \"DEC Backlash Compensation\" in PHD2 Brain.",
                    Context = "BacklashComp = Off, DecMode = Auto"
                });
        }
    }
}
