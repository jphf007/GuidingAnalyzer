using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using GuidingAnalyzer.Models;

namespace GuidingAnalyzer.Services
{
    public class GuidingDataService
    {
        // ────────────────────────────────────────────────────────────────────
        // LOADING: returns ALL sessions from the file
        // ────────────────────────────────────────────────────────────────────

        public async Task<List<GuidingSession>> LoadAllSessionsAsync(string filePath)
        {
            var lines = await File.ReadAllLinesAsync(filePath);
            return ParseAllSessions(filePath, lines);
        }

        // ────────────────────────────────────────────────────────────────────
        // MULTI-SESSION PARSING
        // ────────────────────────────────────────────────────────────────────

        private List<GuidingSession> ParseAllSessions(string filePath, string[] lines)
        {
            var sessions = new List<GuidingSession>();
            int sessionNumber = 0;

            // Global file metadata (avant la 1ère session)
            string globalCamera = string.Empty;
            string globalMount  = string.Empty;
            double globalPixelScale  = 0;
            double globalFocalLength = 0;
            ParseGlobalHeader(lines, ref globalCamera, ref globalMount,
                              ref globalPixelScale, ref globalFocalLength);

            CalibrationSession? lastCalibration = null;

            int i = 0;
            while (i < lines.Length)
            {
                var line = lines[i].Trim();

                // Parse calibration block — often precedes a "Guiding Begins"
                if (line.StartsWith("Calibration Begins"))
                {
                    lastCalibration = ParseCalibrationBlock(lines, ref i);
                    continue;
                }

                if (line.StartsWith("Guiding Begins"))
                {
                    sessionNumber++;
                    var session = new GuidingSession
                    {
                        SourceFile   = filePath,
                        LoadedAt     = DateTime.Now,
                        SessionIndex = sessionNumber,
                        CameraName   = globalCamera,
                        MountName    = globalMount,
                        PixelScale   = globalPixelScale,
                        FocalLength  = globalFocalLength,
                    };

                    // Start timestamp
                    var m = Regex.Match(line, @"(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})");
                    if (m.Success && DateTime.TryParse(m.Groups[1].Value, out DateTime parsedStart))
                        session.StartTime = parsedStart;

                    // Local header (entre Guiding Begins et la ligne Frame,Time,...)
                    i++;
                    while (i < lines.Length && !lines[i].StartsWith("Frame,") && !lines[i].StartsWith("Guiding Ends"))
                    {
                        ParseSessionHeader(lines[i], session);
                        i++;
                    }

                    // Column header line
                    if (i < lines.Length && lines[i].StartsWith("Frame,"))
                    {
                        var colMap = BuildColumnMap(lines[i]);
                        i++;

                        // Dither/settling flag: enabled by "INFO: DITHER" or "Settling started"
                        // disabled by "Settling is Done" or "SETTLE FAILED"
                        bool inSettling = false;

                        // Data lines
                        while (i < lines.Length
                            && !lines[i].StartsWith("Guiding Ends")
                            && !lines[i].StartsWith("Guiding Begins"))
                        {
                            var line2 = lines[i].Trim();

                            // Detect dither/settling events/settling dans les lignes INFO
                            if (line2.StartsWith("INFO:"))
                            {
                                // Actual formats observed in PHD2 logs:
                                // End   : "SETTLING STATE CHANGE, Settling complete"
                                //         "SETTLING STATE CHANGE, Settling failed"
                                // Start : "SETTLING STATE CHANGE, Settling started"
                                //         "DITHER by X, Y, new lock pos = ..."
                                if (line2.Contains("Settling complete") ||
                                    line2.Contains("Settling failed"))
                                {
                                    inSettling = false;
                                }
                                else if (line2.Contains("Settling started") ||
                                         line2.Contains("DITHER"))
                                {
                                    inSettling = true;
                                }
                                i++;
                                continue;
                            }

                            // Parse data
                            var pt = ParseDataLine(line2, colMap, session.StartTime, session.PixelScale);
                            if (pt != null)
                            {
                                pt.IsDitherOrSettling = inSettling; // Mark the point
                                session.RawPoints.Add(pt);
                            }
                            i++;
                        }
                    }

                    session.Calibration = lastCalibration;

                    if (session.RawPoints.Count > 0)
                        sessions.Add(session);
                }
                else
                {
                    i++;
                }
            }

            return sessions;
        }

