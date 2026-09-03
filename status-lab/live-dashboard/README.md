# VOROTEX K15 Live Dashboard

The dashboard is a local, read-only observer for ordinary Codex Desktop sessions. Status Tray remains the state authority and the dashboard never runs a second reducer over journal input.

It binds loopback only at `http://127.0.0.1:17815/` (override with `K15_LIVE_DASHBOARD_PORT`). It polls the current-user `Vorotex.K15.StatusTray.v1` pipe and tails `%LOCALAPPDATA%\\VOROTEX\\K15 Status Lab\\events.jsonl`, projecting only an explicit safe event allowlist. Prompt/response/command/tool arguments, RPC payloads, credentials, and unknown fields are not exposed.

Development: `dotnet run --project status-lab/live-dashboard/Vorotex.K15.LiveDashboard.csproj`. Publish: `dotnet publish status-lab/live-dashboard/Vorotex.K15.LiveDashboard.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true`.

The MVP has no mutation endpoints or owner controls. It keeps a bounded in-memory history, reconnects SSE automatically, and reports tray offline safely. Hardware state is displayed only from the tray snapshot; no live Codex or K15 acceptance is part of this project.
