# `PublishReadyToRun` — evaluation

Date: 2026-10-15
Status: **closed — not adopted** (user, 2026-10-15). Measured, recommended against, branch deleted.
Nothing from it shipped; this file is the record so the lever is not re-chased.
Follows the perf pass recorded in `CLAUDE.md`'s roadmap and `docs/architecture.md` § `RazerDevice.cs`.

## 1. Question

The perf pass that merged on 2026-10-15 (`3582dc9`) attacked device-read latency and UI-thread host
calls. It deliberately left **process start** alone: the gap between launching the exe and the tray
icon showing a battery percentage. ReadyToRun precompiles IL to native code at publish time, which
is the standard lever for that gap, paid for with a larger published binary.

Is it worth adopting here?

**Decision bar set with the task:** a cold-start win under roughly 50 ms is not worth a larger
binary.

### Read this first: the deciding number was not the one being measured

This was framed as a startup-time question with a size cost, and on those terms it is a near miss —
25.5 ms to the tray icon, 49.0 ms to the first percentage, both under the bar, so it would have
failed anyway. But what actually settles it is **idle private working set, up 12.3% against hard
rule 1's ~23 MB gate** (§6). That was expected not to move at all, and the brief said to verify
rather than assume, which is the only reason it was measured. Anyone re-opening this should start at
§6, not §4: the milliseconds are the interesting part and the megabytes are the answer.

## 2. The signal, and why this one

The app is windowless. There is no main window handle to poll, no console to print to, and the tray
icon is owned by the shell, so no external observer sees the same instant before and after a build
change. The only honest observer is the app itself, so this pass added
`Diagnostics/StartupTrace.cs` — three marks, identical in both arms:

| Mark | Fires | Means |
| --- | --- | --- |
| `main` | first line of `Program.Main` | host startup + runtime init is done |
| `tray` | after `_tray.Show()` in `AppHost.Start` | the shell has the icon: **the user sees the app** |
| `battery` | first `TrayIconController.Update` with `DeviceStatus.Online` | **the first real percentage is on screen** |

Each mark is measured from the **OS process-creation time** (`GetProcessTimes`), not from a
stopwatch started in `Main`, so the figure covers everything the user waits through — host startup,
assembly loading, JIT, and the app's own work. `GetSystemTimePreciseAsFileTime` shares that epoch
and resolves well under a millisecond, which the ~15.6 ms system clock tick would not.

Gated on `NAGA_STARTUP_TRACE=1`, reporting `label.mark=ms` lines to `NAGA_STARTUP_TRACE_OUT`,
exactly as `PerfBench` is gated on `NAGA_PERF=1`. Disabled it is one static bool test per mark and
no I/O.

**Caveat on the absolute figures.** The `tray` and `battery` numbers include the trace's own first
`File.AppendAllText` (first-use file I/O initialisation), which an uninstrumented build never pays.
That constant is present in both arms, so it inflates the absolutes by a few ms and leaves every
comparison below untouched.

## 3. Method

1. One clean worktree, one source tree, two publishes from it: `scripts/install.ps1`'s own
   configuration (`-c Release`, self-contained single-file `win-x64`) with `PublishReadyToRun`
   off, then on. Both outputs copied to side-by-side folders — same code, same SDK, one variable.
2. **Interleaved A/B, 40 rounds.** Each round runs both builds, and the order flips every round, so
   machine drift and background load land on both arms instead of on one. Per sample: kill any
   running instance, wait for exit, launch, wait for that sample's `battery` mark.
3. Reported **paired**: the per-round difference `r2r − base`, then its median and quartiles. Pairing
   is the point — both builds met the same machine conditions in each round, which two separate
   medians cannot claim. `r2r_faster` is a sign test over the 40 rounds.
4. Idle footprint measured separately per build: launch, settle 45 s (past the first battery polls),
   then sample private working set every 5 s for 120 s and take the process's CPU time across that
   window. Dashboard closed throughout.

**Warm file cache.** Samples run back to back, so the exe is in the page cache. See §7 — this
flatters R2R rather than the reverse.

## 4. Result — cold start

40 paired rounds, ms from process creation. Negative `d` = R2R faster.

| Mark | base median | R2R median | `d` p25 | **`d` median** | `d` p75 | R2R faster in |
| --- | --- | --- | --- | --- | --- | --- |
| `main` | 44.5 | 55.9 | +8.0 | **+10.8** | +15.2 | 3/40 |
| `tray` (icon visible) | 383.9 | 354.1 | −41.1 | **−25.5** | −15.7 | 33/40 |
| `battery` (first %) | 591.0 | 530.8 | −64.6 | **−49.0** | −33.1 | 36/40 |

Split into segments (median of per-round segment lengths):

| Segment | base | R2R | `d` |
| --- | --- | --- | --- |
| process start → `Main` | 44.5 | 55.9 | **+10.8** |
| `Main` → tray icon | 340.2 | 298.4 | −36.2 |
| tray icon → first % | 205.2 | 178.0 | −27.2 |

