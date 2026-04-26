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

- .NET (see `TS570_Remote/TS570_Remote.csproj` for target framework)
- OmniRig and appropriate rig configuration

## Roadmap (checklist)

High-impact gaps a human contributor would need to tackle — especially anything that needs pixel-accurate layout or non-trivial graphics.

- [ ] **Front-panel fidelity:** Tighten button and control placement so the layout tracks the **real TS-570** more closely. Plain **XAML** is already a bottleneck: fine-grained alignment and “hardware” feel need **manual** iteration; generic AI edits stop being useful here.
- [ ] **LCD badges:** Drive **all** status badges on the faux display so they track the **live** radio state the way the physical LCD does (full dynamic parity, not just a subset).
- [ ] **Meter bargraphs (S, power, SWR, ALC):** Make the arcs **functionally correct** and visually convincing. **Curved labels** that follow the original meter artwork are painful in stock XAML; expect to **simplify** the graphics or move meter rendering to a **custom layer** (e.g. `Canvas`/`DrawingContext`, or another 2D stack) if you want dial-faithful typography.
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
