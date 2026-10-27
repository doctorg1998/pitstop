# Pitstop: Phase 0 design

Status: **approved 2026-10-27**, including all four deviations in section 2. Changes found during Phase 1 testing are marked with their bug IDs from `docs/BUGS.md`.

## 1. Scope

### Health Check (read-only)
| # | Check | Source | Admin? | Severity rules (OK / Watch / Action) |
|---|---|---|---|---|
| H1 | OS build and last update | `Win32_OperatingSystem`, `Win32_QuickFixEngineering` | No | Last update older than 30 days = Watch; older than 60 = Action |
| H2 | Uptime | `Win32_OperatingSystem.LastBootUpTime` | No | Over 7 days = Watch; over 21 = Action |
| H3 | Crashes (30 days) | System Event Log: Kernel-Power 41, BugCheck 1001, EventLog 6008 | No | 1 = Watch; 2 or more = Action |
| H4 | Disk space | `DriveInfo` | No | Under 15% free = Watch; under 8% = Action |
| H5 | Disk health | `MSFT_PhysicalDisk.HealthStatus` (Storage WMI) | No | Warning = Watch; Unhealthy = Action |
| H6 | Memory and CPU load | `GlobalMemoryStatusEx`, `GetSystemTimes` (3 s window) | No | RAM over 85% = Watch; CPU over 90% sustained = Watch |
| H7 | Startup items | HKCU/HKLM `Run` keys, Startup folders | No (read) | 10 or more = Watch; 20 or more = Action |
| H8 | Driver age (GPU, audio, network) | `Win32_PnPSignedDriver`; physical, non-Microsoft devices only (BUG-002) | No | Over 2 years old = Watch |
| H9 | Network | DNS timing (3 hosts, median); 20 pings to the gateway at 200 ms; 10 each to 1.1.1.1 and 8.8.8.8 at 400 ms (BUG-004) | No | Loss of 5% or more, latency of 80 ms or more, or DNS of 150 ms or more = Watch; loss of 15% or more = Action (BUG-003) |
| H10 | Battery wear (laptops) | `powercfg /batteryreport /xml` | No | Under 80% of design capacity = Watch; under 60% = Action |
| H11 | Temperatures | `MSAcpi_ThermalZoneTemperature` | **Yes** | Shown as "Skipped: needs admin / not exposed by this PC" |

Each finding has a plain-English **what it means** and **what to do**, and every threshold is a constant in a single rules file.

### Profiles
**Triggers:** an app starts or stops (by exe name), or a global hotkey. One profile is active at a time. When several apps match, the highest-priority profile wins; ties go to the most recently started app. When the triggering app exits, Pitstop reverts to the previous profile, or to the baseline if there was none.

**Actions:**

| Action | How | Admin? | Revertible |
|---|---|---|---|
| Power plan | `powercfg /setactive` | No | Yes (stores the previous GUID) |
| Default audio output/input | `IPolicyConfig` COM (undocumented but stable since Windows 7) | No | Yes |
| Mute notifications | HKCU `PushNotifications\ToastEnabled` | No | Yes |
| Close apps | `CloseMainWindow()` only, never a force-kill; only the user's own processes; denylist enforced | No | Shown only (Pitstop can relaunch apps it closed if their path is known) |
| Launch apps | `Process.Start` | No | Optional close-on-revert |
| Break reminder | Tray toast every N minutes | No | n/a |

Session length is logged for every profile activation.

### Weekly digest
Local HTML covering:
- time per profile
- health trend: the last 4 checks
- open Action items

## 2. Deviations from the original prompt (all approved; 5–7 were added during the build)
1. **Process detection.** `Win32_ProcessStartTrace` requires admin. The plan is to diff `Process.GetProcesses()` every 2 s (measured cost is well under 1% CPU), behind an `IProcessWatcher` interface so an event-driven version can replace it later.
2. **Do Not Disturb.** Windows 11 has no supported API for Focus/DND. The plan is to toggle per-user toast notifications instead: same effect, fully documented and revertible.
3. **SMART.** Raw SMART data needs admin. The plan is to use Windows' own disk health verdict (`MSFT_PhysicalDisk`), which works without admin.
4. **Temperatures.** These are reported as skipped unless Pitstop is run elevated. No third-party kernel driver will be used.
5. **WinForms instead of WPF for the tray app** (decided in Phase 2 after measuring, BUG-008). Pitstop has no main window, so WPF's rendering stack was pure overhead. Moving to WinForms cut private memory from 72 MB to 14 MB. All logic lives in Core and is UI-agnostic, so this changed no tests.
6. **v1.1 dashboard window in WebView2** (HTML/CSS with hand-drawn canvas charts, no libraries).
   - **Why:** WinForms can't produce a modern, readable dashboard with charts without heavy custom drawing. WebView2 ships with Windows 11 and matches the Nexus stack.
   - **Lifetime:** the window, its WebView2 processes and the 1-second metrics sampler exist only while it's open. Closing it releases them, and the tray stays light.
   - **Lockdown:**
     - The page is embedded in the exe and served only to its own private origin (`https://app.pitstop`).
     - Every other navigation is cancelled, along with any new window.
     - Dev tools, context menus, zoom and browser shortcuts are off.
     - Every message from the page passes `DashboardCommandParser`: a type allowlist, a 4 KB cap, a profile-id pattern, and an HTTPS link allowlist.
7. **Break reminders use a corner popup, not a Windows notification.** Profiles like Play mute notifications, which would silently swallow the reminder. The popup never takes focus.

