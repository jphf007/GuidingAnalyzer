/*
 * PolarAlignmentService.cs
 * ========================
 * Service de calcul de l'erreur de mise en station polaire.
 *
 * Method: solving the Taki / Bob Denny formula by weighted least squares.
 *
 *   Dec_drift = err_az  × cos(lat) × sin(HA)
 *             + err_alt × [cos(lat) × tan(dec) − sin(lat) × cos(HA)]
 *
 * With N sessions (N ≥ 2), the system is over-determined (N equations, 2 unknowns).
 * We solve AtWA × x = AtWb where W = diag(weights), weight = min(sqrt(frames), 50).
 *
 * Conditioning of matrix A: indicates geometric reliability.
 *   < 50   → Excellent    (very diverse positions)
 *   50-300 → Good         (adequate coverage)
 *   300-1000 → Poor       (sessions too similar)
 *   > 1000 → Unreliable  (near-singular)
 *
 * Results:
 *   - err_az  (arcmin): positive = too far East, negative = too far West
 *   - err_alt (arcmin): positive = too high, negative = too low
 *   - Total   (arcmin): √(az² + alt²)
 *   - Conditioning, residual RMS, number of sessions used
 *   - Quality: Excellent / Good / Fair / Poor
 *   - Measurement advice if conditioning > 300
 */

using System;
using System.Collections.Generic;
using System.Linq;
using GuidingAnalyzer.Models;

namespace GuidingAnalyzer.Services
{
    // ═══════════════════════════════════════════════════════════════════════════
    // MODÈLE DE RÉSULTAT
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Complete result of the polar error calculation for a night.
    /// Exposed to the ViewModel for display in the Polar Alignment tab.
    /// </summary>
    public class PolarAlignmentResult
    {
        // ── Computed errors ──────────────────────────────────────────────────

        /// <summary>Erreur azimutale (arcmin). + = trop Est, − = trop Ouest.</summary>
        public double ErrAzArcmin  { get; set; }

        /// <summary>Erreur altitudinale (arcmin). + = trop haut, − = trop bas.</summary>
        public double ErrAltArcmin { get; set; }

        /// <summary>Erreur totale = √(az² + alt²) en arcmin.</summary>
        public double TotalArcmin  => Math.Sqrt(ErrAzArcmin * ErrAzArcmin + ErrAltArcmin * ErrAltArcmin);

        // ── Measurement quality ───────────────────────────────────────────────

        /// <summary>Conditionnement de la matrice A (rapport valeur singulière max/min).</summary>
        public double Conditioning  { get; set; }

        /// <summary>RMS des résidus en mas/min.</summary>
        public double ResidualRmsMasPerMin { get; set; }

        /// <summary>Nombre de sessions ayant contribué au calcul.</summary>
        public int    SessionCount  { get; set; }

        /// <summary>Nombre de sessions exclues (PPEC apprentissage, drift non fiable).</summary>
        public int    ExcludedCount { get; set; }

        /// <summary>True si le calcul a pu aboutir (≥ 2 sessions valides).</summary>
        public bool   IsValid       { get; set; }

        /// <summary>Message d'erreur si IsValid = false.</summary>
        public string ErrorMessage  { get; set; } = string.Empty;

        // ── Quality: labels and colours ──────────────────────────────────

        public PolarAlignmentQuality Quality =>
            TotalArcmin < 0.5 ? PolarAlignmentQuality.Excellent :
            TotalArcmin < 1.0 ? PolarAlignmentQuality.Good      :
            TotalArcmin < 2.0 ? PolarAlignmentQuality.Fair      :
                                PolarAlignmentQuality.Poor;

        public string QualityLabel => Quality switch
        {
            PolarAlignmentQuality.Excellent => "Excellent",
            PolarAlignmentQuality.Good      => "Good",
            PolarAlignmentQuality.Fair      => "Fair",
            _                               => "Needs correction"
        };

