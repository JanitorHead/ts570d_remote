# ts570d_remote

**Disclaimer:** This repository is **vibe coded** (AI-assisted, exploratory hacking) and is **only an experiment**. It is not production software, not audited for safety or correctness, and not a substitute for the manufacturer’s tools or documentation.

---

## Estado del proyecto / cómo ayudar

**SI SABES DE C#, WPF Y XAML: SERÍA GENIAL QUE ME ECHARAS UNA MANO — O QUE HICIERAS UN FORK Y LO LLEVES TÚ MISMO.** La idea no es dejarlo en un juguete visual: quiero que esta herramienta **sea útil** y que **se pueda usar de verdad** en el shack.

**Dónde estamos ahora:** el UI ya “engancha”, pero a partir de aquí toca **meterle mano manual** al código. Con **XAML** la IA ayuda poco (layouts finos, estilos, alineaciones, mantenimiento del panel), así que hace falta alguien que sepa lo suyo o que al menos tenga paciencia para retocar a mano.

**CAT / OmniRig:** **mucha funcionalidad aún no está incorporada** o **no es fiable / no está terminada**. No asumas que todo lo que ves en pantalla está bien cableado al rig.

**Red / remoto real:** **de momento no hay ningún servidor** para colgar la emisora en red y mandarla desde otro PC o desde fuera de casa. Hoy es **control local** (Windows + OmniRig según tu config), no un producto de telecontrol por LAN/Internet.

Si te interesa: abre un issue, un PR, o un fork y adelante.

---

## What it is

A Windows desktop remote-control style UI for the **Kenwood TS-570D** HF transceiver, built with **WPF** and **OmniRig** for CAT. Treat it as a learning toy, not a guaranteed rig-control solution.

![Remote UI mimicking the TS-570D front panel](docs/screenshot.png)

## Requirements

- .NET (see `TS570_Remote/TS570_Remote.csproj` for target framework)
- OmniRig and appropriate rig configuration

## License

This project is released under the [MIT License](LICENSE).

**Safety:** Use at your own risk around real radios and RF equipment. The MIT License does not cover liability for how you operate hardware.
