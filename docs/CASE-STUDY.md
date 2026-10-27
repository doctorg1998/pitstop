# Case study: Pitstop

**Role:** Creator, QA lead, and AI-assisted developer · **Timeline:** October 2026 · **Stack:** C# / .NET 8, WinForms, Win32 interop, xUnit, Inno Setup, GitHub Actions

## The problem
Two things I do every day, by hand:

1. **Diagnosing a slow or unstable PC.** At Geek Squad and Pearson VUE, and now for family and freelance clients, the first 10 minutes are always the same: check updates, uptime, crash history, disk space, drivers and network. Then explain the results in plain language. Built-in Windows tools scatter that information across a dozen screens.
2. **Switching my own PC between modes.** Gaming wants the high-performance power plan, my headset and no notifications. Recording wants my audio interface. Work wants the normal settings back. Doing that by hand means it often doesn't get done, or doesn't get undone.

**Users:**
- Me.
- The relative who calls when "the computer is slow". They need a report they can read.
- A support tech who needs the same facts fast.

## What I built
A Windows 11 tray app with two features:

- **Health Check.** 11 read-only checks run in parallel in about 5 seconds. Each result is rated OK, Watch or Action, with *what it means* and *what to do*, written the way I'd explain it at the counter. The report is a shareable HTML page, and the PC name and username are redacted by default.
- **Profiles.** Pitstop switches power plan, default audio devices and notifications, and closes or opens apps, when a chosen app starts or a hotkey is pressed. **Everything is undone automatically** when the app closes. Break reminders and a weekly digest round it out.

## Key decisions and trade-offs
| Decision | Why | Trade-off |
|---|---|---|
| **No admin rights, ever** | A tool for non-technical users shouldn't need a security prompt | Process detection polls every 2 s instead of using instant alerts (those need admin); temperatures are skipped unless the user runs Pitstop as administrator |
| **Revert journal on disk** | If the app crashes mid-profile, the PC must not stay stuck in "gaming mode" | One small file write per profile switch |
| **Polite app closing only** | Force-killing can lose a user's unsaved work | Apps that ignore a close request are reported, not killed |
| **Corner popup for break reminders** | The Play profile mutes notifications, which would silently swallow its own reminders | One extra small window class |
| **WinForms instead of WPF** (changed mid-project) | Measured: the tray app has no main window, so WPF's rendering stack was pure overhead | Less UI tooling, which a tray app doesn't need |
| **All logic in a platform-free Core library** | Every rule and decision can be tested on any machine, with fakes for Windows | More interfaces up front |

## How I tested it
I treated QA as a feature, not a final step.

- **141 automated tests,** with 97.3% line and 88.4% branch coverage of the Core library. Parsers are tested with device IDs and names recorded from a real PC.
- **Live integration runs** on real hardware. Each scripted step checks the real system state before and after:
  - power plan, default audio device and running apps
  - force-killing Pitstop mid-profile, then recovering
  - live config reload
  - a simulated global hotkey
  - silent install and uninstall
- **A written test plan** with exit criteria, plus a manual checklist for what automation can't see.
- **A bug log written as support tickets:** 10 real bugs, each with repro steps, root cause, fix and regression test.

## Three bugs that taught me something
- **The connection was fine; the measurement wasn't (BUG-004).** Pitstop reported 20–25% packet loss on a wired connection with 0% loss to the router. I isolated the variable by pinging two providers at four different intervals. Loss was 4 in 20 at 100 ms spacing and 0 at 250 ms. Public DNS servers throttle bursts of pings. The fix changed how Pitstop measures, not the threshold.
- **False alarms are bugs too (BUG-002).** The driver check flagged 8 "outdated" drivers. Seven belonged to virtual devices (Steam Streaming Speakers, a VPN tunnel). Comparing device IDs showed that software devices are enumerated under `ROOT\` and `SWD\`, while real hardware sits on PCI, HDAUDIO or USB. One real finding remained.
- **Measure before optimizing (BUG-008 and BUG-010).** The tray app used 72 MB of private memory. The cause was a second UI framework that a windowless app didn't need. Removing it cut private memory to 14 MB (−80%). Removing it from the package cut the installer from 49 MB to 33 MB.

## Results
| | |
|---|---|
| Health Check | 11 checks in 5.1 s, stable verdict across 5 consecutive runs |
| Idle cost | 0.03–0.10% of one CPU core, 14–18 MB private memory |
| Profile switch | About 2 s after the trigger app starts or exits; fully reverted, including after a forced crash |
| Install | Per-user, no admin prompt, 33 MB self-contained installer; clean uninstall |
| Quality | 141 / 141 tests passing, 0 open bugs |

## How I worked with AI
I wrote the design (scope, constraints, interfaces, phase plan) and directed Claude as the coding agent. I made the calls on every trade-off above. I also required real measurements before any claim went into the docs.

AI wrote the first draft of the code. The tests, the live checks on real hardware and the bug log decided what shipped. Every number in this case study was measured on a real PC.

## What I'd do next
- A settings window, so non-technical users never edit JSON.
- A signed installer, to remove the SmartScreen warning.
- Optional event-based app detection when Pitstop already runs as administrator.
- Testing on Windows 10 and on laptops (battery and thermal paths).
- Finishing the manual QA checklist (M1–M11) on a second PC.