## 3. Solution layout
```
Pitstop.sln
src/Pitstop.Core/      net8.0          rules, models, engines, interfaces (no Windows UI)
src/Pitstop.Windows/   net8.0-windows  real probes and actions (WMI, Event Log, powrprof, Core Audio, psapi)
src/Pitstop.App/       net8.0-windows  WinForms tray app; --check and --diag command-line modes
tests/Pitstop.Tests/   xUnit           fixtures + fakes, run on any machine
docs/                  DESIGN, TEST-PLAN, BUGS, CASE-STUDY
installer/             Inno Setup script (per-user, no admin)
```
Core never references Windows APIs directly, so everything in it is testable with fakes.

## 4. Key interfaces (Core)
- `ISystemProbe`: collects a raw `SystemSnapshot`; probe failures are recorded per check and never thrown.
- `IHealthRule`: `Finding Evaluate(SystemSnapshot)`; one rule per check.
- `IProcessWatcher`: emits `ProcessStarted` / `ProcessExited` events.
- `IPowerPlans`, `IAudioDevices`, `INotificationToggle`, `IAppControl`: each action reads current state, applies the change and returns an `IRevertToken`.
- `IClock`, `IFileStore`: so time and disk I/O are fakeable; the file store uses atomic temp-file-plus-rename writes.

## 5. Config (`%LOCALAPPDATA%\Pitstop\config.json`, schema version 1)
```json
{
  "schemaVersion": 1,
  "privacy": { "includeMachineNameInReports": false },
  "profiles": [
    {
      "id": "play", "name": "Play", "priority": 20,
      "triggers": { "processes": ["eldenring.exe", "cs2.exe"], "hotkey": "Ctrl+Alt+P" },
      "actions": {
        "powerPlan": "High performance",
        "audioOutput": "Headset", "audioInput": null,
        "muteNotifications": true,
        "closeApps": ["OneDrive.exe"], "launchApps": [],
        "breakReminderMinutes": 90
      }
    }
  ]
}
```
Config is validated on load. If it's invalid, Pitstop backs up the bad file, loads defaults and shows a tray warning. Unknown fields are ignored, and missing fields fall back to defaults.

**Protected-process denylist** (never closed): `System`, `csrss`, `wininit`, `winlogon`, `services`, `lsass`, `smss`, `explorer`, `dwm`, `svchost`, `audiodg`, `MsMpEng`, and Pitstop itself.

## 6. Privacy
- No telemetry and no accounts.
- The only outbound traffic is in H9, and it runs only when you start a Health Check:
  - DNS lookups of www.microsoft.com, www.cloudflare.com and www.wikipedia.org
  - pings to your router, 1.1.1.1 and 8.8.8.8
- Reports replace the username and machine name with `<user>` / `<pc>` unless you opt in.

## 7. Phase plan
| Phase | Delivers | Done when |
|---|---|---|
| 0 | This design and a building scaffold | `dotnet build` and `dotnet test` pass |
| 1 | Health Check engine: 11 checks, rules, HTML/JSON report | Rule tests pass on fixtures; real report generated on this PC |
| 2 | Profiles engine, process watcher, revert, tray UI | Engine tests pass (including the overlap, revert and denylist cases); manual run on this PC |
| 3 | Installer, CI workflow, README, TEST-PLAN, BUGS | Installer runs without admin; CI YAML validated |
| 4 | Case study, portfolio section, resume/LinkedIn copy | Uses only measured numbers |

## 8. Measured results (Release build, real PC: Windows 11 Home 25H2, 20 logical cores, 32 GB)
| Metric | Target | Measured |
|---|---|---|
| Health Check duration (11 checks, run in parallel) | — | 5.1 s (5 consecutive runs) |
| Idle CPU (tray app, 60 s) | under 1% | **0.026–0.104% of one core** (dev build / installed release build) |
| Private memory (idle) | under 80 MB | **14.3–18.2 MB** |
| Working set (idle) | under 80 MB | **56.3–62.2 MB** |
| Installer / portable zip | — | 33.0 MB / 42.6 MB, self-contained (BUG-010); v1.1 with dashboard: 33.3 MB / 43.1 MB |
| v1.1 dashboard open | — | 6 WebView2 processes, about 297 MB private in total (typical for Edge WebView2) |
| v1.1 dashboard closed | under 80 MB | WebView2 processes 0; tray 20.6 MB private, 0.05% CPU |
| Process-watcher poll (245 processes) | — | 12–14 ms wall time, mostly waiting; under 1 ms CPU per poll |
| Profile switch latency after the app starts or exits | — | about 2 s (the poll interval) |
| Automated tests | — | 141 / 141 passing; Pitstop.Core coverage 97.3% lines, 88.4% branches |

## 9. Known limitations
- **A session in progress when Pitstop crashes isn't logged.** The settings are restored, but that session's time is lost from the digest.
- **App closing is polite.** An app that ignores WM_CLOSE (some tray apps) is reported as "left running", never force-killed.
- **The notification mute couldn't be verified on the development PC,** because toasts were already off there (ToastEnabled = 0), so the action was a correct no-op. It's on the manual QA checklist.
- **The audio switch uses `IPolicyConfig`,** an undocumented interface (the same one the Sound control panel uses). It's verified on Windows 11 25H2, and the code is isolated behind `IAudioDevices`.
- **The dashboard needs the WebView2 Runtime.** Every Windows 11 PC has it. If it's missing, Pitstop says so and keeps working from the tray.
- **PID reuse within one 2 s poll window could hide a restart.** In practice this needs an exit and a new start that reuses the same PID inside 2 s.

## 10. Environment notes
- The .NET 8 SDK (8.0.427) is installed.
- Git 2.56 and Inno Setup 6.7 were installed on 2026-10-27 through winget (installer hashes verified). Their Authenticode signatures are valid: Johannes Schindelin (Git for Windows) and Pyrsys B.V. (Inno Setup).
