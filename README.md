# HSFP acquisition and metadata

New recordings use the sibling **Aind.Experiment.Hsfp** clabe launcher. See [acquisition mapper and restart contract](README-mapper.md) before running this branch. The historical direct-Bonsai commands below describe the pre-mapper workflow and are superseded by the registered AcquisitionRoot/SegmentId contract.

# Getting started

During the early development phase, this repository is intended for internal use only.

## Installation Steps

1. Clone this repository alongside Aind.Experiment.Hsfp using the compatible reviewed branches. Follow the wrapper's [numbered setup instructions](https://github.com/AllenNeuralDynamics/Aind.Experiment.Hsfp#setup) for prerequisites, local rig configuration and deployment.
2. Create the ignored `local` folder in this repository and copy `examples/HSFP-default-camera-config.xml` there as the initial local camera settings. Save rig-specific variants in that local folder.
3. Run `scripts/deploy.ps1` from Aind.Experiment.Hsfp. This synchronizes Python dependencies, restores the Bonsai environment and builds the acquisition extensions. HamamatsuCamera 0.5.0 is [published on NuGet.org](https://www.nuget.org/packages/AllenNeuralDynamics.HamamatsuCamera); `.bonsai/NuGet.config` uses that public feed. No manual NuGet download or `.temp` copy is needed.
4. Run the combined tests and short rig pilot described in the wrapper README before routine acquisition. Do not commit generated packages or local rig/camera settings.

## Running the acquisition
### Running manually on the acquisition mapper branch

After deployment, open `.bonsai/Bonsai.exe` and load `src/main.bonsai` directly. Settings can be inspected by opening HsfpCamera while stopped; preview does not create a recording. Set the top-level SettingsPath to your local XML and verify ports, ChannelCount (5) and ChunkSize.

Leave AcquisitionRoot and SegmentId empty and enter Subject for a standalone hardware recording. Start the workflow, wait for camera/serial startup, then use StartExperiment (`c`) and EndExperiment (`s`). Each run writes a unique `<subject>_<local-time>_<unique-id>` folder under `C:/output_data` (override with the HSFP_MANUAL_OUTPUT_DIRECTORY environment variable before opening Bonsai). No subject-service calls or biological acquisition_fib.json generation occur. Use the launcher for complete animal metadata or its calibration option for preserved no-mouse calibration sources. See README-mapper.md and the wrapper README for details.

### Historical CLI reference
The workflow can be launched via the Bonsai Command Line Interface (CLI). Additional documentation can be found [here](https://bonsai-rx.org/docs/articles/cli.html). To run the acquisition workflow using the CLI, use the following command: <br>
`"./.bonsai/bonsai.exe" "./src/main.bonsai" -p Subject=000000 -p SettingsPath="C:/git/Aind.Physiology.Hsfp/local/HSFP-default-camera-config.xml" -p ChannelCount-5 -p ChunkSize=1000`