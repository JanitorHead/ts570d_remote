# ts570d_remote

**Disclaimer:** This repository is **vibe coded** (AI-assisted, exploratory hacking) and is **only an experiment**. It is not production software, not audited for safety or correctness, and not a substitute for the manufacturer’s tools or documentation.

---

## Project status / how to help

**IF YOU KNOW C#, WPF, AND XAML: IT WOULD BE GREAT IF YOU COULD HELP OUT — OR FORK THIS AND RUN WITH IT YOURSELF.** The goal is not to leave this as eye candy: this tool should be **actually useful** and **usable for real** in the shack.

**Work in progress — honest expectations:** this repo is **unfinished** and **actively stalled on my side**. I am **not a professional developer** and I have hit a **technical roadblock** I cannot reliably push through alone. **I do not promise** that I will keep improving or maintaining it; the next steps really need **manual, skilled work** on the codebase, not more “vibe coding” passes.

**Where things stand:** the UI already “lands”, but from here on it needs **hands-on work** in the code. **XAML** is where AI assistance falls short (fine layouts, styles, alignment, keeping the front panel maintainable), so it helps to know the stack — or at least have patience for manual polish.

**CAT / OmniRig:** **a lot of functionality is still missing** or **not trustworthy / not finished**. Do not assume everything you see on screen is correctly wired to the rig.

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
- **OmniRig** installed and configured for your rig at **runtime**; the **build** also depends on a local interop assembly (see [Build](#build)).

## Build

**Interop assembly:** The project references `TS570_Remote/Libraries/Interop.OmniRig.dll` (see `TS570_Remote.csproj`). That file must be present or the build will fail. If you do not have it in your tree, create it from the OmniRig type library (for example with `tlbimp` on `OmniRig.tlb` from the OmniRig install) and place the DLL in `TS570_Remote/Libraries/`, or adjust the reference in the project to match your layout.

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

## Roadmap (checklist)

High-impact gaps a human contributor would need to tackle — especially anything that needs pixel-accurate layout or non-trivial graphics.

- [ ] **Front-panel fidelity:** Tighten button and control placement so the layout tracks the **real TS-570** more closely. Plain **XAML** is already a bottleneck: fine-grained alignment and “hardware” feel need **manual** iteration; generic AI edits stop being useful here.
- [ ] **LCD badges:** Drive **all** status badges on the faux display so they track the **live** radio state the way the physical LCD does (full dynamic parity, not just a subset).
- [ ] **Meter bargraphs (S, power, SWR, ALC):** Make the arcs **functionally correct** and visually convincing. **Curved typography** that follows the real meter artwork is **not something you can do in a clean, maintainable way in stock WPF/XAML** — there is no first-class “text along a path” in the layout system, so you either **simplify** (straight labels, fewer ticks), use **prerendered** dial art, or move meters to a **custom draw** path (`DrawingContext`, SkiaSharp, etc.) instead of pretending XAML can own that geometry.
- [ ] **CAT coverage:** Implement and verify **Kenwood TS-570 CAT** commands end-to-end (map UI actions ↔ rig, error handling, edge cases) — not just the paths that happen to work today.
- [ ] **PHONES / MIC knobs:** Hook up **headphone level** and **mic gain** (or whatever the rig exposes via CAT/OmniRig for those controls) so the knobs do real work.

## What works today (snapshot)

Roughly what is **known to do something useful** in the current build; everything else should be treated as **incomplete or cosmetic** until proven otherwise.

- [x] **VFO frequency** readback from the rig.
- [x] **Main VFO knob** and **MULTI/CH** knob: frequency changes.
- [x] **Band UP / DOWN** buttons.
- [x] **Mode** selection (LSB/USB/CW/FM/AM paths as wired).
- [x] **S-meter:** partially working (do not trust it for critical readings yet).
- [x] **Power off** from the remote UI.
- [x] **Antenna** selection.
- [x] **VFO A/B** toggle behaviour as implemented.

**Still weak or missing:** For **most other front-panel buttons**, either **nothing is wired**, or the **LCD-style feedback** does not yet mirror what the real radio would show when you press them. Treat the panel as a **work in progress**, not a complete control surface.

## License

This project is released under the [MIT License](LICENSE).

**Safety:** Use at your own risk around real radios and RF equipment. The MIT License does not cover liability for how you operate hardware.
