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

### Inspiration

The idea comes from **Hans, DK9BP** — a fellow amateur who offered **TS-570 control software** from his website. He is now, sadly, **silent key**. His site is **no longer online**, so the original download is gone; what remains is mainly material on his **YouTube channel**. For a taste of what that era looked like, here is **one example video** from his channel: [youtube.com/watch?v=DLC3zaSg2NM](https://www.youtube.com/watch?v=DLC3zaSg2NM).

![Remote UI mimicking the TS-570D front panel](docs/screenshot.png)

## Requirements

- .NET (see `TS570_Remote/TS570_Remote.csproj` for target framework)
- OmniRig and appropriate rig configuration

## License

This project is released under the [MIT License](LICENSE).

**Safety:** Use at your own risk around real radios and RF equipment. The MIT License does not cover liability for how you operate hardware.