        // ────────────────────────────────────────────────────────────────────
        // GLOBAL HEADER (before the first session)
        // ────────────────────────────────────────────────────────────────────

        private void ParseGlobalHeader(string[] lines,
            ref string camera, ref string mount,
            ref double pixelScale, ref double focalLength)
        {
            foreach (var line in lines)
            {
                if (line.StartsWith("Guiding Begins")) break;
                TryExtractHeaderValues(line, ref camera, ref mount, ref pixelScale, ref focalLength);
            }
        }

        // ────────────────────────────────────────────────────────────────────
        // LOCAL HEADER (between Guiding Begins and data)
        // ────────────────────────────────────────────────────────────────────

        private void ParseSessionHeader(string line, GuidingSession session)
        {
            string cam   = session.CameraName;
            string mount = session.MountName;
            double ps    = session.PixelScale;
            double fl    = session.FocalLength;
            TryExtractHeaderValues(line, ref cam, ref mount, ref ps, ref fl);
            session.CameraName  = cam;
            session.MountName   = mount;
            session.PixelScale  = ps;
            session.FocalLength = fl;

            session.SessionInfo ??= new GuidingSessionInfo();
            var info = session.SessionInfo;
            info.CameraName         = cam;
            info.MountName          = mount;
            info.PixelScaleArcSecPx = ps;
            info.FocalLengthMm      = fl;

            var h = line.Trim();

            // Equipment Profile = jph
            if (h.StartsWith("Equipment Profile ="))
                info.EquipmentProfile = h.Split('=')[1].Trim();

            // Dither = both axes, Dither scale = 1.000, Image noise reduction = 3x3 median, Guide-frame time lapse = 0
            if (h.StartsWith("Dither ="))
            {
                var mAx  = Regex.Match(h, @"Dither\s*=\s*([^,]+)");
                var mSc  = Regex.Match(h, @"Dither scale\s*=\s*([\d.]+)");
                var mNr  = Regex.Match(h, @"Image noise reduction\s*=\s*([^,]+)");
                var mTl  = Regex.Match(h, @"Guide-frame time lapse\s*=\s*(\d+)");
                if (mAx.Success)  info.DitherAxes = mAx.Groups[1].Value.Trim();
                if (mSc.Success && double.TryParse(mSc.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double ds)) info.DitherScale = ds;
                if (mNr.Success)  info.NoiseReduction = mNr.Groups[1].Value.Trim();
                if (mTl.Success && int.TryParse(mTl.Groups[1].Value, out int tl)) info.GuideFrameTimeLapse = tl;
            }

            // Pixel scale = 1.13 arc-sec/px, Binning = 1, Focal length = 530 mm
            if (h.StartsWith("Pixel scale ="))
            {
                var mBin = Regex.Match(h, @"Binning\s*=\s*(\d+)");
                if (mBin.Success && int.TryParse(mBin.Groups[1].Value, out int bin)) info.Binning = bin;
            }

            // Search region = 50 px, Star mass tolerance disabled, Multi-star mode, list size = 12
            if (h.StartsWith("Search region ="))
            {
                var mSr  = Regex.Match(h, @"Search region\s*=\s*(\d+)");
                var mSz  = Regex.Match(h, @"list size\s*=\s*(\d+)");
                if (mSr.Success  && int.TryParse(mSr.Groups[1].Value,  out int sr)) info.SearchRegionPx = sr;
                if (mSz.Success  && int.TryParse(mSz.Groups[1].Value,  out int sz)) info.MultiStarListSize = sz;
                info.StarMassToleranceEnabled = !h.Contains("tolerance disabled");
                info.MultiStarMode = h.Contains("Multi-star mode");
            }

            // Camera = ZWO ASI290MM Mini, gain = 50, full size = 1936 x 1096, no dark, no defect map, pixel size = 2.9 um
            if (h.TrimStart().StartsWith("Camera ="))
            {
                var mGn  = Regex.Match(h, @"gain\s*=\s*(\d+)");
                var mSz  = Regex.Match(h, @"full size\s*=\s*([\d]+ x [\d]+)");
                var mPx  = Regex.Match(h, @"pixel size\s*=\s*([\d.]+)");
                if (mGn.Success && int.TryParse(mGn.Groups[1].Value, out int gain)) info.CameraGain = gain;
                if (mSz.Success) info.CameraFullSize = mSz.Groups[1].Value;
                if (mPx.Success && double.TryParse(mPx.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double pxSz)) info.CameraPixelSizeUm = pxSz;
                info.CameraHasDark      = !h.Contains("no dark");
                info.CameraHasDefectMap = !h.Contains("no defect map");
            }

            // Exposure = 1500 ms
            if (h.StartsWith("Exposure ="))
            {
                var m = Regex.Match(h, @"Exposure\s*=\s*(\d+)");
                if (m.Success && int.TryParse(m.Groups[1].Value, out int exp)) info.ExposureMs = exp;
            }

            // Mount = ..., connected, guiding enabled, xAngle = 0.3, xRate = 4.041, yAngle = 101.9, yRate = 6.442, parity = +/-
            if (h.StartsWith("Mount =") && h.Contains("xAngle"))
            {
                var mXa = Regex.Match(h, @"xAngle\s*=\s*([\d.]+)");
                var mXr = Regex.Match(h, @"xRate\s*=\s*([\d.]+)");
                var mYa = Regex.Match(h, @"yAngle\s*=\s*([\d.]+)");
                var mYr = Regex.Match(h, @"yRate\s*=\s*([\d.]+)");
                var mPa = Regex.Match(h, @"parity\s*=\s*(\S+)");
                if (mXa.Success && double.TryParse(mXa.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double xa)) info.MountXAngle = xa;
                if (mXr.Success && double.TryParse(mXr.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double xr)) info.MountXRate  = xr;
                if (mYa.Success && double.TryParse(mYa.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double ya)) info.MountYAngle = ya;
                if (mYr.Success && double.TryParse(mYr.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double yr)) info.MountYRate  = yr;
                if (mPa.Success) info.MountParity = mPa.Groups[1].Value;
            }

            // Norm rates RA = 7.8"/s @ dec 0, Dec = 7.3"/s; ortho.err. = 11.6 deg
            if (h.StartsWith("Norm rates RA ="))
            {
                var mRa   = Regex.Match(h, @"Norm rates RA\s*=\s*([\d.]+)");
                var mDec  = Regex.Match(h, @"Dec\s*=\s*([\d.]+)");
                var mOrth = Regex.Match(h, @"ortho\.err\.\s*=\s*([\d.]+)");
                if (mRa.Success)   info.NormRatesRa  = $"{mRa.Groups[1].Value}\"/s @ dec 0";
                if (mDec.Success)  info.NormRatesDec = $"{mDec.Groups[1].Value}\"/s";
                if (mOrth.Success && double.TryParse(mOrth.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double oe)) info.OrthoErrorDeg = oe;
            }

            // X guide algorithm = Predictive PEC, Control gain = 1.000
            if (h.StartsWith("X guide algorithm ="))
            {
                info.GuideAlgoRA = h.Split('=')[1].Split(',')[0].Trim();
                var mCg = Regex.Match(h, @"Control gain\s*=\s*([\d.]+)");
                if (mCg.Success && double.TryParse(mCg.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double cg)) info.RaControlGain = cg;
                var mMm = Regex.Match(h, @"Minimum move\s*=\s*([\d.]+)");
                if (mMm.Success && double.TryParse(mMm.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double mm)) info.MinMotionRA = mm;
            }

            // Prediction gain = 0.800
            if (h.StartsWith("Prediction gain ="))
            {
                var m = Regex.Match(h, @"([\d.]+)$");
                if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double pg)) info.RaPredictionGain = pg;
            }

