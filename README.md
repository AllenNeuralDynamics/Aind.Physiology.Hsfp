# HSFP acquisition and metadata

New recordings use the sibling **Aind.Experiment.Hsfp** clabe launcher. See [acquisition mapper and restart contract](README-mapper.md) before running this branch. The historical direct-Bonsai commands below describe the pre-mapper workflow and are superseded by the registered AcquisitionRoot/SegmentId contract.

# Getting started

During the early development phase, this repository is intended for internal use only.

## Installation Steps
1. Clone this repository (recommended to clone into `C:/git`)
2. Create a folder named `.temp` and a folder named `local` in the root of the cloned repository
3. Download the nuget package release from [AllenNeuralDynamics.HamamatsuCamera](https://github.com/AllenNeuralDynamics/AllenNeuralDynamics.HamamatsuCamera/releases) that corresponds to the version specified in the `bonsai/Bonsai.config` file, save it to the `.temp` folder
4. Create the environments for Bonsai by running `./bonsai/setup.cmd`
5. Launch Bonsai, it will attempt to find the Hamamatsu package in the `.temp` folder
6. Copy the camera settings file `HSFP-default-camera-config.xml` from the `examples` folder into your newly created `local` folder
    - This is your local, on-rig copy of the camera settings used in the Hamamatsu Bonsai node. 
    - Any changes you want to make to the default settings should be saved as new variants of this `.xml` file in the `local` folder
6. Do NOT commit packages  or the `.temp` and `local` folders to git

## Running the acquisition
### Running manually on the acquisition mapper branch

After deployment, open `.bonsai/Bonsai.exe` and load `src/main.bonsai` directly. Settings can be inspected by opening HsfpCamera while stopped; preview does not create a recording. Set the top-level SettingsPath to your local XML and verify ports, ChannelCount (5) and ChunkSize.

Leave AcquisitionRoot and SegmentId empty and enter Subject for a standalone hardware recording. Start the workflow, wait for camera/serial startup, then use StartExperiment (`c`) and EndExperiment (`s`). Each run writes a unique `<subject>_<local-time>_<unique-id>` folder under `C:/output_data` (override with the HSFP_MANUAL_OUTPUT_DIRECTORY environment variable before opening Bonsai). No subject-service calls or biological acquisition_fib.json generation occur. Use the launcher for complete animal metadata or its calibration-hsfp option for preserved no-mouse calibration sources. See README-mapper.md and the wrapper README for details.

### Historical CLI reference
The workflow can be launched via the Bonsai Command Line Interface (CLI). Additional documentation can be found [here](https://bonsai-rx.org/docs/articles/cli.html). To run the acquisition workflow using the CLI, use the following command: <br>
`"./.bonsai/bonsai.exe" "./src/main.bonsai" -p Subject=000000 -p SettingsPath="C:/git/Aind.Physiology.Hsfp/local/HSFP-default-camera-config.xml" -p ChannelCount-5 -p ChunkSize=1000`