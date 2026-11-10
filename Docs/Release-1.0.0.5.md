# GuidingAnalyzer 1.0.0.5 — Reliability update

## Thanks to our contributor

Special thanks to **@ccharhut** for [pull request #3](https://github.com/jphf007/GuidingAnalyzer/pull/3): **opening PHD2 guide logs while they are still being written**. GuidingAnalyzer can now read a snapshot of an active guiding run without a file-sharing error. An unfinished final data row is ignored. This contribution includes a regression test and has been integrated with the other fixes below.

## Fixes and improvements

- Correctly read negative calibration angles instead of displaying zero.
- Avoid false DEC backlash alerts caused by zero-duration pulses; distinguish real pulse reversals from dithering and interrupted measurements.
- Require actual corrective pulses before reporting possible over-correction.
- Keep sliding RMS windows within a valid, uninterrupted segment of one session. Use a consistent RMS definition and support latitude estimation in both hemispheres.
- Use screened FFT peaks for candidate mechanical periods instead of selecting arbitrary noise bins.
- Handle failed calibrations, truncated session headers and invalid measurements more reliably.
- Clear stale results when loading an empty log, and clear previous confidence/amplitude values for short sessions.
- Preserve millisecond timestamps in CSV exports and identify the selected session in JSON exports.
- Improve selected-tab readability and prepare all analysable sessions before computing comparison data.
- Check the permanent plugin identity, version and final ZIP checksum when preparing the N.I.N.A. catalog manifest.

## Preserved behaviour

- RA and DEC reconstructed curves are unchanged from version 1.0.0.4.
- RA reconstruction still matches version 1.0.0.2.
- Drift Correction applies only to RA.
- Polar Alignment uses decimal-point input and applies both parameters when Recalculate is clicked.
- The plugin identifier remains unchanged.

## Validation and installation

54 automated tests pass, including @ccharhut's active-log test. The previous audit also replayed a real log containing 16 sessions and 7,659 frames, verified RA/DEC reconstruction point by point, and rendered all seven WPF tabs without binding errors.

Close N.I.N.A., back up the existing plugin folder, and extract the **entire contents** of `GuidingAnalyzer-v1.0.0.5-preview.zip` into `%LOCALAPPDATA%\NINA\Plugins\3.0.0\GuidingAnalyzer\`.

Reconstructed motion and mechanical diagnoses remain estimates. A final check of the candidate loaded in N.I.N.A. is still required before publishing the release; automated WPF tests run outside the N.I.N.A. application.
