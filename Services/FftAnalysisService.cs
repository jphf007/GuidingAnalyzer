// Services/FftAnalysisService.cs
// FFT analysis of the mount curve to identify error frequencies.
// Uses MathNet.Numerics for the Fast Fourier Transform.
//
// FFT PRINCIPLE:
// A periodic signal can be decomposed into a sum of sinusoids.
// The FFT returns, for each frequency, the amplitude of the sinusoidal component.
// A peak = a mechanical error at that frequency.
//
// PEAK INTERPRETATION:
//   Period ~8–10 min  → Periodic error (worm gear)
//   Period ~1–2 min   → PE harmonic or second gear
//   Period < 30 sec   → Mechanical vibrations
//   Very low freq     → Differential flexure or atmospheric drift

using System;
using System.Collections.Generic;
using System.Linq;
using GuidingAnalyzer.Models;
using MathNet.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace GuidingAnalyzer.Services
{
    public class FftAnalysisService
    {
        // Detection threshold for significant peaks (% of max amplitude)
        private const double PEAK_THRESHOLD_PERCENT = 0.15;

        // Characteristic mount periods (minutes) for interpretation
        private static readonly (double MinPeriod, double MaxPeriod, string Label)[] KNOWN_PERIODS =
        {
            (6.0, 12.0, "Periodic error (main worm)"),
            (2.0, 6.0,  "PE harmonic or second gear"),
            (0.5, 2.0,  "High-frequency mechanical vibration"),
            (12.0, 60.0,"Differential flexure or slow drift"),
        };

        /// <summary>
        /// Computes the FFT of the mount curve signal.
        /// </summary>
        /// <param name="signal">Signal in arcsec (array of doubles)</param>
        /// <param name="samplingPeriodSeconds">Interval between two points in seconds</param>
        /// <param name="axis">Analysed axis (RA or Dec)</param>
        public FftResult ComputeFft(double[] signal, double samplingPeriodSeconds, GuidingAxis axis)
        {
            if (signal.Length == 0)
                return new FftResult();

            var result = new FftResult
            {
                Axis                = axis,
                SamplingPeriodSeconds = samplingPeriodSeconds,
                PointCount          = signal.Length
            };

            if (signal.Length < 16)
            {
                // Not enough points for a meaningful FFT
                return result;
            }

            // 1. Linear detrending: removes slow drift from the signal
            //    to avoid spectral leakage
            double[] detrended = RemoveLinearTrend(signal);

            // 2. Apply Hann window
            //    Reduces edge artefacts (spectral leakage)
            double[] windowed = ApplyHannWindow(detrended);

            // 3. Zero-pad to next power of two
            //    FFT is optimal for sizes N = 2^k
            int fftSize = NextPowerOfTwo(windowed.Length);
            var complexSignal = new System.Numerics.Complex[fftSize];
            for (int i = 0; i < windowed.Length; i++)
                complexSignal[i] = new System.Numerics.Complex(windowed[i], 0);
            // Remaining elements are already zero (zero-padding)

            // 4. Compute FFT via MathNet.Numerics
            Fourier.Forward(complexSignal, FourierOptions.Matlab);

            // 5. Compute amplitudes (modulus of complex number)
            //    Keep only the positive half of the spectrum (symmetry)
            int halfSize = fftSize / 2;
            var amplitudes  = new double[halfSize];
            var frequencies = new double[halfSize];

            // Nyquist frequency = 1 / (2 × Te)
            // Frequency resolution = 1 / (N × Te)
            double frequencyResolution = 1.0 / (fftSize * samplingPeriodSeconds);

            for (int i = 0; i < halfSize; i++)
            {
                // Amplitude = modulus / N × 2 (factor 2 for symmetry, except DC)
                amplitudes[i]  = complexSignal[i].Magnitude / fftSize * (i == 0 ? 1 : 2);
                // Frequency in cycles/hour
                frequencies[i] = i * frequencyResolution * 3600.0;
            }

            result.Frequencies = frequencies;
            result.Amplitudes  = amplitudes;

            // 6. Automatic detection of significant peaks
            result.SignificantPeaks = DetectPeaks(frequencies, amplitudes);

            return result;
        }

        // Removes the linear trend from the signal (detrending)
        private double[] RemoveLinearTrend(double[] signal)
        {
            int n = signal.Length;
            // Simple linear regression: y = ax + b
            double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
            for (int i = 0; i < n; i++)
            {
                sumX += i; sumY += signal[i];
                sumXY += i * signal[i]; sumX2 += i * i;
            }
            double a = (n * sumXY - sumX * sumY) / (n * sumX2 - sumX * sumX);
            double b = (sumY - a * sumX) / n;

            var result = new double[n];
            for (int i = 0; i < n; i++)
                result[i] = signal[i] - (a * i + b);
            return result;
        }

        // Hann window: w[n] = 0.5 × (1 − cos(2π·n/(N−1)))
        private double[] ApplyHannWindow(double[] signal)
        {
            int n = signal.Length;
            var result = new double[n];
            for (int i = 0; i < n; i++)
                result[i] = signal[i] * 0.5 * (1 - Math.Cos(2 * Math.PI * i / (n - 1)));
            return result;
        }

        private int NextPowerOfTwo(int n)
        {
            int p = 1;
            while (p < n) p <<= 1;
            return p;
        }

        // Detects significant peaks in the FFT spectrum
        private List<FftPeak> DetectPeaks(double[] frequencies, double[] amplitudes)
        {
            var peaks = new List<FftPeak>();
            if (amplitudes.Length < 3) return peaks;

            double maxAmplitude = amplitudes.Max();
            double threshold    = maxAmplitude * PEAK_THRESHOLD_PERCENT;

            for (int i = 1; i < amplitudes.Length - 1; i++)
            {
                // A peak = local maximum above the threshold
                if (amplitudes[i] > threshold &&
                    amplitudes[i] > amplitudes[i - 1] &&
                    amplitudes[i] > amplitudes[i + 1])
                {
                    double freqCph   = frequencies[i];
                    double periodMin = freqCph > 0 ? 60.0 / freqCph : double.MaxValue;

                    var peak = new FftPeak
                    {
                        FrequencyCph     = freqCph,
                        AmplitudeArcsec  = amplitudes[i],
                        Interpretation   = InterpretPeak(periodMin),
                        Confidence       = Math.Min(1.0, amplitudes[i] / maxAmplitude)
                    };
                    peaks.Add(peak);
                }
            }

            // Sort by descending amplitude (most important first)
            return peaks.OrderByDescending(p => p.AmplitudeArcsec).Take(10).ToList();
        }

        private string InterpretPeak(double periodMinutes)
        {
            foreach (var (min, max, label) in KNOWN_PERIODS)
                if (periodMinutes >= min && periodMinutes <= max)
                    return $"{label} (~{periodMinutes:F1} min)";

            return $"Unknown frequency (~{periodMinutes:F1} min)";
        }
    }
}
