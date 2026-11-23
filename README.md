# Hyperspectral Fiber Photometry (HSFP)

![CI](https://github.com/AllenNeuralDynamics/Aind.Physiology.Hsfp/actions/workflows/aind-physiology-hsfp.yml/badge.svg)
[![License](https://img.shields.io/badge/license-MIT-brightgreen)](LICENSE)
[![ruff](https://img.shields.io/endpoint?url=https://raw.githubusercontent.com/astral-sh/ruff/main/assets/badge/v2.json)](https://github.com/astral-sh/ruff)
[![uv](https://img.shields.io/endpoint?url=https://raw.githubusercontent.com/astral-sh/uv/main/assets/badge/v0.json)](https://github.com/astral-sh/uv)

For HSFP data acquisition and hardware control.

> [!Note]
> During the early development phase, this repository is intended for internal use only.

## Overview

The output of each fiber is dispersed by a slit and a prism onto a Hamamatsu camera, so that each recorded frame holds the emission spectrum of every fiber. Fibers are excited by a sequence of lasers, one at a time, so that each frame is acquired under a single excitation wavelength.

## Temporal multiplexing

A cuTTLefish board triggers the camera and the lasers through its PWM outputs:

* Pin 0 triggers the camera (`camera.trigger` in the rig).
* Pin N + 1 triggers the N-th laser of the rig (`lasers[N]`), which defines acquisition channel N.

With the default rig, the camera is triggered at 150 Hz and the five lasers fire in turn, each at 30 Hz:

| Channel | cuTTLefish pin | Laser       | Offset (µs) |
|--------:|---------------:|-------------|------------:|
| 0       | 1              | OBIS 405 LX | 0           |
| 1       | 2              | OBIS 445 LX | 6667        |
| 2       | 3              | OBIS 488 LX | 13334       |
| 3       | 4              | OBIS 514 LX | 20001       |
| 4       | 5              | OBIS 561 LS | 26668       |

Each frame is assigned to a channel using the cuTTLefish rising-edge events of the laser pins. The rig validates that every laser period equals the camera period times the number of lasers.

## Using the acquisition system

### Prerequisites

* [Visual Studio Code](https://code.visualstudio.com/) (recommended for editing code and git commits)
* [Git for Windows](https://gitforwindows.org/)
* [.NET Framework 4.7.2 Developer Pack](https://dotnet.microsoft.com/download/dotnet-framework/thank-you/net472-developer-pack-offline-installer) (required for intellisense when editing code scripts)
* Hamamatsu DCAM-API (camera driver)
* [uv](https://docs.astral.sh/uv/getting-started/installation/) (Python environment manager. All examples assume uv.)

### Installation

1. Clone this repository.
2. Run `./scripts/deploy.ps1` (or double-click `./scripts/deploy.cmd`). This creates the Python environment and the Bonsai environment in `./.bonsai/`.
   * To only create the Bonsai environment, run `./.bonsai/setup.ps1`.

### Generating input configurations

The workflow takes three input configuration files, formalized as pydantic models:

* `AindPhysioHsfpRig` (`aind_physiology_hsfp.rig`): camera, cuTTLefish and lasers of the rig.
* `AindPhysioHsfpTaskLogic` (`aind_physiology_hsfp.task_logic`): the recorded fibers, the patch cord connected to each one, and their intended measurements.
* `Session` (`aind_behavior_services.session`): subject, experimenter and notes.

Templates are in `./examples/`. Running `uv run ./examples/example.py` writes them to `./local/`, the folder the workflow reads by default. Modify `camera.settings_path` (the Hamamatsu camera settings `.xml`, see `./examples/HSFP-default-camera-config.xml`) and `cuttlefish.port_name` for each PC.

### Running the acquisition

#### Running manually

* Open Bonsai from `./.bonsai/Bonsai.exe`.
* Open `./src/main.bonsai`.
* Set the top-level properties `RigPath`, `SessionPath` and `TaskPath` to the configuration files.
* Click `Start` in the Bonsai editor. The camera preview starts, but nothing is saved yet.
* Before starting the acquisition, place the region of interest (ROI) of each fiber (see [Acquisition UI](#acquisition-ui)).
* Click `Start experiment` to start the acquisition, and `Stop experiment` to stop it. The workflow then closes.

> [!Important]
> `Session.allow_dirty_repo` is checked at the start of the workflow. If `false`, the workflow stops with an error when the repository has uncommitted changes.

#### Running via CLI

```bash
"./.bonsai/bonsai.exe" "./src/main.bonsai" -p RigPath="../local/AindPhysioHsfpRig.json" -p SessionPath="../local/Session.json" -p TaskPath="../local/AindPhysioHsfpTaskLogic.json"
```

> [!Note]
> Relative paths are relative to the workflow directory (`./src/`).

Additional flags can start the workflow automatically (`--start`) or run it without the editor (`--no-editor`), as described in the [Bonsai CLI documentation](https://bonsai-rx.org/docs/articles/cli.html).

### Acquisition UI

The acquisition window is composed of independent panels (`src/Extensions/Visualization.bonsai`), each drawn by its own visualizer in `src/Extensions/Hsfp*Visualizer*.cs`:

* **Acquisition**: start / stop, session details, the folder being saved to, and notes (saved as `Annotations` software events).
* **Health**: camera rate against the rate expected from the rig, frames received, dropped frames and channel sequence errors (frames whose laser does not follow the previous frame's, i.e. frames that may be assigned to the wrong channel).
* **Laser sequence**: the lasers of the rig with the rate and number of frames of each channel.
* **Channels**: one button per laser; selects the channel shown by the ROI and spectrum panels.
* **Regions of interest**: the live image of the selected channel with one ROI per fiber of the task logic. Drag a ROI to move it, drag its corner to resize it, or type its position in the table. With `Link channels` on, a change applies to every channel; otherwise only to the selected one. ROIs are locked while recording.
* **Activity**: the mean of each ROI over the last 30 s, by channel or by fiber.
* **Spectrum**: the mean of each column of each ROI (the emission spectrum along the sensor) for the selected channel.

Channels, colors and names come from the rig (one channel per laser, colored by wavelength), so adding or removing lasers needs no UI change.

ROIs are saved with `Save` to `../local/hsfp_roi.json` (relative to `./src/`) and loaded from it at startup. The ROIs at the start of an acquisition are saved to `regions.json`, and every change is saved as a `Regions` software event.

To work on the UI without hardware, run `./src/test_visualization.bonsai`, which replaces the camera and the cuTTLefish with simulated frames.

### Data

Each acquisition (from `Start` to `End`) is saved to a new folder, `<data_directory>/<session_name>/fib/hsfp_<datetime>/`:

| Path | Content |
|------|---------|
| `channel_<N>/frame_<NNNN>.tiff` | Raw frames of channel N, in TIFF files of `tiff_chunk_size` frames |
| `frame_metadata.csv` | One row per frame, for all channels: camera frame counter (`FrameCounter`), camera timestamp (`Timestamp`), harp time of the laser trigger (`Seconds`) and channel (`Channel`) |
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

hsfp = dataset("path/to/session/fib/hsfp_<datetime>")
frames = hsfp["channel_0"].read().get_frames([0, 1, 2])
```

## Generating `aind-data-schema` metadata

The `hsfp-data-mapper` CLI maps a session to `acquisition_fib.json`, written into the session directory. Each `hsfp_<datetime>` epoch becomes one data stream.

```powershell
uv run hsfp-data-mapper --data-path "path/to/session" --instrument-path "path/to/instrument_fib.json"
```

The instrument metadata is optional. When given, device names are checked against it, and laser powers not set in the rig (`lasers[N].power`) are taken from its latest calibrations. Run `uv run hsfp-data-mapper --help` for all options.

## Contributors

### Linting

```bash
uv run ruff format .
uv run ruff check .
uv run codespell --check-filenames
```

### Testing

```bash
uv run pytest
```

Tests that run Bonsai are skipped if the Bonsai environment is not set up.

### Regenerating schemas

The JSON schema (`./schema/`) and the Bonsai classes (`./src/Extensions/AindPhysiologyHsfp.Generated.cs`) are generated from the pydantic models:

```bash
uv run hsfp regenerate
```
