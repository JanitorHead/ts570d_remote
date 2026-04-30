# ts570d_remote

Kenwood TS-570D remote control software for Windows using WPF and OmniRig CAT.

**Latest release:** `v0.3.0` (Git tag/release).  
**Project file version:** `0.2.0` in `TS570_Remote.csproj` (not yet bumped to match the release tag).

**Disclaimer:** This repository is **vibe coded** (AI-assisted, exploratory hacking) and is **only an experiment**. It is not production software, not audited for safety or correctness, and not a substitute for the manufacturer’s tools or documentation.

---

## Project status / how to help

**IF YOU KNOW C#, WPF, AND XAML: IT WOULD BE GREAT IF YOU COULD HELP OUT — OR FORK THIS AND RUN WITH IT YOURSELF.** The goal is not to leave this as eye candy: this tool should be **actually useful** and **usable for real** in the shack.

**Work in progress — honest expectations:** this repo is still **unfinished**, but it has moved forward significantly: CAT sync, meter handling, startup bootstrap, TX power editing flow, and display color customization are now in place. It is still not production-grade software.

**Where things stand:** the UI already “lands”, but from here on it needs **hands-on work** in the code. **XAML** is where AI assistance falls short (fine layouts, styles, alignment, keeping the front panel maintainable), so it helps to know the stack — or at least have patience for manual polish.

**CAT / OmniRig:** many key functions are now wired and usable, but CAT coverage is still incomplete and some paths still need verification against the physical TS-570D front panel behavior.

**Network / true remote:** **there is no server yet** to put the radio on the network and drive it from another PC or from outside the house. Today this is **local control** (Windows + OmniRig per your setup), not a LAN/Internet remote-control product.

If this sounds like your thing: open an issue, send a PR, or fork and go.

---

## What it is

A Windows desktop remote-control style UI for the **Kenwood TS-570D** HF transceiver, built with **WPF** and **OmniRig** for CAT. Treat it as a toy, not a guaranteed rig-control solution.

![Remote UI mimicking the TS-570D front panel](docs/screenshot.png)

### Inspiration

The idea comes from **Hans, DK9BP** — a fellow amateur who offered **TS-570 control software** from his website. He is now, sadly, **silent key**. His site is **no longer online**, so the original download is gone; what remains is mainly material on his **YouTube channel**.

If you want to see **how that program behaved in practice**, watch this recording on YouTube: https://www.youtube.com/watch?v=DLC3zaSg2NM

## Requirements

- **OS:** 64-bit Windows (WPF; `net10.0-windows`).
- **.NET 10 SDK** — the project targets .NET 10; install the matching SDK from [https://dotnet.microsoft.com/download](https://dotnet.microsoft.com/download) (see `TargetFramework` in `TS570_Remote/TS570_Remote.csproj`).
- **OmniRig** installed and configured for your rig at runtime (Rig1 expected by default).

## Build

From the **repository root** (where this `README.md` and `TS570_Remote.slnx` are):

**Restore and compile** (Debug):

```powershell
dotnet build TS570_Remote.slnx
```

The solution under `TS570_Remote/TS570_Remote.sln` is equivalent; the VS Code tasks in `.vscode/tasks.json` use that path.

**Run** the app (after a successful build):

```powershell
dotnet run --project TS570_Remote/TS570_Remote.csproj
```

**Release** output:

```powershell
dotnet build TS570_Remote.slnx -c Release
```

**Publish** (for example a folder of binaries you can copy elsewhere):

```powershell
dotnet publish TS570_Remote.slnx -c Release
```

Default build output is under `TS570_Remote/bin/<Configuration>/net10.0-windows/`. In VS Code you can also use the **build**, **publish**, and **watch** tasks from `.vscode/tasks.json` (F1 → *Tasks: Run Task*).

**Debugging:** `.vscode/launch.json` is set up to launch `TS570_Remote/bin/Debug/net10.0-windows/TS570_Remote.dll` with the *build* task as a pre-launch step.

## Current feature status

### Working (implemented and generally usable)

- [x] **CAT connection via OmniRig (Rig1)** with recurring sync loop.
- [x] **Startup bootstrap from rig state** before normal command push (reduces unsafe/default overwrites).
- [x] **VFO A/B frequency readback and tuning** (main VFO + MULTI/CH where applicable).
- [x] **Band up/down and mode switching** (wired CAT paths for active mode controls).
- [x] **TX/RX state and key status indicators** with LCD badge updates.
- [x] **TX power editor mode (`PWR`)** with 5 W step control and bounds normalization.
- [x] **Meter activity pipeline** (RX S-meter and TX PWR/SWR/COMP/ALC read paths, with visual bars and legends).
- [x] **ACC2 monitor audio path** (input/output device selection + monitor toggle).
- [x] **MIC knob driving Windows TX playback endpoint volume** (separate from monitor level).
- [x] **Display color tool** (combined picker + hue + RGB + HEX + presets + persistent save).
- [x] **Theme-aware LCD badges/text** reacting to active display color.

### Partially working / needs validation

- [~] **Meter calibration** is improved but still needs rig-by-rig validation for exact correspondence.
- [~] **Some CAT commands** are wired but not fully tested in all operating scenarios and modes.
- [~] **Visual parity with the physical front panel** is close, but still not exact in all details.

### Not implemented or known limitations

- [ ] **No LAN/Internet remote backend/server** yet (this is local Windows control only).
- [ ] **No CAT command to force-open the physical radio’s front-panel menus** (e.g., remote `PWR` does not open the rig UI menu screen).
- [ ] **Full TS-570 command coverage** is not complete yet; some front-panel functions remain unimplemented.
- [ ] **Robust production hardening** (full error recovery, exhaustive edge-case handling, long-run stability testing) is still pending.

## Roadmap (next high-impact tasks)

- [ ] Complete CAT command coverage and verify each path against the TS-570D manual.
- [ ] Continue meter calibration and smoothing with real-world RF checks.
- [ ] Finish remaining front-panel controls and LCD state parity.
- [ ] Improve resilience (disconnect/reconnect handling, clearer diagnostics, safer fallbacks).
- [ ] Add true network remote architecture (server + client model).

## License

This project is released under the [MIT License](LICENSE).

**Safety:** Use at your own risk around real radios and RF equipment. The MIT License does not cover liability for how you operate hardware.

## Search and discoverability

To help Google index this project, a lightweight GitHub Pages SEO setup is included under `docs/`:

- `docs/index.md`
- `docs/robots.txt`
- `docs/sitemap.xml`

Expected Pages URL:

- https://janitorhead.github.io/ts570d_remote/
