# Bug log

These are real defects found while testing Pitstop on a real PC (Windows 11 Home 25H2, build 26200, RTX 3080, two NVMe SSDs, wired Ethernet). They're written up the way a support ticket would be. Each fix has a regression test.

---

## BUG-001: Startup app names show stray spaces and install hashes
| | |
|---|---|
| **Found** | 2026-10-27, Phase 1, first real Health Check |
| **Severity** | Low (cosmetic, but it makes the report look unprofessional) |
| **Component** | `WindowsSystemProbe.ReadStartupItems` |

**Steps to reproduce**
1. Install Google Chrome and NVIDIA Broadcast, with both set to start with Windows.
2. Run `Pitstop.App.exe --check`.

**Expected:** "NVIDIA Broadcast, GoogleChromeAutoLaunch"
**Actual:** "NVIDIA Broadcast , GoogleChromeAutoLaunch_38791F4ABEA708E49E1BBE279F94BC2B"

**Root cause:** Registry value names are shown exactly as the apps wrote them. NVIDIA's has a trailing space, and Chrome adds a 32-character per-install hash.

**Fix:** Added `Parsers.CleanStartupName`, which trims whitespace and removes a trailing `_` followed by exactly 32 hex characters. Names like `My_App` are left alone.

**Regression test:** `ParserTests.CleanStartupName_trims_and_drops_hash_suffix`, using the real names from this PC.

---

## BUG-002: Driver check warns about virtual devices
| | |
|---|---|
| **Found** | 2026-10-27, Phase 1, first real Health Check |
| **Severity** | Medium (false alarms teach users to ignore the report) |
| **Component** | `WindowsSystemProbe.ReadDrivers` |

**Steps to reproduce**
1. Install Steam, Virtual Desktop or a VPN such as Tailscale.
2. Run the Health Check.

**Expected:** Only physical graphics, audio and network hardware is graded.
**Actual:** 8 "outdated" drivers were flagged. 7 of them were software-only devices, such as Steam Streaming Speakers (2017), Tailscale Tunnel (2021) and PdaNet Broadband Adapter (2017).

**Root cause:** `Win32_PnPSignedDriver` lists virtual devices in the same DISPLAY, MEDIA and NET classes as real hardware. Their driver dates don't reflect anything the user needs to update.