Read that as: R2R removes about **63 ms of JIT** from the app's own work, and **gives 10.8 ms of it
back before `Main`** — the 59 MB larger image costs real time to map even with a warm cache, in 37
of 40 rounds. Net at the icon: **25.5 ms**. Net at the first percentage: **49.0 ms**.

An earlier, non-interleaved pass (15 samples each, separate publishes) agreed in direction and
magnitude — tray 404.6 → 391.7, battery 599.5 → 569.9 — but it is superseded: it was published from
a checkout another session was editing concurrently, so its two arms cannot be shown to have come
from the same source.

## 5. Result — published size

| | base | R2R | delta |
| --- | --- | --- | --- |
| `NagaBatteryTray.exe` | 198,598,178 B (189.40 MiB) | 260,948,563 B (248.86 MiB) | **+59.46 MiB, +31.4%** |
| whole shipped set | 206,813,658 B (197.23 MiB) | 269,164,043 B (256.69 MiB) | +59.46 MiB |
| file count | 6 | 6 | — |

Both sets ship the exe plus all five `*_cor3.dll` WPF natives beside it
(`D3DCompiler_47`, `PenImc`, `PresentationNative`, `vcruntime140`, `wpfgfx`), so R2R does not
disturb the single-file packaging rule that `install.ps1` documents.

Publishing also gets slower, because crossgen2 runs over the whole self-contained framework, not
just this app's assemblies. That is a dev-loop cost on every `install.ps1`.

Adopting it would also pull in a documentation edit under hard rule 4: the size appears in
`README.md` twice (~196 MB on disk, ~188 MB exe) and in `install.ps1`'s doc comment, and all three
would become ~257 MB / ~249 MB. Nothing *visual* changes, so no screenshots would need
regenerating.

## 6. Result — idle footprint (hard rule 1)

Hard rule 1 gates the app at ~0% idle CPU and ~23 MB private working set. R2R should not move
either, but the rule says verify, not assume. Dashboard closed, 24 samples over 120 s after a 45 s
settle:

| | base | R2R | delta |
| --- | --- | --- | --- |
| **private working set, mean** | **24.03 MB** | **26.98 MB** | **+2.95 MB, +12.3%** |
| private working set, min / max | 23.89 / 24.52 MB | 26.92 / 27.06 MB | — |
| peak working set | 94.84 MB | 105.55 MB | +10.71 MB |
| CPU over the 121 s window | 0.016 s | 0.016 s | none |
| idle CPU, as % of one core | 0.013% | 0.013% | none |
| threads / handles | 10 / 532 | 10 / 511 | — |

**Idle CPU does not move**, which is what was expected: R2R changes how code is compiled, not what
runs. Both arms sit at 0.013% of one core, which is the "~0%" the rule asks for.

**Private working set does move, and this is the finding.** R2R costs **+2.95 MB of private
working set at idle, a 12.3% increase on a gate written as ~23 MB** — it puts the steady state at
27.0 MB. That is not noise: the two arms' ranges do not overlap (24.52 max against 26.92 min across
20 samples each). The cause is structural rather than incidental — a ReadyToRun image carries
precompiled native code whose fixups are applied at load, so those pages become private
copy-on-write, and there are 59 MB more of them to map.

Both arms were measured with the **dashboard never opened**, which is the app's own baseline state.
That is a different state from the post-close trim (where the dashboard has been opened and closed
and the double-GC-plus-trim has run), so these figures are comparable to each other and should not
be compared against a post-close number.

## 7. Verdict — do not adopt

The win is **25.5 ms to the tray icon and 49.0 ms to the first battery percentage**. Both are under
the ~50 ms bar, the second only just — and the bar turns out not to be what decides it, because the
cost side is worse than "a larger binary" implied.

1. **It regresses a hard rule.** Hard rule 1 gates this app at ~23 MB private working set. R2R puts
   it at 27.0 MB, and keeps it there for the whole life of a process that runs all day. That is the
   opposite of the trade this project makes everywhere else: the post-close GC/trim exists
   *specifically* to claw working set back, and this would hand back a slice of what that trim is
   worth, permanently, to save tens of milliseconds once. Startup time is not a gated number.
   Private working set is.
2. **The cost is also 59.5 MiB of binary, +31.4%**, plus crossgen2 on every publish and the
   README/`install.ps1` size edit that hard rule 4 would require.
3. **Part of the win is already spent.** +10.8 ms before `Main`, in 37 of 40 rounds — the larger
   image costs real time to map even with a warm cache.
4. **The measurement flatters R2R.** These are warm-cache launches. The app's real start is once per
   login, off disk, cold — and R2R adds 59.5 MiB to that read. True cold start was not measured
   (that needs reboots or standby-list flushing), but the warm pre-`Main` regression in point 3 is
   direct evidence the direction is unfavourable there.
