# GuidingAnalyzer

[![License: GPL-3.0](https://img.shields.io/badge/License-GPL--3.0-blue.svg)](https://www.gnu.org/licenses/gpl-3.0.html)
[![NINA Version: 3.2.0.9001+](https://img.shields.io/badge/NINA-3.2.0.9001+-green)](https://nighttime-imaging.eu/)
[![.NET 8.0](https://img.shields.io/badge/.NET-8.0-purple)](https://dotnet.microsoft.com/)

**Advanced PHD2 Guiding Log Analyzer for N.I.N.A.**
A comprehensive plugin for analyzing PHD2 guiding logs with **FFT spectrum analysis, anomaly detection, polar alignment estimation, and actionable recommendations** to improve your astrophotography guiding performance.

Version **1.0.0.5** is a locally validated release candidate; see the [audit, corrections and installation instructions](Docs/Validation-1.0.0.5.md). RA and DEC reconstruction are unchanged from 1.0.0.4; RA still matches 1.0.0.2 exactly. In Polar Alignment, enter decimal values with a **dot**, then click **Recalculate** to apply both fields. Polar estimates require known session coordinates and DEC parity.

Thanks to **@ccharhut** for [PR #3](https://github.com/jphf007/GuidingAnalyzer/pull/3), which allows opening PHD2 logs while guiding is still active and ignores an unfinished final data row. See the [1.0.0.5 release notes](Docs/Release-1.0.0.5.md).

Build and test without changing your NINA installation:

```powershell
dotnet test Tests/GuidingAnalyzer.Tests.csproj -c Release
dotnet build GuidingAnalyzer.csproj -c Release
./scripts/Package-Plugin.ps1
```

Packages are written to `artifacts/`. The `preview.zip` contains the compiled plugin and dependencies; `sources.zip` contains the source code. Opt-in local deployment uses `-p:DeployPluginToNina=true`.

---

## 🌟 Features

### 📊 **Core Analysis**
- **PHD2 / DEC Curves**: Interactive visualization with crosshair and zoom for precise guiding error inspection.
- **Sliding RMS Trend**: Track guiding performance over an entire night session.
- **RMS vs. Altitude Scatter Plot**: Identify altitude-dependent guiding issues.

### 🔍 **Advanced Diagnostics**
- **FFT Spectrum Analysis**:
  - Corrections-removed estimates and guided residuals, with RA-only linear detrending; DEC drift is preserved.
  - Full frequency spectra and dominant-frequency tables with period, amplitude, estimated RMS and power share. Possible mechanical origins require confirmation.
  - Analysis of multiple sessions from a PHD2 log, with night-wide diagnostics.
- **Worm Gear Period Estimation**: Automatic detection with confidence scoring.
- **Polar Alignment Error Estimation**: Calculate Az/Alt errors in arcminutes and screw turns.
- **Statistical Analysis**:
  - Skewness, kurtosis, RA/DEC correlation.
  - Stability ratio calculations.

### ⚠️ **Automatic Anomaly Detection**
Detects and flags common guiding issues:
- Periodic error (PE)
- Backlash
- Flexure
- Poor guide star selection
- Seeing conditions
- Mechanical vibrations

### 💡 **Actionable Recommendations**
- **Prioritized advice list** with recommended actions to fix detected issues.
- Clear, step-by-step suggestions for improving guiding performance.

### 📤 **Data Export**
- **CSV Export**: Raw guiding measurements and guide pulses, with millisecond timestamps.
- **JSON Export**: Session identity, statistics, dominant FFT peaks and anomalies.

### 🔄 **Multi-Session Support**
- Select a session from the loaded log.
- Analyze night-wide RMS, altitude dependence and polar-alignment geometry.
- A standalone multi-file comparison service exists in the code, but its controls are not exposed in the current view. PDF report export is also not exposed.

---

## 📥 Installation

### **Via NINA Plugin Manager (Recommended)**
1. Open **N.I.N.A.**
2. Go to **Options → Plugins**
3. Click **"Check for Updates"** or **"Install Plugins"**
4. Find **GuidingAnalyzer** in the list and install it.

### **Manual Installation**
1. **Download** the latest release from [GitHub Releases](https://github.com/jphf007/GuidingAnalyzer/releases).
2. **Extract all files** from the compiled plugin ZIP, including its dependency DLLs, to:



```text
%LOCALAPPDATA%\NINA\Plugins\3.0.0\GuidingAnalyzer\
```

3. **Restart NINA** to load the plugin.

---

## 🚀 Usage

### **Quick Start**
1. **Open NINA** and start a guiding session with PHD2.
2. **After guiding**, open the **GuidingAnalyzer** plugin from the NINA plugins menu.
3. **Load your PHD2 log file** (`.txt` or `.csv` format).
4. **Analyze** the data using the available tools:
- View **guiding curves** (RA/DEC).
- Run **FFT analysis** to detect periodic errors.
- Check **polar alignment** estimates.
- Review **anomaly detections** and recommendations.

### **Detailed Workflow**
1. **Load a Guiding Log**:
- Click **"Open"** and select your PHD2 guiding log file.
- Supported formats: PHD2 `.txt` logs, CSV exports.

2. **View Guiding Curves**:
- Interactive **RA/DEC plots** with zoom and pan.
- **Crosshair tool** for precise measurement of guiding errors.

3. **Run FFT Analysis**:
- Click **"FFT Analysis"** to compute the frequency spectrum.
- Toggle between **drift-corrected** and **uncorrected** modes.
- Identify **periodic error frequencies** (e.g., worm gear period).

4. **Check Polar Alignment**:
- Go to the **"Polar Alignment"** tab.
- View **Az/Alt errors** in arcminutes.
- Get **screw turn recommendations** for adjustment.

5. **Review Anomalies**:
- The **"Anomalies"** tab lists detected issues.
- Each anomaly includes a **severity level** and **recommended action**.

6. **Export Results**:
- Export data as **CSV**, **JSON**, or generate a **PDF report**.

---

## 📦 Requirements

| Requirement | Version | Notes |
|-------------|---------|-------|
| **N.I.N.A.** | ≥ 3.2.0.9001 | Required for plugin compatibility. |
| **.NET Runtime** | 8.0 | Included with NINA. |
| **PHD2** | Latest | Log files must be from PHD2. |
| **Operating System** | Windows 10/11 | NINA is Windows-only. |

---

## 🛠️ Dependencies

This plugin uses the following libraries:
- **[NINA.Plugin](https://github.com/Nighttime-Imaging-NINA/NINA)** (v3.2.0.9001)
- **[NINA.WPF.Base](https://github.com/Nighttime-Imaging-NINA/NINA)** (v3.2.0.9001)
- **[NINA.Equipment](https://github.com/Nighttime-Imaging-NINA/NINA)** (v3.2.0.9001)
- **[OxyPlot.Wpf](https://github.com/oxyplot/oxyplot)** (v2.2.0) – For interactive plots.
- **[MathNet.Numerics](https://numerics.mathdotnet.com/)** (v5.0.0) – For FFT and statistical calculations.
- **[PdfSharp-MigraDoc](https://github.com/empira/PdfSharp-MigraDoc)** (v6.1.1) – For PDF report generation.
- **[CsvHelper](https://joshclose.github.io/CsvHelper/)** (v33.1.0) – For CSV export.
- **[Newtonsoft.Json](https://www.newtonsoft.com/json)** (v13.0.3) – For JSON export.
- **[CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)** (v8.4.0) – For MVVM pattern.

---

## 📂 Project Structure



GuidingAnalyzer/
├── GuidingAnalyzer.csproj          # Project configuration
├── GuidingAnalyzer.sln             # Visual Studio solution
├── plugin.json                     # NINA plugin manifest
├── GuidingAnalyzerPlugin.cs        # Plugin entry point (MEF export)
├── GuidingAnalyzerVM.cs            # Main ViewModel (analysis logic)
├── GuidingAnalyzerView.xaml         # Main UI view
├── GuidingAnalyzerView.xaml.cs      # Main UI code-behind
├── GuidingAnalyzerDockableVM.cs    # Dockable pane ViewModel
├── GuidingAnalyzerDockableView.xaml # Dockable pane UI
├── Properties/
│   └── AssemblyInfo.cs             # Plugin metadata
├── Models/                        # Data models
│   ├── GuidingSession.cs           # Guiding session data
│   └── ...                         # Other models
└── Services/                      # Analysis services
├── GuidingDataService.cs       # Log parsing
├── FftAnalysisService.cs       # FFT calculations
├── AnomalyDetector.cs          # Anomaly detection
├── PolarAlignmentService.cs    # Polar alignment estimation
├── StatisticsService.cs        # Statistical analysis
├── AdviceService.cs            # Recommendation generation
└── ExportService.cs            # Data export

---

## 🤝 Contributing

Contributions are welcome! Please follow these steps:
1. **Fork** the repository.
2. **Create a feature branch** (`git checkout -b feature/your-feature`).
3. **Commit your changes** (`git commit -m 'Add your feature'`).
4. **Push to the branch** (`git push origin feature/your-feature`).
5. **Open a Pull Request** to the `main` branch.

### **How to Help**
- **Report bugs**: Open an issue with a **detailed description** and **sample log file** (if applicable).
- **Suggest features**: Share your ideas in the [NINA Forum](https://groups.io/g/NINA).
- **Improve code**: Submit PRs for bug fixes, optimizations, or new features.

---

## 📜 License

This project is licensed under the **GNU General Public License v3.0** – see the [LICENSE.txt](LICENSE.txt) file for details.


Copyright © 2026 JPH