**Investigation:** Listed the `DeviceID` of every vendor driver on this PC. Virtual devices are enumerated under `ROOT\` or `SWD\`. Physical devices sit on a hardware bus (`PCI\`, `HDAUDIO\`, `USB\`).

**Fix:** Added `Parsers.IsVirtualDevice`, and the probe now skips those devices.

**Result:** 1 real finding: Realtek High Definition Audio, driver dated 2024-06-25.

**Regression test:** `ParserTests.IsVirtualDevice_separates_software_devices_from_hardware`, using device IDs recorded from this PC.

---

## BUG-003: A single dropped ping turned the whole report red
| | |
|---|---|
| **Found** | 2026-10-27, Phase 1, repeat runs |
| **Severity** | Medium (the overall verdict flipped between runs on a healthy connection) |
| **Component** | `Thresholds.PacketLossPercent`, `WindowsSystemProbe` |

**Steps to reproduce:** Run the Health Check three times in a row.

**Expected:** The same verdict every time on a stable wired connection.
**Actual:** Run 1 = Watch, runs 2 and 3 = **Action**, with "10% loss to internet".

**Root cause:** With only 10 pings, one lost reply is 10%, which met the old Action threshold of 10%. A single lost packet is normal noise and isn't evidence of a problem.

**Fix:** The test now sends 20 pings, and the thresholds are Watch at 5% or more and Action at 15% or more. One lost reply out of 20 is now Watch, and three are needed for Action.

**Regression test:** `HealthRuleTests.Single_dropped_ping_is_not_action`

---

## BUG-004: Pinging too fast caused false packet loss
| | |
|---|---|
| **Found** | 2026-10-27, Phase 1, while verifying BUG-003 |
| **Severity** | High (it reported a network problem that didn't exist) |
| **Component** | `WindowsSystemProbe.ReadNetworkAsync` |

**Steps to reproduce:** With the BUG-003 fix in place, run the check repeatedly.

**Expected:** 0% loss, since the router showed 0% loss and a steady 8 ms latency.
**Actual:** 20–25% loss to 1.1.1.1 on every run.

**Investigation:** Isolated the variable by testing outside Pitstop:

| Target | Spacing | Lost |
|---|---|---|
| 1.1.1.1 | 100 ms | 4 / 20 |
| 8.8.8.8 | 100 ms | 4 / 20 |
| 1.1.1.1 | 1 s (Windows `ping`) | 0 / 15 |
| 1.1.1.1 and 8.8.8.8 | 250 ms | 0 / 12 each |
| 1.1.1.1 and 8.8.8.8 | 500 ms | 0 / 12 each |

**Root cause:** Public DNS providers rate-limit bursts of ICMP echo requests. At 10 pings per second, Pitstop was measuring Cloudflare's throttling, not the user's connection.

**Fix:** 10 pings each to 1.1.1.1 and 8.8.8.8, run in parallel at 400 ms spacing. That still gives 20 samples, and a hiccup at one provider can't skew the result. The router is pinged 20 times at 200 ms.

**Result:** 5 back-to-back runs, each with 0% loss, a stable verdict and a 5.1 s check time.

---

## BUG-005: Report wording review (7 findings)
| | |
|---|---|
| **Found** | 2026-10-27, Phase 1, while reviewing the generated report as an end user would see it |
| **Severity** | Low to medium (wording and accuracy) |
| **Component** | `Rules.cs`, `ReportWriter.cs` |

| # | Problem | Fix |
|---|---|---|
| 1 | "Free up space on C::" had a double colon, because the drive name already ends in ":" | Reworded to "Free up space on drive C: by …" |
| 2 | The disk summary didn't show the percentage that triggered the warning | Each drive now shows its free percentage, e.g. "(13%)" |
| 3 | The network summary started lowercase ("router 2 ms") | Capitalized the first letter |
| 4 | A desktop with no battery was counted as **Healthy** | Added the `NotApplicable` severity, shown as "N/A" and excluded from the counts |
| 5 | Skipped temperature said "Nothing to do" | It now explains how to include the check (Run as administrator) |
| 6 | A power-loss event got blue-screen advice | When there's no BugCheck event, the advice notes that a power cut or a held power button causes the same event |
| 7 | **The privacy footer said only the latency test sent traffic, but DNS lookups do too** | The footer now names both the DNS lookups and the pings |

Also added a light print theme, so a report can be printed or saved as PDF for a customer.

---

## BUG-006: Duplicate hotkeys weren't detected when written differently
| | |
|---|---|
| **Found** | 2026-10-27, Phase 2, by a unit test written before the feature shipped |
| **Severity** | Medium (two profiles could claim one hotkey, and the second would silently fail to register) |
| **Component** | `ConfigLoader.Validate` |

**Steps to reproduce:** Give one profile `"Ctrl+Alt+P"` and another `"alt+ctrl+p"`.

**Expected:** The config is rejected with "already used by another profile".
**Actual:** It was accepted.

**Root cause:** The duplicate check compared the normalized text ("Ctrl+Alt+P" vs "Alt+Ctrl+P"), and modifier order changes the text.

**Fix:** The check now compares the key combination itself: modifier flags plus virtual-key code.

**Regression test:** `ConfigLoaderTests.Invalid_config_falls_back_with_reason` (the "already used" case)

---

## BUG-007: Break reminders could arrive up to 30 seconds late
| | |
|---|---|
| **Found** | 2026-10-27, Phase 2, live test on a real PC |
| **Severity** | Low |
| **Component** | `TrayHost` reminder timer |

**Steps to reproduce:** Set `breakReminderMinutes: 1`, open the trigger app, and close it after 68 seconds.

**Expected:** A reminder at about 60 s.
**Actual:** No reminder. The 30 s timer last ticked at 56 s, before the minute was up, and the profile ended before the next tick.

**Root cause:** The reminder engine is polled, so its accuracy is limited to the polling interval.

**Fix:** The timer now polls every 10 s. A tick is a cheap clock comparison, and idle CPU stayed at 0.026%.

**Verified live:** The reminder fired at 65 s.

---

## BUG-008: The tray app used 72 MB of private memory (target: under 80 MB total)
| | |
|---|---|
| **Found** | 2026-10-27, Phase 2, 60-second idle measurement |
| **Severity** | Medium (a background app should be close to invisible) |
| **Component** | `Pitstop.App` UI framework |

**Measured (idle, 60 s, Release build):** 72.1 MB private memory, 125.7 MB working set, 22 threads.

**Root cause:** The app loaded **two** UI frameworks:
- WPF, for the app lifetime, the hotkey window and the reminder popup.
- WinForms, for the tray icon, because WPF has no tray icon control.

WPF's rendering stack (PresentationFramework plus native milcore/wpfgfx) costs tens of MB even with no window open.

**Fix:** Moved the tray app entirely to WinForms:
- the app lifetime uses `Application.Run`
- hotkeys use a `NativeWindow`
- the reminder is a borderless `Form` with Windows 11 rounded corners

Core and Windows code were unchanged, and so were all 125 tests.

**Result (same measurement):**

| | Before | After |
|---|---|---|
| Private memory | 72.1 MB | **14.3 MB (−80%)** |
| Working set | 125.7 MB | **56.3 MB (−55%)** |
| Idle CPU | 0.130% of one core | **0.026%** |
| Threads | 22 | 16 |

---

## BUG-009: A rejected config reload claimed to be "using defaults"
| | |
|---|---|
| **Found** | 2026-10-27, Phase 2, reading the activity log after a live test |
| **Severity** | Low (a misleading message during troubleshooting) |
| **Component** | `ConfigLoader`, `TrayHost.ReloadConfig` |

**Steps to reproduce:** While Pitstop is running, save `config.json` with an invalid hotkey.

**Expected:** A log entry saying the change wasn't applied and the previous profiles are still active.
**Actual:** "… Using defaults; your file was saved as config.json.bad." That's wrong, because a live reload keeps the working profiles.

**Root cause:** The loader wrote the consequence into its message, but only the caller knows the consequence (defaults at startup, previous profiles on a reload).

**Fix:** The loader now reports only the problem and the backup file name. Startup adds "using default settings until it's fixed", and a reload adds "your previous profiles are still active".

**Regression test:** `ConfigLoaderTests.Corrupt_json_backs_up_file_and_uses_defaults`
**Verified live:** The log reads "… A copy of the file was saved as config.json.bad. Your previous profiles are still active."

---

## BUG-010: The release package shipped an unused UI framework
| | |
|---|---|
| **Found** | 2026-10-27, Phase 3, by inspecting the first self-contained publish output |
| **Severity** | Medium (download size and disk space for every user) |
| **Component** | `win-x64.pubxml` publish profile |

**Observed:** The publish folder held `wpfgfx_cor3.dll`, `PresentationNative_cor3.dll`, `D3DCompiler_47_cor3.dll` and `PenImc_cor3.dll`, which are WPF's native rendering stack. That's despite the app being WinForms-only since BUG-008. Pitstop.exe was 163.6 MB and the installer was 49.2 MB.

**Root cause:** The self-contained Windows Desktop runtime pack ships WPF and WinForms together, and .NET can't trim WinForms apps automatically. The pack also includes translated resource files for 13 languages, but Pitstop is English-only.

**Fix:**
- Set `SatelliteResourceLanguages=en`.
- Added an MSBuild target that removes WPF's assemblies and native files, plus debug symbols, from the publish list.

**Verified:** The installed build ran every code path without a missing-assembly error: tray startup, hotkey toggle, profile switch, `--check` and `--diag`.

| | Before | After |
|---|---|---|
| Pitstop.exe | 163.6 MB | **107.9 MB** |
| Installer | 49.2 MB | **33.0 MB (−33%)** |
| Portable zip | 67.4 MB | **42.6 MB** |
| Files shipped | 8 | **2** |

---

## BUG-011: Dashboard content could stay invisible
| | |
|---|---|
| **Found** | 2026-10-27, v1.1 dashboard, first preview |
| **Severity** | High (a blank window is the worst first impression) |
| **Component** | `dashboard.html` page transition |

**Observed:** The main content area rendered almost black for several seconds while the sidebar looked normal. Screenshots taken a few seconds apart showed it either way.

**Root cause:** Each page faded in from `opacity: 0`. In a background or hidden page, the browser pauses animations, so the content stayed at the animation's first frame, which was invisible. A dashboard window that opens behind other windows could hit the same thing.

**Fix:** The page transition now moves content but never hides it. A CSS comment records the rule: movement only, never opacity.

**Verified:** The real app window renders immediately (screenshots taken through the app's own WebView2).

---

## BUG-012 to BUG-015: Chart readability (dashboard review)
Found while reviewing every dashboard page at the real window size (1320 × 860), first in demo mode and then in the real app.

| # | Problem | Fix |
|---|---|---|
| 012 | The last date label on the score-history chart was cut off ("Oct 2") | First and last x-axis labels now align inward |
| 013 | The "time per profile" value text overlapped the end of the bar | The label and value columns are measured from the actual text instead of fixed at 120 px |
| 014 | The network axis read "2.4M / 625K", with no units and odd steps such as "13 KB/s / 38 KB/s" | Units added ("40 KB/s", "2 MB/s"); gridlines use 1-2-5 steps; the network axis has a minimum scale of 0–20 KB/s so tiny values don't produce fractional ticks |
| 015 | A session of a few seconds showed as "0 min", and two checks on one day both read "Oct 27" | Durations under a minute read "under 1 min"; same-day checks are labeled with their time |

**Regression tests:**
- `HealthRuleTests.Disk_space_shows_percent_and_has_no_double_colon`
- `Network_summary_starts_with_capital`
- `Desktop_without_battery_is_not_applicable`
- `Temperature_without_admin_is_skipped_and_says_how_to_enable`
- `Power_loss_only_mentions_power_button_case`
- `HealthCheckRunnerTests.Html_footer_discloses_dns_and_pings_and_has_print_theme`
- `Not_applicable_is_labelled_and_not_counted_healthy`