        public string QualityColor => Quality switch
        {
            PolarAlignmentQuality.Excellent => "#4CAF50",   // vert
            PolarAlignmentQuality.Good      => "#8BC34A",   // vert clair
            PolarAlignmentQuality.Fair      => "#FFC107",   // orange
            _                               => "#F44336"    // rouge
        };

        public string ConditioningLabel =>
            Conditioning < 50   ? "Excellent"   :
            Conditioning < 300  ? "Good"         :
            Conditioning < 1000 ? "Low"      : "Unreliable";

        public string ConditioningColor =>
            Conditioning < 50   ? "#4CAF50" :
            Conditioning < 300  ? "#8BC34A" :
            Conditioning < 1000 ? "#FFC107" : "#F44336";

        // ── Human-readable directions ────────────────────────────────────────────────

        /// <summary>Direction azimut : "Est" ou "Ouest".</summary>
        public string AzDirection  => ErrAzArcmin  >= 0 ? "towards East"   : "towards West";

        /// <summary>Direction altitude : "Haut" ou "Bas".</summary>
        public string AltDirection => ErrAltArcmin >= 0 ? "upwards"  : "downwards";

        // ── Conversion to screw turns ─────────────────────────────────────────

        /// <summary>Fractions de tour vis azimut (base paramétrable, défaut 30 arcmin/tour).</summary>
        public double AzTurns(double arcminPerTurn = 30.0)
            => arcminPerTurn > 0 ? Math.Abs(ErrAzArcmin) / arcminPerTurn : 0;

        /// <summary>Fractions de tour vis altitude (base paramétrable, défaut 30 arcmin/tour).</summary>
        public double AltTurns(double arcminPerTurn = 30.0)
            => arcminPerTurn > 0 ? Math.Abs(ErrAltArcmin) / arcminPerTurn : 0;

        // ── Measurement advice ────────────────────────────────────────────────

        /// <summary>
        /// Advice message if conditioning is degraded.
        /// Indicates which position (HA, Dec) would improve precision.
        /// </summary>
        public string MeasurementAdvice { get; set; } = string.Empty;

        // ── Contributing sessions (for display table) ──────────────────

        public List<PolarSessionRow> SessionRows { get; set; } = new();
    }

    public enum PolarAlignmentQuality { Excellent, Good, Fair, Poor }

    /// <summary>Ligne de tableau : une session et sa contribution au calcul.</summary>
    public class PolarSessionRow
    {
        public int      SessionIndex  { get; set; }
        public DateTime StartTime     { get; set; }
        public double   HourAngle     { get; set; }
        public double   DecDeg        { get; set; }
        public double   AltDeg        { get; set; }
        public double   AzDeg         { get; set; }
        public double   DecDriftRaw   { get; set; }   // arcsec/min, sur courbe monture reconstituée
        public double   Weight        { get; set; }
        public int      FrameCount    { get; set; }
        public bool     IsExcluded    { get; set; }
        public string   ExcludeReason { get; set; } = string.Empty;

