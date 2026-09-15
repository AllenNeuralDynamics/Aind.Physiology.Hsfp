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
### Running manually
- Open Bonsai from the bootstrapped environment in `./.bonsai/bonsai.exe`
- Open the workflow file `./src/main.bonsai`
- Manually set the 4 highest level properties:
    - `ChunkSize`: number of frames to stack in a single tiff _(default = 1000)_
    - `SettingsPath`: path to the camera settings file _(default = C:/git/Aind.Physiology.Hsfp/local/HSFP-default-camera-config.xml)_
    - `Subject`: subject ID for the current acquisition _(default = 000000)_
    - `Value`: number of laser channels to deinterleave _(default = 5)_
- Launch the workflow by clicking the "Start" button in the Bonsai editor
- Wait for the Teensy message `Experiment manually stopped`to display, then click Start

### Running via CLI
The workflow can be launched via the Bonsai Command Line Interface (CLI). Additional documentatoin can be found [here](https://bonsai-rx.org/docs/articles/cli.html). To run the acquisition workflow using the CLI, use the following command: <br>
`"./.bonsai/bonsai.exe" "./src/main.bonsai" -p Subject=000000 -p SettingsPath="C:/git/Aind.Physiology.Hsfp/local/HSFP-default-camera-config.xml" -p Value-5 -p ChunkSize=1000`