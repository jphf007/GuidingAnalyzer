using System.Reflection;
using System.Runtime.InteropServices;

// [MANDATORY] The following GUID is used as a unique identifier of the plugin.
// It must NEVER change once the plugin is published.
[assembly: Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567890")]

// [MANDATORY] The assembly versioning — increment for each new release build
[assembly: AssemblyVersion("1.0.0.1")]
[assembly: AssemblyFileVersion("1.0.0.1")]

// [MANDATORY] The name of your plugin (used as the plugin folder name by NINA)
[assembly: AssemblyTitle("GuidingAnalyzer")]

// [MANDATORY] A short description of your plugin
[assembly: AssemblyDescription("Advanced PHD2 guiding log analyzer for N.I.N.A.")]

// Your name
[assembly: AssemblyCompany("JPH")]

// The product name that this plugin is part of
[assembly: AssemblyProduct("GuidingAnalyzer")]

[assembly: AssemblyCopyright("Copyright © 2026 JPH")]

// Standard COM / culture attributes (unused but expected by the template)
[assembly: ComVisible(false)]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

// ── Required metadata for NINA plugin manager ──────────────────────────────

[assembly: AssemblyMetadata("ShortDescription",
    "Advanced PHD2 guiding log analyzer: FFT, anomaly detection, polar alignment, statistics and recommendations.")]

[assembly: AssemblyMetadata("LongDescription",
    "GuidingAnalyzer is a comprehensive PHD2 guiding log analysis plugin for N.I.N.A.\r\n\r\n" +
    "Features:\r\n" +
    "• PHD2 / DEC curves with interactive crosshair and zoom\r\n" +
    "• FFT spectrum analysis (drift-corrected and uncorrected modes) to identify periodic error, backlash and vibrations\r\n" +
    "• Worm gear period estimation with confidence scoring\r\n" +
    "• Multi-session comparison with overlaid FFT spectra\r\n" +
    "• Polar alignment error estimation from drift (Az/Alt in arcmin, screw turns)\r\n" +
    "• RMS vs Altitude scatter plot and sliding RMS trend over a full night\r\n" +
    "• Advanced statistics: skewness, kurtosis, RA/DEC correlation, stability ratio\r\n" +
    "• Automatic anomaly detection (PE, backlash, flexure, poor guide star…)\r\n" +
    "• Prioritized advice list with recommended actions\r\n" +
    "• Full session metadata display (equipment, algorithms, calibration)\r\n" +
    "• CSV and JSON export")]

// ── Recommended metadata ───────────────────────────────────────────────────

[assembly: AssemblyMetadata("License",          "GPL-3.0")]
[assembly: AssemblyMetadata("LicenseURL",       "https://www.gnu.org/licenses/gpl-3.0.html")]
[assembly: AssemblyMetadata("Repository",       "https://github.com/jph/GuidingAnalyzer")]
[assembly: AssemblyMetadata("ChangelogURL",     "https://github.com/jph/GuidingAnalyzer/releases")]
[assembly: AssemblyMetadata("Homepage",         "https://github.com/jph/GuidingAnalyzer")]
[assembly: AssemblyMetadata("Tags",             "guiding,PHD2,analysis,FFT,polar alignment,periodic error")]

// ── Minimum NINA version ───────────────────────────────────────────────────
[assembly: AssemblyMetadata("MinimumApplicationVersion", "3.2.0.9001")]

// ── Optional images (replace with real URLs before publishing) ────────────
[assembly: AssemblyMetadata("FeaturedImageURL",  "")]
[assembly: AssemblyMetadata("ScreenshotURL",     "")]
[assembly: AssemblyMetadata("AltScreenshotURL",  "")]
