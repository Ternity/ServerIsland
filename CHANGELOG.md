# Changelog

## v0.5.1 — unofficial Windows fix (Ternity fork, 2026-11-09)

Fork of upstream v0.5.0 (`1e6393a7`). Windows-only changes; the update check and issue links now point at this fork.

- **Windows: the island no longer gets stuck behind a maximized window.** The window keeps `WS_EX_TOPMOST` but can
  drop out of the topmost z-order band (seen after the process ran for hours), after which any maximized window
  covers it completely while `IsWindowVisible` is still true — the island "disappears" until the process is killed.
  It is now re-asserted into the band on open, after `GWL_EXSTYLE` changes, on `Screens.Changed`, and by a 2 s watchdog.
- **Windows: double-clicking the exe brings the island back.** The second instance used to exit silently; it now
  signals the running instance through a named event, which re-raises the island.
- **Windows: `SetWindowPos(SWP_FRAMECHANGED)` after `SetWindowLongPtr(GWL_EXSTYLE)`** so the extended style and
  the z-order band stay in sync.
- Version bumped to 0.5.1; update check / issue URLs point at this fork.

## v0.5.0 — 2026-11-08

- **Phone push via WeCom** (Settings → Phone Push, off by default): paste a WeCom group bot's webhook and ServerIsland posts job alerts to your phone — in WeCom, or in WeChat with WeCom's WeChat plugin. By default only starred jobs push, so star the jobs you care about before you step away; you can also push all jobs or follow the island's alert settings, and pick Finished / Failed / Unknown / Started, server warnings and coding-agent alerts. Detailed messages include the job ID, partition, resources, run time against the limit, start and end times, the failure reason, app progress, the folder (last two levels, without your username) and the cost; Brief keeps just the title and server. Alerts within 10 seconds are merged, quiet hours are respected (failures can still go through), and the key lives only in the system keychain / credential store (Windows Credential Manager, Secret Service on Linux). Your phone never connects to your servers, and nothing is sent while the computer sleeps.
- **External display (ESP32)** (Settings → General → Extensions, off by default): mirror job status, alerts, AI usage and coding-agent sessions on a small ESP32 AMOLED screen over USB, or Bluetooth on the Mac and Windows. A new Display page in Settings syncs the board's own settings (face, rotation, brightness, night hours, sounds…), deadlines and weather. The display firmware is not public yet. Linux connects over USB only for now.
- **Coding-agent alerts**: Claude Code and OpenCode (one-click hooks / plugin, existing configuration kept and backed up) and Codex (reads its session logs, nothing to install) — "needs your input" and "done" alerts on the display.
- **DeePMD-kit training progress** (TensorFlow and PyTorch backends): steps from `lcurve.out`, the target from the log's "Start to train N steps" (so `num_epochs` works) or `numb_steps`, energy / force errors in the job row, and an E / F / V loss curve on the details page
- **AI usage** from CC Switch's local database (read-only): today, 7 and 30 days, models and tools.
- **Cost estimates** (Settings → Billing, off by default): set a price per GPU-hour, core-hour or node-hour for each Slurm / PBS partition (or read the partitions from the server), and ServerIsland estimates what each job costs from the resources the scheduler allocated. A coin button in the island header opens a Costs page — today, this month, last month, per day, per partition and the most expensive jobs; job details and History show costs too. Each running job's allocation is queried once (GPUs a job asked for are counted even if the cluster doesn't track `gres/gpu` in accounting); nothing changes on your servers when billing is off. Today and this month include running jobs up to the latest refresh. On the external display, the Info face's fourth card shows today's cost, and server and job pages show costs too. These are estimates — your provider's bill is what counts.
- **Mac: ⌘V, ⌘C, ⌘A and ⌘Z now work in Settings text fields** (the app had no Edit menu, so pasting only worked from the right-click menu); ⌘W closes the Settings window.

## v0.4.0 — 2026-10-21

