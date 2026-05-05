using System;
using System.Globalization;
using System.IO;
using System.Text;
using GuidingAnalyzer.Models;
using Newtonsoft.Json;

namespace GuidingAnalyzer.Services
{
    /// <summary>CSV and JSON export of guiding data.</summary>
    public class ExportService
    {
        public void ExportToCsv(GuidingSession session, string outputPath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Frame,Timestamp,RA_Error_arcsec,Dec_Error_arcsec,RA_Corr_ms,Dec_Corr_ms,SNR,Valid");
            foreach (var p in session.RawPoints)
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "{0},{1:yyyy-MM-dd HH:mm:ss},{2:F4},{3:F4},{4:F1},{5:F1},{6:F1},{7}",
                    p.FrameIndex, p.Timestamp, p.RaError, p.DecError,
                    p.RaCorrection, p.DecCorrection, p.StarSNR, p.IsGuideStep ? 1 : 0));
            File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
        }

        public void ExportToJson(GuidingSession session, string outputPath)
        {
            var export = new {
                ExportedAt   = DateTime.Now,
                Session      = new { session.SourceFile, session.MountName, session.CameraName, session.PixelScale },
                Statistics   = session.Statistics,
                FftRaPeaks   = session.FftRa?.SignificantPeaks,
                FftDecPeaks  = session.FftDec?.SignificantPeaks,
                Anomalies    = session.Anomalies
            };
            File.WriteAllText(outputPath, JsonConvert.SerializeObject(export, Formatting.Indented), Encoding.UTF8);
        }
    }
}