5. **Nobody is watching when it happens.** Run-at-login is a *deliberately delayed* scheduled task —
   a 1-minute logon delay, for the Smart App Control reason in `StartupRegistration.cs`. The saving
   lands once a day, a minute into a session, in a tray the user is not looking at. An interactive
   relaunch is rare by comparison.

Summed up: 25–49 ms once a day where no one is looking, bought with 2.95 MB of permanently resident
private memory against a 23 MB gate, 59.5 MiB of disk, and a slower publish. Not worth it.

`PublishReadyToRunComposite` was not pursued: it trades strictly more size for more startup, so it
loses the same argument by a wider margin.

### What the measurement did find

The app's own work from `Main` to the tray icon is **~340 ms**, of which only ~63 ms is JIT. The
other ~280 ms is real work on the UI thread before `_tray.Show()` — merging the WPF-UI themes and
controls dictionaries plus `DesignSystem.xaml`, `ApplicationThemeManager.Apply`, `ThemeManager.Apply`,
then constructing the device, monitor and tray icon. **If startup ever becomes a target, that is the
lever, and it costs no bytes.** It is not a target today, and hard rule 1 plus YAGNI say leave it.

## 8. Tests

`dotnet test` on the evaluation branch: **375 passed, 0 failed, 0 skipped** (9 s).

That is green, but it is worth being exact about what it proves. `dotnet test` builds **Debug**, and
the Release-only property group is where `PublishReadyToRun` lives, so the suite exercises the
instrumentation and nothing about R2R. R2R is a publish-time setting; the only things that can
check it are the published build's own behaviour — which is what §4 and §6 measure — and the fact
that the published app ran, read the mouse and rendered a percentage in all 40 R2R samples.

## 9. What became of the work

The measurement ran on `perf/ready-to-run-eval`, which was **deleted on the user's instruction
once this file existed** — nothing was merged and nothing was pushed. This file is the only thing
kept. The deleted commits, should the reasoning ever need re-reading inside the gc window (~30
days):

| Commit | Contents |
| --- | --- |
| `7be6c2e` | `Diagnostics/StartupTrace.cs` + three call sites — the measurement instrument |
| `7e517f2` | `<PublishReadyToRun>true</PublishReadyToRun>` — the subject |
| `7a31ce1`, `c4bce43` | this file |

Branch tip was `c4bce439063a851e265755114a10b15588a00630`, so
`git branch perf/ready-to-run-eval c4bce43` brings it back while the objects survive.

**`StartupTrace` was deliberately not kept.** It was ~70 lines and inert when disabled, and the
argument for keeping it is the one that justifies `PerfBench` — the next startup question would
start from a measurement instead of a guess. It went anyway, because startup is not on the roadmap
and an instrument nobody runs rots. §2 and §3 describe it precisely enough to rebuild in an hour if
that changes: three marks, each timed from `GetProcessTimes`' creation time against
`GetSystemTimePreciseAsFileTime`, written to a file behind an env gate.

## 10. Machine state during the evaluation — on the record

The first measurement pass used `scripts/install.ps1`, which does not only publish: it installs to
`%LOCALAPPDATA%\Programs\NagaBatteryTray`, re-registers the logon task and launches the exe. So the
everyday app and the logon task pointed at an **experimental build for about 21 minutes** — an
instrumented non-R2R build from 10:51:55, then the R2R build, until the restore at 11:13:23. Nothing
was lost by it (settings live in `%APPDATA%` and were untouched), but it should not have happened,
and it is the reason the second pass never went near the installed app: both arms published to
side-by-side scratch folders and were measured out of those.

**Restored.** At 11:13:23 the installed exe was made byte-identical to a fresh publish of
`3582dc9` — 198,598,178 bytes, SHA-256 `A5C3E4C9EEE1DA6E…`, against `7A63BB1E…` for the
instrumented build and `4F74EE8A…` for the R2R one — with the logon task `Ready` and pointing at
it. It was replaced again at 11:17:00 by the clear-preferred-mouse pass installing its own build,
which is that pass's own acceptance step; the claim being recorded here is only that nothing from
this evaluation stayed installed. The instrumented build happens to be the same byte *length* as
master's, so length alone does not identify a build — hash it.

The lesson for the next startup question: **measure out of publish folders, never through
`install.ps1`.** `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" publish src/NagaBatteryTray -c Release`
gives the same binary without touching what the user is running.

### Reproducing

1. `NAGA_STARTUP_TRACE=1`, `NAGA_STARTUP_TRACE_OUT=<file>`, `NAGA_STARTUP_TRACE_LABEL=<sample>`.
2. Publish both arms from one tree into two folders; never compare across two source states.
3. Interleave the arms round by round, flipping the order each round, and report the paired
   per-round difference. Twenty rounds was not enough here — the quartiles only settled at forty.
