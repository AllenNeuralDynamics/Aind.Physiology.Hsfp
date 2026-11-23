# Hyperspectral Fiber Photometry (HSFP)

![CI](https://github.com/AllenNeuralDynamics/Aind.Physiology.Hsfp/actions/workflows/aind-physiology-hsfp.yml/badge.svg)
[![License](https://img.shields.io/badge/license-MIT-brightgreen)](LICENSE)
[![ruff](https://img.shields.io/endpoint?url=https://raw.githubusercontent.com/astral-sh/ruff/main/assets/badge/v2.json)](https://github.com/astral-sh/ruff)
[![uv](https://img.shields.io/endpoint?url=https://raw.githubusercontent.com/astral-sh/uv/main/assets/badge/v0.json)](https://github.com/astral-sh/uv)

For HSFP data acquisition and hardware control.

> [!Note]
> During the early development phase, this repository is intended for internal use only.

## Overview

The output of each fiber is dispersed by a slit and a prism onto a Hamamatsu camera, so that each recorded frame holds the emission spectrum of every fiber. Fibers are excited by a sequence of light sources, one at a time, so that each frame is acquired under a single excitation wavelength.

---

## 📋 General instructions

This repository follows the project structure laid out in the [Aind.Behavior.Services repository](https://github.com/AllenNeuralDynamics/Aind.Behavior.Services).

---

## 🔧 Prerequisites

[Pre-requisites for running the project can be found here](https://allenneuraldynamics.github.io/Aind.Behavior.Services/articles/requirements.html). In addition, the acquisition PC needs the Hamamatsu DCAM-API (camera driver).

---

## 🚀 Deployment

For convenience, once third-party dependencies are installed, `Bonsai` and `python` virtual environments can be bootstrapped by running:

```powershell
./scripts/deploy.ps1
```

from the root of the repository (or by double-clicking `./scripts/deploy.cmd`). To only create the Bonsai environment, run `./.bonsai/setup.ps1`.

## ⚙️ Generating settings files

The HSFP acquisition is instantiated by a set of three settings files that strictly follow a DSL schema:

- `AindPhysioHsfpRig.json` (`aind_physiology_hsfp.rig`): camera, cuTTLefish channels (light sources) and trigger timing of the rig.
- `AindPhysioHsfpTaskLogic.json` (`aind_physiology_hsfp.task_logic`): the recorded fibers, the patch cord connected to each one, and their intended measurements.
- `Session.json` (`aind_behavior_services.session`): subject, experimenter and notes.

Examples are in `./examples/`. Running `uv run ./examples/example.py` writes them to `./local/`, the folder the workflow reads by default. Modify `camera.settings_path` (the Hamamatsu camera settings `.xml`, see `./examples/HSFP-default-camera-config.xml`) and `cuttlefish.port_name` for each PC.

Once generated, these are the only required inputs to run the Bonsai workflow in `./src/main.bonsai`, which can be executed using the [Bonsai CLI](https://bonsai-rx.org/docs/articles/cli.html):

```powershell
"./.bonsai/bonsai.exe" "./src/main.bonsai" -p RigPath="../local/AindPhysioHsfpRig.json" -p SessionPath="../local/Session.json" -p TaskPath="../local/AindPhysioHsfpTaskLogic.json"
```

> [!Note]
> Relative paths are relative to the workflow directory (`./src/`).

Additional flags can start the workflow automatically (`--start`) or run it without the editor (`--no-editor`).

To run it from the Bonsai editor instead, open `./src/main.bonsai` in `./.bonsai/Bonsai.exe`, set `RigPath`, `SessionPath` and `TaskPath`, and click `Start`. The camera preview starts, but nothing is saved until `Start experiment` is clicked in the acquisition UI. Place the region of interest (ROI) of each fiber before that (see [Acquisition UI](#️-acquisition-ui)). `Stop experiment` stops the acquisition and closes the workflow.

> [!Important]
> `Session.allow_dirty_repo` is checked at the start of the workflow. If `false`, the workflow stops with an error when the repository has uncommitted changes.

## 💡 Temporal multiplexing

A cuTTLefish board triggers the camera and the light sources through its PWM outputs (`channels` in the rig):

- Channel 0 is reserved for the camera trigger.
- Channels 1 to 7 each hold a `LightSource` (`name`, optional `wavelength` and `power`) or `None`. Each light source defines the acquisition channel with the same number, e.g. frames acquired under channel 3 are saved in `channel_3/`.

All outputs are derived from the shared `timing` of the rig, in integer microseconds:

| Field | Default | Meaning |
| ----- | ------: | ------- |
| `frame_period_us` | 6667 | Time between camera triggers (P) |
| `camera_trigger_duration_us` | 100 | Width of the camera trigger |
| `light_source_on_duration_us` | 1000 | Time each light source stays on in its frame |
| `light_source_delay_us` | 0 | Time from the camera trigger to the light source turning on, in its frame |

The light sources fire in turn, one per frame, in channel order, skipping unused channels. With L light sources, the camera runs with period P and the k-th light source with period L × P and offset k × P + `light_source_delay_us`. Every value is an exact integer, and the firmware schedules edges on absolute deadlines, so the light sources stay phase-locked to the camera with no drift. The rig rejects a pulse that would not end within its frame. Pick P directly in microseconds: 150 Hz, for example, is not a whole number of microseconds, and the default 6667 µs gives 149.99 Hz.

With the default rig, five light sources fire in turn, each at 29.99 Hz:

| cuTTLefish channel | Light source | Offset (µs) |
| -----------------: | ------------ | ----------: |
| 0                  | Camera       | 0           |
| 1                  | OBIS 405 LX  | 0           |
| 2                  | OBIS 445 LX  | 6667        |
| 3                  | OBIS 488 LX  | 13334       |
| 4                  | OBIS 514 LX  | 20001       |
| 5                  | OBIS 561 LS  | 26668       |
| 6, 7               | unused       |             |

Each frame is assigned to a channel using the cuTTLefish rising-edge events. When a light source has no `wavelength`, the data mapper takes it from the instrument metadata, by name.

> [!Note]
> The cuTTLefish firmware cannot remove a PWM output once it has been configured. If a previous session used a channel that is now unused, power-cycle the board so it stops pulsing.

## 🖥️ Acquisition UI

The acquisition window is composed of independent panels (`src/Extensions/Visualization.bonsai`), each drawn by its own visualizer in `src/Extensions/Hsfp*Visualizer*.cs`:

- **Acquisition**: start / stop, session details, the folder being saved to, and notes (saved as `Annotations` software events).
- **Health**: camera rate against the rate expected from the rig, frames received, dropped frames and channel sequence errors (frames whose light source does not follow the previous frame's, i.e. frames that may be assigned to the wrong channel).
- **cuTTLefish channels**: every channel of the board (camera, light sources and unused channels) with its timing, rate and number of frames.
- **Channels**: one button per light source; selects the channel shown by the ROI and spectrum panels.
- **Regions of interest**: the live image of the selected channel with one ROI per fiber of the task logic. Drag a ROI to move it, drag its corner to resize it, or type its position in the table. With `Link channels` on, a change applies to every channel; otherwise only to the selected one. ROIs are locked while recording.
- **Activity**: the mean of each ROI over the last 30 s, by channel or by fiber.
- **Spectrum**: the mean of each column of each ROI (the emission spectrum along the sensor) for the selected channel.

Channels, colors and names come from the rig (one channel per light source, colored by wavelength, or by a neutral tint when the wavelength is not set), so changing the light sources needs no UI change.

ROIs are saved with `Save` to `../local/hsfp_roi.json` (relative to `./src/`) and loaded from it at startup. The ROIs at the start of an acquisition are saved to `regions.json`, and every change is saved as a `Regions` software event.

To work on the UI without hardware, run `./src/test_visualization.bonsai`, which replaces the camera and the cuTTLefish with simulated frames.

## 📁 Data

Each acquisition (from `Start` to `End`) is saved to a new folder, `<data_directory>/<session_name>/fib/hsfp_<datetime>/`:

| Path | Content |
| ---- | ------- |
| `channel_<N>/frame_<NNNN>.tiff` | Raw frames of channel N, in TIFF files of `tiff_chunk_size` frames |
| `frame_metadata.csv` | One row per frame, for all channels: camera frame counter (`FrameCounter`), camera timestamp (`Timestamp`), harp time of the light source trigger (`Seconds`) and cuTTLefish channel (`Channel`) |
| `camera_applied.json` | Camera properties applied by the DCAM driver |
| `camera_settings.xml` | Copy of the camera settings |
| `camera_parameters.csv` | Camera parameters reported by the camera node |
| `Cuttlefish.harp/` | cuTTLefish events |
| `HarpCommands/Cuttlefish.harp/` | Commands sent to the cuTTLefish |
| `regions.json` | Regions of interest at the start of the acquisition |
| `Logs/` | Input (`*_input.json`) and deserialized (`*_output.json`) session, rig and task logic |
| `SoftwareEvents/*.jsonl` | Software events (`StartSessionTime`, `EndSessionTime`, `RepositoryStatus`, `Regions`, `Annotations`) |

The layout is formalized as a [contraqctor](https://github.com/AllenNeuralDynamics/contraqctor) dataset in `aind_physiology_hsfp.data_contract`:

```python
from aind_physiology_hsfp.data_contract import dataset

hsfp = dataset("path/to/session/fib/hsfp_<datetime>", channels=[1, 2, 3, 4, 5])
frames = hsfp["channel_1"].read().get_frames([0, 1, 2])
```

## 🔄 Regenerating schemas

The JSON schema (`./schema/`) and the Bonsai classes (`./src/Extensions/AindPhysiologyHsfp.Generated.cs`) are generated from the pydantic models. To regenerate them, run the following from the repository root:

```powershell
uv run hsfp regenerate
```

## 🧪 Tests

Run Python tests:

```powershell
uv run pytest
```

Tests that run Bonsai are skipped if the Bonsai environment is not set up.

Run the linters:

```powershell
uv run ruff format .
uv run ruff check .
uv run codespell --check-filenames
```

## [> ] CLI tools

The platform exposes a few CLI tools to facilitate various tasks. Tools are available via:

```powershell
uv run hsfp <subcommand>
```

for a list of all sub commands available:

```powershell
uv run hsfp -h
```

## 🌉 Mapping to aind-data-schema

Data mapping is provided by the internal `aind-physiology-hsfp-data-mappers` workspace package. The `hsfp-data-mapper` CLI maps a session to `acquisition_fib.json`, written into the session directory. Each `hsfp_<datetime>` epoch becomes one data stream.

```powershell
uv run hsfp-data-mapper --data-path "path/to/session" --instrument-path "path/to/instrument_fib.json"
```

The instrument metadata is optional. When given, device names are checked against it, and light source powers not set in the rig (`channels.channel_N.power`) are taken from its latest calibrations. Run `uv run hsfp-data-mapper --help` for all options.

See the [data mapper package README](./src/packages/aind_physiology_hsfp_data_mappers/README.md) for details.
