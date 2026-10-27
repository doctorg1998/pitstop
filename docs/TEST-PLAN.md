# Pitstop test plan

## 1. Scope
| In scope | Out of scope (v1.0) |
|---|---|
| Health Check (11 checks), HTML/JSON reports, redaction | Windows 10 (supported by the code, but not tested) |
| Profiles: triggers, priority, hotkeys, actions, revert, crash recovery | ARM64 builds |
| Weekly digest, session log | Multi-user / fast user switching |
| Tray app, live config reload, installer and uninstaller | Localization (English only) |

## 2. Test levels
| Level | What | How | Where |
|---|---|---|---|
| **Unit** | Rules, parsers, config validation, profile engine, digest, file store | xUnit with fixture data and fakes (`FakeSystem`, `FakeClock`, `FakeFileStore`); no real system calls | Any machine, and CI on every push |
| **Integration (live)** | Real Windows APIs: WMI, Event Log, powrprof, Core Audio, psapi, RegisterHotKey | Scripted runs of the Release build on a real PC, checking system state before and after each step | Development PC (see section 4) |
| **Packaging** | Installer, uninstaller, self-contained publish | Silent install, exercise the installed exe, silent uninstall | Development PC, and CI (build only) |
| **Manual** | What automation can't observe: visuals, clicks, real hardware | Checklist in section 5 | Development PC |

## 3. Entry and exit criteria
- **Entry:** the solution builds with 0 warnings.
- **Exit for a release:**
  - All unit tests pass.
  - Pitstop.Core line coverage is at least 90%.
  - The live integration script passes.
  - No open bug of severity Medium or higher.
  - Every manual checklist item is Pass or has an accepted reason.

**Current status (2026-10-27):**
- **Unit tests:** 141 / 141 passing.
- **Coverage (Pitstop.Core):** 97.3% of lines, 88.4% of branches.
- **Open bugs:** 0. BUG-001 to BUG-010 are fixed, see [BUGS.md](BUGS.md).

## 4. Test environment
| | |
|---|---|
| OS | Windows 11 Home 25H2 (build 26200), standard user (not elevated) |
| Hardware | Desktop, 20 logical cores, 32 GB RAM, RTX 3080, 2× NVMe SSD |
| Audio devices | Fosi Audio K5 Pro, Focusrite Scarlett Solo, Realtek, plus 5 virtual devices |
| Network | Wired Ethernet |
| Build | .NET 8.0.427 SDK, Release; installer built with Inno Setup 6.7 |

## 5. Live integration results (run 2026-10-27, Release build)
| # | Scenario | Expected | Result |
|---|---|---|---|
| L1 | Headless Health Check, run 5 times back to back | Stable verdict, under 10 s | ✅ Watch every time, 5.1 s |
| L2 | Report redaction | No PC name or username in the HTML | ✅ 0 occurrences |
| L3 | Trigger app starts (Notepad, QA profile) | Power plan, audio output and closing an app all applied within the poll interval | ✅ Balanced, Realtek, Character Map closed, about 2 s |
| L4 | Trigger app exits | Everything reverted in reverse order; closed app relaunched | ✅ High performance, Fosi K5 Pro, Character Map reopened |
| L5 | Pitstop force-killed while a profile is active | On restart, the revert journal restores the settings | ✅ Plan and audio restored, journal cleared |
| L6 | Break reminder at 1 min | Fires within 10 s of being due | ✅ Fired at 65 s |
| L7 | Valid config edit while running | Reloaded live; next activation uses the new settings | ✅ "Power saver" applied |
| L8 | Invalid config edit while running | Rejected; previous profiles kept; clear log message | ✅ |
| L9 | Global hotkey (Ctrl+Alt+W), simulated key press | Toggles Work on, then off | ✅ Balanced, then High performance |
| L10 | Second launch | Refused with an "already running" message | ✅ |
| L11 | Idle cost, 60 s | Under 1% CPU, under 80 MB | ✅ 0.03–0.10% CPU across runs, 14–18 MB private memory, 56–62 MB working set |
| L12 | Silent per-user install | No admin prompt; Start menu entry; uninstall entry | ✅ |
| L13 | Silent uninstall | Program, shortcut and startup entry removed; user data kept | ✅ |
| L14 | `--diag` support snapshot | All checks pass, nothing changed | ✅ |

## 6. Manual QA checklist
| # | Check | Steps | Expected | Status |
|---|---|---|---|---|
| M1 | Tray icon looks right | Launch Pitstop and look in the tray (click ^ if hidden) | Dark tile with a cyan "P"; turns green while a profile is active | ☐ To do |
| M2 | Tray menu | Right-click the icon | Status line, Run Health Check, Weekly digest, Profiles ▸ (with hotkeys shown), Open folder, Start with Windows, Exit | ☐ To do |
| M3 | Health Check from the menu | Click Run Health Check | Report opens in the browser within about 10 s | ☐ To do |
| M4 | Weekly digest | Click Weekly digest after using a profile | Time per profile and health trend shown | ☐ To do |
| M5 | Break reminder popup | Set `breakReminderMinutes: 1` and activate the profile | Card in the bottom-right corner; doesn't steal focus from a text editor; closes on click or after 15 s | ☐ To do (it fired in L6, but the visuals haven't been reviewed) |
| M6 | Notification mute | With notifications on, activate a profile with `muteNotifications` | Toasts stop; restored on exit | ☐ To do (couldn't be verified on the dev PC, where toasts were already off) |
| M7 | Real hotkey press on the keyboard | Press Ctrl+Alt+P | Play toggles on and off | ☐ To do (L9 used a simulated key press) |
| M8 | Start with Windows | Turn it on in the menu, sign out and back in | Pitstop starts in the tray | ☐ To do |
| M9 | Exit reverts | Activate a profile, then choose Exit | Settings restored; icon removed | ☐ To do |
| M10 | High DPI / second monitor | Run at 150% scaling | Menu and reminder are crisp and correctly placed | ☐ To do |
| M11 | Installer UI (not silent) | Run the setup normally | Modern wizard, tasks unchecked by default, "Launch Pitstop now" works | ☐ To do |