- **Star jobs**: click the ☆ next to a job's name (it appears on hover), use the right-click menu, or the star on its details page. Starred jobs stay at the top, survive restarts, and are unstarred automatically when they end. Job arrays are starred as a whole.
- **All jobs across servers**: a new list button in the island header opens every server's jobs on one page — search by name, ID, folder or server, filter by status, server or starred only. Back from a job's details returns to the same filtered list.
- **Finer alerts** (Settings → General → Alerts): alert for all jobs (default) or starred jobs only, and turn Started / Finished / Failed / Unknown alerts on or off separately. Muted servers still stay silent; disk alerts and watched processes are not affected by the starred-only setting.
- **Quiet hours** (Settings → General → Alerts, off by default): no sounds or island pop-ups during the hours you pick; system notifications still arrive silently
- **Import / export settings** (Settings → General → Files & Diagnostics): move all your servers and settings to another computer, including between the Mac and Windows / Linux versions. Replacing backs up your current settings first; passwords are never included.
- **TorchNEP and MACE** progress and loss curves; NEP reads the new GPUMD `loss.out` header lines and charge / dipole column layouts, and its curve no longer folds back when a training run is resumed
- **Safer self-updates on Windows / Linux**: a failed swap restores every file, and only the files the update renamed are cleaned up afterwards

## v0.3.0 — 2026-10-19

- **One-click updates**: "Update to v…" downloads the new version, checks its SHA-256 against GitHub, replaces the app and reopens it (macOS, Windows and Linux). Updating from v0.2.0 or earlier still has to be done by hand once.
- **Reorder servers**: drag them in the Settings sidebar or right-click → Move to Top / Up / Down / Bottom; the island uses the same order
- **Terminal button** on every server card in the island: one click opens an ssh session (reusing the kept-alive connection)
- **Lighter on your servers**: one `squeue` / `qstat` call per refresh instead of three, one pass over your processes, one fewer `nvidia-smi`; disk usage is checked every 5 minutes; VASP / LAMMPS / GPUMD / custom-rule folders whose files haven't changed aren't re-read
- **History** kept for 7 days (was 24 hours) with each job's folder, copy and "Open in Terminal"; jobs that ended while ServerIsland was closed are recorded on the next launch; Clear History in Settings
- **Choose the screen** the island shows on (Settings → General)
- **DCU / NPU / MLU** in the partition view; a clear `chmod 600` hint when ssh rejects a private key because of its permissions
- **VASP**: VTST-aware max force; NEB energy profile and barrier
- Faster tooltips; a Quit button in Settings

## v0.2.0 — 2026-10-18

- **Windows and Linux** versions (virtual island at the top of the screen, or a desktop pet; tray icon)
- **PBS clusters** (PBS Pro / OpenPBS / Torque): jobs, arrays, pending reasons, final states, hold-to-cancel with `qdel`
- **More ways to log in**: key, password, key + password, or one-time code; enter the address, user, port and key file without `~/.ssh/config`; jump hosts (multi-hop, each hop asks and reports errors on its own)
- **Remember password** (off by default): stored only in the macOS Keychain / Windows Credential Manager / Linux keyring, used to reconnect automatically
- **Only these folders**: limit a server to jobs and processes under given directories
- **VASP relaxations**: max-force curve next to the energy curve, with the EDIFFG target as a dashed line
- **Diagnostic log retention** (7 days by default) and a Clear Log button
- **Check for Updates** in Settings → General, plus an optional daily check that tells you when a new release is on GitHub

## v0.1.0 — 2026-10-17

First public release.

- One card per server in the notch: running / pending counts, CPU, memory and GPUs; idle servers fold into one line
- Slurm: jobs, job arrays as segmented bars, plain-language pending reasons, free resources per partition, 24-hour history (`sacct` with a local fallback)
- Direct servers: filtered processes, GPU processes, watch any PID
- Progress, speed and ETA for NEP / GNEP, GPUMD, VASP and LAMMPS; loss and energy curves in job details
- Custom progress rules with a read-only Test button
- Alerts with distinct sounds for finished and failed jobs; per-server mute and pause
- Job details with live log tail; opt-in, long-press-and-confirm cancelling with a server-side re-check
- Password / OTP clusters via a native dialog, with connection keep-alive
- English and Chinese UI
