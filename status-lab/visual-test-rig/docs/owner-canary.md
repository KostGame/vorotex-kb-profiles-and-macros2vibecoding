# K15 Visual Test Rig v0 — owner canary

1. Connect a webcam and frame **only** the physical K15.
2. Launch `Vorotex.K15.VisualTestRig.exe`.
3. Select the intended camera and choose **Start Camera**.
4. Verify the large preview and `CAMERA ON` indicator.
5. Drag the green ROI around the keyboard and verify its normalized coordinates.
6. Choose **Capture** and check `latest\latest.png` and `latest\latest.json`.
7. Choose **Capture Burst** and wait for `BURST ACTIVE` to return to idle.
8. Verify the timestamped capture folder, `capture.json`, and frame ordering.
9. Keep the app open only if an owner wants Codex Computer Use to inspect the preview; stop the camera/close the app afterwards.

This tool records webcam images only. It does not judge RGB colors or Status Lab states.