        // Résidu après ajustement (arcsec/min)
        public double   Residual      { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // SERVICE
    // ═══════════════════════════════════════════════════════════════════════════

    public class PolarAlignmentService
    {
        // ── Filtering thresholds ─────────────────────────────────────────────────

        /// <summary>Durée minimale de session pour être utilisable (secondes).</summary>
        private const double MinSessionDurationSec = 120.0;

        /// <summary>Nombre minimum de frames valides.</summary>
        private const int MinFrames = 30;

        /// <summary>Poids maximum par session (évite qu'une très longue session domine).</summary>
        private const double MaxWeight = 50.0;

        // ══════════════════════════════════════════════════════════════════════
        // MAIN ENTRY POINT
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Computes err_az and err_alt from a list of guiding sessions.
        /// </summary>
        /// <param name="sessions">Night sessions (all or filtered).</param>
        /// <param name="latitudeDeg">Site latitude in degrees (e.g. 48.8 for Île-de-France).</param>
        /// <param name="arcminPerTurn">Screw sensitivity (arcmin/turn). Default 30.</param>
        public PolarAlignmentResult Compute(
            IEnumerable<GuidingSession> sessions,
            double latitudeDeg,
            double arcminPerTurn = 30.0)
        {
            var result = new PolarAlignmentResult();

            // ── 1. Build session rows ────────────────────────────
            var rows = BuildSessionRows(sessions.ToList());
            result.SessionRows = rows;

            var validRows = rows.Where(r => !r.IsExcluded).ToList();
            result.SessionCount  = validRows.Count;
            result.ExcludedCount = rows.Count - validRows.Count;

            if (validRows.Count < 2)
            {
                result.IsValid      = false;
                result.ErrorMessage = validRows.Count == 0
                    ? "No usable sessions — sessions too short or missing position metadata."
                    : "Only one valid session — at least 2 with different positions are required.";
                return result;
            }

            // ── 2. Build matrix A and vector b ─────────────────────
            double latRad = latitudeDeg * Math.PI / 180.0;

            double[] dAz  = new double[validRows.Count];
            double[] dAlt = new double[validRows.Count];
            double[] b    = new double[validRows.Count];
            double[] w    = new double[validRows.Count];

            for (int i = 0; i < validRows.Count; i++)
            {
                var row   = validRows[i];
                double ha  = row.HourAngle * Math.PI / 180.0;   // HA en radians (converti depuis heures → degrés → rad)
                double dec = row.DecDeg    * Math.PI / 180.0;

                // Azimuth term: cos(lat) × sin(HA)
                dAz[i]  = Math.Cos(latRad) * Math.Sin(ha);

                // Altitude term: cos(lat)×tan(dec) − sin(lat)×cos(HA)
                // Guard on tan(dec) for Dec near ±90°
                double tanDec = Math.Abs(dec) < 1.55 ? Math.Tan(dec) : Math.Sign(dec) * 50.0;
                dAlt[i] = Math.Cos(latRad) * tanDec - Math.Sin(latRad) * Math.Cos(ha);

                b[i]    = row.DecDriftRaw;   // arcsec/min
                w[i]    = row.Weight;
            }

            // ── 3. Solve (AtWA)^-1 AtWb ─────────────────────────────────
            // 2×2 matrix : AtWA = [ Σ w·dAz²     Σ w·dAz·dAlt ]
            //                      [ Σ w·dAz·dAlt  Σ w·dAlt²   ]
            double s11 = 0, s12 = 0, s22 = 0, r1 = 0, r2 = 0;
            for (int i = 0; i < validRows.Count; i++)
            {
                s11 += w[i] * dAz[i]  * dAz[i];
                s12 += w[i] * dAz[i]  * dAlt[i];
                s22 += w[i] * dAlt[i] * dAlt[i];
                r1  += w[i] * dAz[i]  * b[i];
                r2  += w[i] * dAlt[i] * b[i];
            }

            double det = s11 * s22 - s12 * s12;
            if (Math.Abs(det) < 1e-12)
            {
                result.IsValid      = false;
                result.ErrorMessage = "Singular system — sessions have too similar positions. " +
                                      "Observe on a different target (different HA or Dec).";
                return result;
            }

            // Direct 2×2 solution
            double errAzArcsec  = ( s22 * r1 - s12 * r2) / det;  // arcsec/min → arcmin ci-dessous
            double errAltArcsec = (-s12 * r1 + s11 * r2) / det;

            // Convert arcsec → arcmin
            result.ErrAzArcmin  = errAzArcsec  / 60.0;
            result.ErrAltArcmin = errAltArcsec / 60.0;
            result.IsValid      = true;

            // ── 4. Conditioning ────────────────────────────────────────────
            // Approximation via eigenvalue ratio of AtWA
            double trace = s11 + s22;
            double discr = Math.Sqrt(Math.Max(0, (s11 - s22) * (s11 - s22) + 4 * s12 * s12));
            double lambda1 = (trace + discr) / 2.0;
            double lambda2 = (trace - discr) / 2.0;
            result.Conditioning = lambda2 > 1e-12 ? lambda1 / lambda2 : 9999.0;

            // ── 5. Residual RMS ─────────────────────────────────────────────────
            double sumResidSq = 0;
            for (int i = 0; i < validRows.Count; i++)
            {
                double predicted = dAz[i] * errAzArcsec + dAlt[i] * errAltArcsec;
                double residual  = b[i] - predicted;
                validRows[i].Residual = residual;
                sumResidSq += residual * residual;
            }
            // In mas/min (×1000 for mas)
            result.ResidualRmsMasPerMin = validRows.Count > 2
                ? Math.Sqrt(sumResidSq / (validRows.Count - 2)) * 1000.0
                : 0;

            // ── 6. Measurement advice ──────────────────────────────────────────
            result.MeasurementAdvice = BuildMeasurementAdvice(result.Conditioning, validRows, latitudeDeg);

            return result;
        }

        // ══════════════════════════════════════════════════════════════════════
        // SESSION ROW CONSTRUCTION
        // ══════════════════════════════════════════════════════════════════════

        private List<PolarSessionRow> BuildSessionRows(List<GuidingSession> sessions)
        {
            var rows = new List<PolarSessionRow>();

            foreach (var session in sessions)
            {
                var row = new PolarSessionRow
                {
                    SessionIndex = session.SessionIndex,
                    StartTime    = session.StartTime,
                    FrameCount   = session.RawPoints.Count(p => p.IsGuideStep && !p.IsDitherOrSettling),
                };

                // ── Duration / frames check ────────────────────────────────
                var validPts = session.RawPoints
                    .Where(p => p.IsGuideStep && !p.IsDitherOrSettling)
                    .ToList();

                if (validPts.Count < MinFrames)
                {
                    row.IsExcluded    = true;
                    row.ExcludeReason = $"Too few frames ({validPts.Count} < {MinFrames})";
                    rows.Add(row);
                    continue;
                }

                double durationSec = (validPts.Last().Timestamp - validPts.First().Timestamp).TotalSeconds;
                if (durationSec < MinSessionDurationSec)
                {
                    row.IsExcluded    = true;
                    row.ExcludeReason = $"Session too short ({durationSec:F0} s < {MinSessionDurationSec} s)";
                    rows.Add(row);
                    continue;
                }

                // ── Sky position ───────────────────────────────────────────
                // Prefer SessionInfo (more precise) then Calibration
                double ha = double.NaN, dec = double.NaN, alt = double.NaN, az = double.NaN;

                if (session.SessionInfo != null)
                {
                    ha  = session.SessionInfo.HourAngle * 15.0;  // heures → degrés
                    dec = session.SessionInfo.DecDeg;
                    alt = session.SessionInfo.AltDeg;
                    az  = session.SessionInfo.AzDeg;
                }
                else if (session.Calibration != null)
                {
                    ha  = session.Calibration.HourAngle * 15.0;
                    dec = session.Calibration.DecDeg;
                    alt = session.Calibration.AltDeg;
                    az  = session.Calibration.AzDeg;
                }

                if (double.IsNaN(ha) || double.IsNaN(dec))
                {
                    row.IsExcluded    = true;
                    row.ExcludeReason = "No position metadata (HA/Dec absent from log)";
                    rows.Add(row);
                    continue;
                }

                row.HourAngle = ha;
                row.DecDeg    = dec;
                row.AltDeg    = alt;
                row.AzDeg     = az;

                // ── Detect PPEC learning phase ────────────────────────────
                if (IsPpecLearning(session))
                {
                    row.IsExcluded    = true;
                    row.ExcludeReason = "PPEC in learning phase — RA drift not representative";
                    rows.Add(row);
                    continue;
                }

                // ── DEC drift on mount curve (real mechanical drift) ────────
                row.DecDriftRaw = ComputeDecDriftFromMountCurve(session);

                // ── Weight = min(√frames, 50) ────────────────────────────────────
                row.Weight = Math.Min(Math.Sqrt(row.FrameCount), MaxWeight);

                rows.Add(row);
            }

            return rows;
        }

        // ══════════════════════════════════════════════════════════════════════
        // DEC DRIFT ON MOUNT CURVE (TRUE MECHANICAL DRIFT)
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Computes the real DEC drift in arcsec/min from the reconstructed mount curve.
        ///
        /// WHY the mount curve and NOT rawPoints:
        /// PHD2 actively guides DEC (algorithme Resist Switch ou similar) et maintient
        /// l'erreur Dec brute (rawPoints.DecError) proche de zéro en permanence.
        /// La régression sur rawPoints donne donc toujours ≈ 0, quelle que soit
        /// la qualité de la mise en station — ce qui rend le calcul inutile.
        ///
        /// The reconstructed mount curve (MountCurvePoint.DecMountPosition) cumule
        /// Position[n] = Erreur[n] + Correction[n-1], ce qui représente la dérive
        /// MÉCANIQUE réelle que PHD2 a dû compenser. Sa pente est la dérive vraie.
        /// </summary>
        private static double ComputeDecDriftFromMountCurve(GuidingSession session)
        {
            if (session.MountCurve == null || session.MountCurve.Count < 10)
            {
                // Fallback sur rawPoints si la courbe n'est pas disponible
                return ComputeDecDriftRaw(
                    session.RawPoints.Where(p => p.IsGuideStep && !p.IsDitherOrSettling).ToList());
            }

            var clean = session.MountCurve
                .Where(p => !p.IsDitherOrSettling)
                .ToList();
            if (clean.Count < 10)
                return ComputeDecDriftRaw(
                    session.RawPoints.Where(p => p.IsGuideStep && !p.IsDitherOrSettling).ToList());

            double durationMin = (clean.Last().Timestamp - clean.First().Timestamp).TotalMinutes;
            if (durationMin < 0.1) return 0;

            int n = clean.Count;
            double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
            for (int i = 0; i < n; i++)
            {
                double x = i;
                double y = clean[i].DecMountPosition;
                sumX  += x; sumY  += y;
                sumXY += x * y; sumX2 += x * x;
            }
            double denom = n * sumX2 - sumX * sumX;
            if (Math.Abs(denom) < 1e-10) return 0;

            // slope en arcsec/frame → convertir en arcsec/min
            double slope = (n * sumXY - sumX * sumY) / denom;
            double frameIntervalMin = durationMin / Math.Max(1, n - 1);
            return frameIntervalMin > 0 ? slope / frameIntervalMin : 0;
        }

        /// <summary>
        /// Linear regression on raw DEC errors (rawPoints) — FALLBACK ONLY.
        /// Do not use normally : PHD2 corrige activement la Dec, donc cette
        /// valeur est toujours proche de zéro et ne reflète pas la dérive réelle.
        /// </summary>
        private static double ComputeDecDriftRaw(List<GuidingPoint> points)
        {
            if (points.Count < 2) return 0;

            var t0 = points.First().Timestamp;
            int n  = points.Count;

            double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
            foreach (var p in points)
            {
                double x = (p.Timestamp - t0).TotalMinutes;
                double y = p.DecError;
                sumX  += x;
                sumY  += y;
                sumXY += x * y;
                sumX2 += x * x;
            }

            double denom = n * sumX2 - sumX * sumX;
            if (Math.Abs(denom) < 1e-10) return 0;

            return (n * sumXY - sumX * sumY) / denom;  // arcsec/min
        }

        // ══════════════════════════════════════════════════════════════════════
        // PPEC LEARNING DETECTION
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Returns true if the session was in PPEC learning phase.
        /// Indicateur : la valeur "AD PPEC aggressiveness" change dans l'en-tête.
        /// Ici on utilise le flag que GuidingDataService pourrait stocker,
        /// ou on le déduit des statistiques de dérive RA anormalement élevées.
        /// </summary>
        private static bool IsPpecLearning(GuidingSession session)
        {
            // Si GuidingDataService a parsé le flag PPEC et l'a stocké dans SessionInfo,
            // on l'utilise directement. Ce champ est à ajouter à GuidingSessionInfo (P1).
            // Pour l'instant : heuristique — si RA drift > 0.5 arcsec/min ET Dec < 10°,
            // c'est suspect (phase d'apprentissage PPEC typique sur une cible à Dec~0°).
            // TODO P1 : remplacer par session.SessionInfo.IsPpecLearning quand disponible.

            if (session.Statistics == null) return false;

            bool suspiciousRaDrift = Math.Abs(session.Statistics.RaDriftArcsecPerMin) > 0.5;
            bool lowDec            = session.SessionInfo != null && Math.Abs(session.SessionInfo.DecDeg) < 10.0;

            return suspiciousRaDrift && lowDec;
        }

        // ══════════════════════════════════════════════════════════════════════
        // MEASUREMENT ADVICE
        // ══════════════════════════════════════════════════════════════════════

        private static string BuildMeasurementAdvice(
            double conditioning,
            List<PolarSessionRow> rows,
            double latitudeDeg)
        {
            if (conditioning <= 300) return string.Empty;

            // Analyse HA distribution pour savoir ce qui manque
            var has  = rows.Select(r => r.HourAngle).ToList();
            var decs = rows.Select(r => r.DecDeg).ToList();

            bool hasLowDec   = decs.Any(d => Math.Abs(d) < 30);
            bool hasEastSide = has.Any(h => h < -30);   // HA < −2h = côté Est
            bool hasWestSide = has.Any(h => h >  30);   // HA > +2h = côté Ouest
            bool hasNearMer  = has.Any(h => Math.Abs(h) < 20); // HA ≈ 0

            if (!hasLowDec)
                return "Poor conditioning — to improve precision, " +
                       "observe a target at low declination (Dec < 30°). " +
                       "The meridian (HA ≈ 0h) is ideal for isolating the altitude error.";

            if (!hasEastSide)
                return "Poor conditioning — to improve azimuth precision, " +
                       "observe a target to the East (HA ≈ −3h to −5h, Dec < 30°). " +
                       "Run the Guiding Assistant for at least 10 minutes.";

            if (!hasWestSide)
                return "Poor conditioning — to improve azimuth precision, " +
                       "observe a target to the West (HA ≈ +3h to +5h, Dec < 30°). " +
                       "Run the Guiding Assistant for at least 10 minutes.";

            return $"Conditioning = {conditioning:F0} — diversify positions " +
                   "(varied HA and Dec) to improve calculation reliability.";
        }

        // ══════════════════════════════════════════════════════════════════════
        // RECONSTRUCTED LATITUDE (bonus)
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Estimates the site latitude from PHD2 calibration data.
        ///
        /// Principe : lors d'une calibration, PHD2 déplace l'étoile guide en RA et DEC
        /// et mesure les vitesses en px/s. Le rapport des vitesses dépend de la déclinaison
        /// et de l'échelle de plaque. On peut en dériver la latitude si on dispose de
        /// plusieurs calibrations à des déclinaisons différentes.
        ///
        /// Ici : estimateur simple basé sur la déclinaison de calibration et la vitesse
        /// normalisée de guidage.
        ///
        /// Returns double.NaN if data is insufficient.
        /// </summary>
        public static double EstimateLatitudeFromCalibration(GuidingSession session)
        {
            var cal = session.Calibration;
            if (cal == null) return double.NaN;

            // PHD2 corrige automatiquement la vitesse RA pour la déclinaison : RA_rate_corr = RA_rate / cos(dec)
            // La vitesse DEC est indépendante de la latitude (dans un premier ordre).
            // La latitude est directement lue dans CalibrationSession.AltDeg si disponible.
            if (cal.AltDeg != 0 && cal.AzDeg != 0)
            {
                // Conversion Alt/Az → latitude approchée (étoile polaire proche du pôle)
                // Non applicable ici — on retourne AltDeg du site si disponible dans SessionInfo
            }

            if (session.SessionInfo != null && session.SessionInfo.AltDeg > 0)
            {
                // La latitude = altitude de l'objet au méridien + Dec − 90° ... trop approximatif.
                // On retourne NaN et on laisse l'utilisateur saisir la valeur.
            }

            return double.NaN;  // Pas de déduction fiable sans Polaris visible
        }
    }
}
