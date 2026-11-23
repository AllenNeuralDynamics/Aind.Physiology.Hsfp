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

[Pre-requisites for running the project can be found here](https://allenneuraldynamics.github.io/Aind.Behavior.Services/articles/requirements.html), plus the Hamamatsu DCAM-API (camera driver).

---

## 🚀 Deployment

```powershell
./scripts/deploy.ps1
```

## ⚙️ Generating settings files

The workflow takes three settings files: `AindPhysioHsfpRig.json`, `AindPhysioHsfpTaskLogic.json` and `Session.json`. `uv run ./examples/example.py` writes examples to `./local/`; set `camera.settings_path` and `cuttlefish.port_name` for each PC.

```powershell
"./.bonsai/bonsai.exe" "./src/main.bonsai" -p RigPath="../local/AindPhysioHsfpRig.json" -p SessionPath="../local/Session.json" -p TaskPath="../local/AindPhysioHsfpTaskLogic.json"
```

Paths are relative to `./src/`. Place the ROIs, then click `Start experiment`; nothing is saved before that.

> [!Important]
> `Start experiment` asks for confirmation if the cuTTLefish is not synchronized with the Harp clock (`R_HEARTBEAT`), and saves an override as a `HardwareQA` software event. `Session.skip_hardware_validation` skips the check.

## 💡 Temporal multiplexing

The cuTTLefish triggers the camera on channel 0. Channels 1-7 each hold a `LightSource` or `None`; light sources fire one per frame, in channel order, and each defines the acquisition channel with the same number (`channel_3/`, ...).

Timing is shared by all light sources, in integer microseconds, so the schedule is exact and never drifts:

| `timing` field | Default | |
| -------------- | ------: | - |
| `frame_period_us` | 6667 | Camera period (149.99 Hz) |
| `camera_trigger_duration_us` | 100 | Camera trigger width |
| `light_source_on_duration_us` | 1000 | Light source pulse width |
| `light_source_delay_us` | 0 | Delay from the camera trigger |

> [!Note]
> The cuTTLefish cannot remove a configured PWM output. If a channel used in a previous session is now unused, power-cycle the board.

## 🖥️ Acquisition UI

Composed of independent panels (`src/Extensions/Visualization.bonsai`): acquisition controls and notes, health (rates, dropped frames, Harp clock), cuTTLefish channels, channel selector, ROI editor, activity and spectrum. ROIs are saved to `../local/hsfp_roi.json` and recorded in `regions.json` and `Regions` events. `./src/test_visualization.bonsai` runs the UI with simulated hardware.

## 📁 Data

Each acquisition is saved to `<data_directory>/<session_name>/fib/hsfp_<datetime>/`:

| Path | Content |
| ---- | ------- |
| `channel_<N>/*.tiff` | Raw frames of channel N |
| `frame_metadata.csv` | `FrameCounter`, `Timestamp`, `Seconds` and `Channel` of every frame |
| `camera_*` | Applied camera properties, settings and parameters |
| `Cuttlefish.harp/`, `HarpCommands/` | cuTTLefish events and commands |
| `regions.json` | ROIs at the start of the acquisition |
| `Logs/` | Session, rig and task logic |
| `SoftwareEvents/*.jsonl` | Software events |

The layout is formalized as a [contraqctor](https://github.com/AllenNeuralDynamics/contraqctor) dataset:

```python
from aind_physiology_hsfp.data_contract import dataset

frames = dataset("path/to/hsfp_<datetime>")["channel_1"].read().get_frames([0, 1, 2])
```

## 🔄 Regenerating schemas

```powershell
uv run hsfp regenerate
```

## 🧪 Tests

```powershell
uv run pytest
uv run ruff check .
```

## [> ] CLI tools

```powershell
uv run hsfp -h
```

## 🌉 Mapping to aind-data-schema

```powershell
uv run hsfp-data-mapper --data-path "path/to/session" --instrument-path "path/to/instrument_fib.json"
```

Writes `acquisition_fib.json` into the session directory. The instrument metadata is optional; it fills in light source powers and wavelengths missing from the rig. See the [data mapper README](./src/packages/aind_physiology_hsfp_data_mappers/README.md).