            // Minimum move = 0.150  (ligne séparée pour Predictive PEC)
            if (h.StartsWith("Minimum move =") && info.MinMotionRA == 0)
            {
                var m = Regex.Match(h, @"([\d.]+)$");
                if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double mm)) info.MinMotionRA = mm;
            }

            // Hyperparamètres Predictive PEC (lignes indentées par \t)
            if (h.StartsWith("Period length periodic kernel ="))
            {
                var m = Regex.Match(h, @"([\d.]+)$");
                if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double pl)) info.PecPeriodLength = pl;
            }
            if (h.StartsWith("FFT called after ="))
            {
                var m = Regex.Match(h, @"([\d.]+)");
                if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double fc)) info.PecFftAfterCycles = fc;
            }
            if (h.StartsWith("Auto-adjust period length ="))
                info.PecAutoAdjustPeriod = h.Split('=').Last().Trim();

            // Y guide algorithm = Resist Switch, Minimum move = 0.200 Aggression = 100% FastSwitch = enabled
            if (h.StartsWith("Y guide algorithm ="))
            {
                info.GuideAlgoDEC = h.Split('=')[1].Split(',')[0].Trim();
                var mMm = Regex.Match(h, @"Minimum move\s*=\s*([\d.]+)");
                if (mMm.Success && double.TryParse(mMm.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double mm)) info.MinMotionDEC = mm;
                var mAg = Regex.Match(h, @"Aggression\s*=\s*([\d.]+)");
                if (mAg.Success && double.TryParse(mAg.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double ag)) info.DecAggression = ag;
                info.DecFastSwitch = h.Contains("FastSwitch = enabled");
            }

            // Backlash comp = enabled, pulse = 670 ms
            if (h.StartsWith("Backlash comp ="))
            {
                var mE = Regex.Match(h, @"Backlash comp\s*=\s*(\w+)");
                if (mE.Success) info.BacklashCompEnabled = mE.Groups[1].Value;
                var mP = Regex.Match(h, @"pulse\s*=\s*(\d+)");
                if (mP.Success && int.TryParse(mP.Groups[1].Value, out int bp)) info.BacklashPulseMs = bp;
            }

            // Max RA duration = 2500, Max DEC duration = 2500, DEC guide mode = Auto
            if (h.StartsWith("Max RA duration"))
            {
                var mMd  = Regex.Match(h, @"DEC guide mode\s*=\s*(\w+)");
                if (mMd.Success) info.DecGuideMode = mMd.Groups[1].Value;
                var mRa  = Regex.Match(h, @"Max RA duration\s*=\s*(\d+)");
                if (mRa.Success  && int.TryParse(mRa.Groups[1].Value,  out int mrd)) info.MaxRaDuration  = mrd;
                var mDec = Regex.Match(h, @"Max DEC duration\s*=\s*(\d+)");
                if (mDec.Success && int.TryParse(mDec.Groups[1].Value, out int mdd)) info.MaxDecDuration = mdd;
            }

            // RA Guide Speed = 7.5 a-s/s, Dec Guide Speed = 7.5 a-s/s, Cal Dec = 0.0, Last Cal Issue = None, Timestamp = ...
            if (h.StartsWith("RA Guide Speed ="))
            {
                var mR  = Regex.Match(h, @"RA Guide Speed\s*=\s*([\d.]+)");
                var mD  = Regex.Match(h, @"Dec Guide Speed\s*=\s*([\d.]+)");
                var mCd = Regex.Match(h, @"Cal Dec\s*=\s*(-?[\d.]+)");
                var mLc = Regex.Match(h, @"Last Cal Issue\s*=\s*([^,]+)");
                var mTs = Regex.Match(h, @"Timestamp\s*=\s*(.+)$");
                if (mR.Success  && double.TryParse(mR.Groups[1].Value,  NumberStyles.Float, CultureInfo.InvariantCulture, out double ra))
                {
                    info.GuideRateRA = ra;
                    session.GuidingRateArcsecPerMs = ra / 1000.0;
                }
                if (mD.Success  && double.TryParse(mD.Groups[1].Value,  NumberStyles.Float, CultureInfo.InvariantCulture, out double dec)) info.GuideRateDEC = dec;
                if (mCd.Success && double.TryParse(mCd.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double cd))  info.CalDec = cd;
                if (mLc.Success) info.LastCalIssue = mLc.Groups[1].Value.Trim();
                if (mTs.Success) info.CalTimestamp = mTs.Groups[1].Value.Trim();
            }

            // RA = 14.07 hr, Dec = 54.2 deg, Hour angle = -3.71 hr, Pier side = West, Rotator pos = N/A, Alt = 55.9 deg, Az = 59.5 deg
            if (h.StartsWith("RA ="))
            {
                var mRa  = Regex.Match(h, @"RA\s*=\s*([\d.]+)\s*hr");
                var mDec = Regex.Match(h, @"Dec\s*=\s*(-?[\d.]+)\s*deg");
                var mAh  = Regex.Match(h, @"Hour angle\s*=\s*(-?[\d.]+)");
                var mAlt = Regex.Match(h, @"Alt\s*=\s*([\d.]+)\s*deg");
                var mAz  = Regex.Match(h, @"Az\s*=\s*([\d.]+)\s*deg");
                var mPs  = Regex.Match(h, @"Pier side\s*=\s*(\w+)");
                if (mRa.Success  && double.TryParse(mRa.Groups[1].Value,  NumberStyles.Float, CultureInfo.InvariantCulture, out double ra))  info.RaHours   = ra;
                if (mDec.Success && double.TryParse(mDec.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double dec)) info.DecDeg    = dec;
                if (mAh.Success  && double.TryParse(mAh.Groups[1].Value,  NumberStyles.Float, CultureInfo.InvariantCulture, out double ah))  info.HourAngle = ah;
                if (mAlt.Success && double.TryParse(mAlt.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double alt)) info.AltDeg    = alt;
                if (mAz.Success  && double.TryParse(mAz.Groups[1].Value,  NumberStyles.Float, CultureInfo.InvariantCulture, out double az))  info.AzDeg     = az;
                if (mPs.Success) info.PierSide = mPs.Groups[1].Value;
            }

            // Lock position = 764.261, 713.008, Star position = ..., HFD = 6.08 px
            if (h.StartsWith("Lock position ="))
            {
                var mLk  = Regex.Match(h, @"Lock position\s*=\s*([\d.]+),\s*([\d.]+)");
                var mHfd = Regex.Match(h, @"HFD\s*=\s*([\d.]+)");
                if (mLk.Success)
                {
                    if (double.TryParse(mLk.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double lx)) info.LockPositionX = lx;
                    if (double.TryParse(mLk.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double ly)) info.LockPositionY = ly;
                }
                if (mHfd.Success && double.TryParse(mHfd.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double hfd)) info.StarHfd = hfd;
            }
        }

                private void TryExtractHeaderValues(string line,
            ref string camera, ref string mount,
            ref double pixelScale, ref double focalLength)
        {
            // Camera = ZWO ASI290MM Mini, ...
            if (line.TrimStart().StartsWith("Camera ="))
            {
                var parts = line.Split('=');
                if (parts.Length > 1)
                    camera = parts[1].Split(',')[0].Trim();
            }

            // Mount = Gemini Telescope .NET (ASCOM), ...
            if (line.StartsWith("Mount ="))
            {
                var parts = line.Split('=');
                if (parts.Length > 1)
                    mount = parts[1].Split(',')[0].Trim();
            }

            // Pixel scale = 1.13 arc-sec/px, ...
            if (line.StartsWith("Pixel scale ="))
            {
                var m = Regex.Match(line, @"Pixel scale\s*=\s*([\d.]+)");
                if (m.Success && double.TryParse(m.Groups[1].Value,
                    NumberStyles.Float, CultureInfo.InvariantCulture, out double ps))
                    pixelScale = ps;
            }

            // Focal length = 530 mm
            if (line.Contains("Focal length ="))
            {
                var m = Regex.Match(line, @"Focal length\s*=\s*(\d+)");
                if (m.Success && double.TryParse(m.Groups[1].Value,
                    NumberStyles.Float, CultureInfo.InvariantCulture, out double fl))
                    focalLength = fl;
            }
        }

        // ────────────────────────────────────────────────────────────────────
        // COLUMN PARSING
        // ────────────────────────────────────────────────────────────────────

        private Dictionary<string, int> BuildColumnMap(string headerLine)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var cols = headerLine.Split(',');
            for (int i = 0; i < cols.Length; i++)
                map[cols[i].Trim()] = i;
            return map;
        }

        private GuidingPoint? ParseDataLine(string line, Dictionary<string, int> colMap,
                                             DateTime sessionStart, double pixelScale)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("//")) return null;
            var parts = line.Split(',');
            if (parts.Length < 5) return null;

            try
            {
                double scale = pixelScale > 0 ? pixelScale : 1.0;
                var pt = new GuidingPoint();

                pt.FrameIndex = GetInt(parts, colMap, "Frame", 0);

                double timeSec = GetDouble(parts, colMap, "Time", 0);
                pt.Timestamp = sessionStart.AddSeconds(timeSec);

                // PHD2 stores raw distances in pixels → convert to arcsec
                pt.RaError  = GetDouble(parts, colMap, "RARawDistance",  0) * scale;
                pt.DecError = GetDouble(parts, colMap, "DECRawDistance", 0) * scale;

                // Corrections: duration in ms + direction
                double raDur  = GetDouble(parts, colMap, "RADuration",  0);
                double decDur = GetDouble(parts, colMap, "DECDuration", 0);
                string raDir  = GetString(parts, colMap, "RADirection");
                string decDir = GetString(parts, colMap, "DECDirection");

                pt.RaCorrection  = raDur  * (raDir  == "W" ? -1 : 1);
                pt.DecCorrection = decDur * (decDir == "S" ? -1 : 1);

                pt.StarSNR = GetDouble(parts, colMap, "SNR", 0);

                // ErrorCode = 0 → valid frame
                int errorCode = GetInt(parts, colMap, "ErrorCode", 0);
                pt.IsGuideStep = errorCode == 0;

                return pt;
            }
            catch { return null; }
        }

        // ────────────────────────────────────────────────────────────────────
        // HELPERS
        // ────────────────────────────────────────────────────────────────────

        private double GetDouble(string[] parts, Dictionary<string, int> map, string key, double fallback)
        {
            if (map.TryGetValue(key, out int idx) && idx < parts.Length)
                if (double.TryParse(parts[idx].Trim().Trim('"'), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double v)) return v;
            return fallback;
        }

        private int GetInt(string[] parts, Dictionary<string, int> map, string key, int fallback)
        {
            if (map.TryGetValue(key, out int idx) && idx < parts.Length)
                if (int.TryParse(parts[idx].Trim().Trim('"'), out int v)) return v;
            return fallback;
        }

        private string GetString(string[] parts, Dictionary<string, int> map, string key)
        {
            if (map.TryGetValue(key, out int idx) && idx < parts.Length)
                return parts[idx].Trim().Trim('"');
            return string.Empty;
        }

        // ────────────────────────────────────────────────────────────────────
        // CALIBRATION PARSING
        // ────────────────────────────────────────────────────────────────────

        private CalibrationSession? ParseCalibrationBlock(string[] lines, ref int i)
        {
            var cal = new CalibrationSession();

            // Ligne "Calibration Begins at YYYY-MM-DD HH:MM:SS"
            var mDate = Regex.Match(lines[i], @"(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})");
            if (mDate.Success) cal.StartTime = DateTime.Parse(mDate.Groups[1].Value);
            i++;

            // En-tête calibration (jusqu'à la ligne "Direction,Step,dx,dy,x,y,Dist")
            while (i < lines.Length && !lines[i].TrimStart().StartsWith("Direction,"))
            {
                var hLine = lines[i].Trim();

                if (hLine.StartsWith("Pixel scale ="))
                {
                    var m = Regex.Match(hLine, @"Pixel scale\s*=\s*([\d.]+)");
                    if (m.Success && double.TryParse(m.Groups[1].Value,
                        NumberStyles.Float, CultureInfo.InvariantCulture, out double ps))
                        cal.PixelScaleArcSecPx = ps;
                }
                if (hLine.Contains("Focal length ="))
                {
                    var m = Regex.Match(hLine, @"Focal length\s*=\s*(\d+)");
                    if (m.Success && double.TryParse(m.Groups[1].Value,
                        NumberStyles.Float, CultureInfo.InvariantCulture, out double fl))
                        cal.FocalLengthMm = fl;
                }
                if (hLine.StartsWith("Mount ="))
                {
                    // Mount = Gemini Telescope .NET (ASCOM), Calibration Step = 400 ms, Calibration Distance = 30 px, ...
                    var mMount = Regex.Match(hLine, @"Mount\s*=\s*([^,]+)");
                    if (mMount.Success) cal.MountName = mMount.Groups[1].Value.Trim();
                    var m = Regex.Match(hLine, @"Calibration Step\s*=\s*(\d+)");
                    if (m.Success && int.TryParse(m.Groups[1].Value, out int cs))
                        cal.CalibrationStepMs = cs;
                    var m2 = Regex.Match(hLine, @"Calibration Distance\s*=\s*([\d.]+)");
                    if (m2.Success && double.TryParse(m2.Groups[1].Value,
                        NumberStyles.Float, CultureInfo.InvariantCulture, out double cd))
                        cal.CalibrationDistancePx = cd;
                }
                if (hLine.StartsWith("RA =") || hLine.StartsWith("RA="))
                {
                    // RA = 10.80 hr, Dec = 0.0 deg, Hour angle = -1.32 hr, Pier side = West, Rotator pos = N/A, Alt = 38.2 deg, Az = 154.4 deg
                    var mRa  = Regex.Match(hLine, @"RA\s*=\s*([\d.]+)\s*hr");
                    var mDec = Regex.Match(hLine, @"Dec\s*=\s*(-?[\d.]+)\s*deg");
                    var mAh  = Regex.Match(hLine, @"Hour angle\s*=\s*(-?[\d.]+)");
                    var mAlt = Regex.Match(hLine, @"Alt\s*=\s*([\d.]+)\s*deg");
                    var mAz  = Regex.Match(hLine, @"Az\s*=\s*([\d.]+)\s*deg");
                    var mPs  = Regex.Match(hLine, @"Pier side\s*=\s*(\w+)");
                    if (mRa.Success  && double.TryParse(mRa.Groups[1].Value,  NumberStyles.Float, CultureInfo.InvariantCulture, out double ra))  cal.RaHours   = ra;
                    if (mDec.Success && double.TryParse(mDec.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double dec)) cal.DecDeg    = dec;
                    if (mAh.Success  && double.TryParse(mAh.Groups[1].Value,  NumberStyles.Float, CultureInfo.InvariantCulture, out double ah))  cal.HourAngle = ah;
                    if (mAlt.Success && double.TryParse(mAlt.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double alt)) cal.AltDeg    = alt;
                    if (mAz.Success  && double.TryParse(mAz.Groups[1].Value,  NumberStyles.Float, CultureInfo.InvariantCulture, out double az))  cal.AzDeg     = az;
                    if (mPs.Success) cal.PierSide = mPs.Groups[1].Value;
                }
                // RA Guide Speed = 7.5 a-s/s, Dec Guide Speed = 7.5 a-s/s
                if (hLine.StartsWith("RA Guide Speed ="))
                {
                    var m1 = Regex.Match(hLine, @"RA Guide Speed\s*=\s*([\d.]+)");
                    var m2 = Regex.Match(hLine, @"Dec Guide Speed\s*=\s*([\d.]+)");
                    if (m1.Success && double.TryParse(m1.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double rs)) cal.RaGuideSpeedArcsecS  = rs;
                    if (m2.Success && double.TryParse(m2.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double ds)) cal.DecGuideSpeedArcsecS = ds;
                }
                i++;
            }

            if (i >= lines.Length) return cal;
            i++; // sauter "Direction,Step,dx,dy,x,y,Dist"

            // Points de calibration
            string currentDir = "";
            while (i < lines.Length)
            {
                var dLine = lines[i].Trim();

                // End of calibration
                if (dLine.StartsWith("Calibration complete") ||
                    dLine.StartsWith("Guiding Begins") ||
                    dLine.StartsWith("Guiding Ends") ||
                    dLine.StartsWith("Calibration Begins"))
                    break;

                // Summary: "West calibration complete. Angle = 0.3 deg, Rate = 6.913 px/sec, Parity = Even"
                //          "North calibration complete. Angle = 101.9 deg, Rate = 6.442 px/sec, Parity = Odd"
                if (dLine.Contains("calibration complete"))
                {
                    var mAngle  = Regex.Match(dLine, @"Angle\s*=\s*([\d.]+)");
                    var mRate   = Regex.Match(dLine, @"Rate\s*=\s*([\d.]+)");
                    var mParity = Regex.Match(dLine, @"Parity\s*=\s*(\w+)");
                    double angle  = mAngle.Success  ? double.Parse(mAngle.Groups[1].Value,  CultureInfo.InvariantCulture) : 0;
                    double rate   = mRate.Success   ? double.Parse(mRate.Groups[1].Value,   CultureInfo.InvariantCulture) : 0;
                    string parity = mParity.Success ? mParity.Groups[1].Value : "";

                    // Lire la direction directement depuis la ligne (pas currentDir)
                    string dirWord = dLine.Split(' ')[0].ToLowerInvariant();
                    if (dirWord == "west" || (dirWord == "east" && cal.RaRatePxSec == 0))
                    { cal.RaAngleDeg = angle; cal.RaRatePxSec = rate; cal.RaParity = parity; }
                    else if (dirWord == "east" && cal.RaRatePxSec == 0)
                    { cal.RaAngleDeg = angle; cal.RaRatePxSec = rate; cal.RaParity = parity; }
                    else if (dirWord == "north" || (dirWord == "south" && cal.DecRatePxSec == 0))
                    { cal.DecAngleDeg = angle; cal.DecRatePxSec = rate; cal.DecParity = parity; }
                    i++; continue;
                }

                // Data point line: "West,0,0.000,0.000,1336.511,66.003,0.000"
                var parts = dLine.Split(',');
                if (parts.Length >= 7 &&
                    int.TryParse(parts[1].Trim(), out int step))
                {
                    currentDir = parts[0].Trim();
                    if (double.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double dx) &&
                        double.TryParse(parts[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double dy) &&
                        double.TryParse(parts[4].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double x)  &&
                        double.TryParse(parts[5].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
                    {
                        var pt = new CalibrationPoint { Step = step, Dx = dx, Dy = dy, X = x, Y = y };
                        switch (currentDir.ToLower())
                        {
                            case "west":     cal.RaWestPoints.Add(pt);   break;
                            case "east":     cal.RaEastPoints.Add(pt);   break;
                            case "north":    cal.DecNorthPoints.Add(pt); break;
                            case "south":    cal.DecSouthPoints.Add(pt); break;
                            case "backlash": cal.BacklashPoints.Add(pt); break;
                        }
                    }
                }
                i++;
            }

            return cal;
        }

        public List<string> GetAvailableLogFiles()
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PHD2");
            if (!Directory.Exists(path)) return new();
            return Directory.GetFiles(path, "PHD2_GuideLog_*.txt")
                .OrderByDescending(File.GetLastWriteTime).ToList();
        }
    }
}