# K15 Visual Test Rig

Independent development/testing WinForms tool for a webcam aimed at the physical VOROTEX K15. It is **not** a Status Lab production app and is deliberately absent from the RC2 four-app release/packaging contract.

Frame the webcam tightly on the keyboard and avoid putting monitors, documents, people, or other private material inside the camera field of view.

## Build and use

```text
dotnet build status-lab/visual-test-rig/Vorotex.K15.VisualTestRig.csproj -c Release
```

Choose a camera, click **Start Camera**, drag the green keyboard ROI, then use **Capture** or bounded **Capture Burst** (1–10 seconds, 2–20 timer-sampled frames/sec). Capture saves the ROI crop; without an ROI it saves the complete webcam frame and the metadata reports `roi.enabled=false`.

Local-only files are stored under `%LOCALAPPDATA%\VOROTEX\K15 Visual Test Rig\`: `settings.json`, timestamped capture folders, and atomically replaced `latest\latest.png` / `latest\latest.json`. The app makes no network requests, uses no microphone/audio, does not capture the desktop, access HID, alter lighting/profiles, install hooks, or create autostart/background services.

The versioned Windows target exposes the Windows-native MediaCapture enumeration and CPU frame-reading APIs directly; no extra camera package, computer-vision dependency, or classifier is implemented.
