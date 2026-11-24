"""Data contract of one HSFP acquisition epoch (``<session>/fib/hsfp_<datetime>``)."""

import dataclasses
import functools
import os
from collections.abc import Sequence
from pathlib import Path

import numpy as np
from aind_behavior_services.session import Session
from contraqctor.contract import Dataset, DataStream, DataStreamCollection, FilePathBaseParam, csv
from contraqctor.contract.harp import DeviceYmlByFile, HarpDevice
from contraqctor.contract.json import Json, JsonParams, PydanticModel, SoftwareEvents
from contraqctor.contract.text import Text, TextParams

from aind_physiology_hsfp import __semver__
from aind_physiology_hsfp.rig import AindPhysioHsfpRig
from aind_physiology_hsfp.task_logic import AindPhysioHsfpTaskLogic

CAMERA_APPLIED_SETTINGS = "camera_applied.json"
"""Camera properties applied by the DCAM driver, relative to the epoch directory."""

RIG_INPUT = "Logs/rig_input.json"
"""Rig the epoch was acquired with, relative to the epoch directory."""


@dataclasses.dataclass
class TiffStackReader:
    """Reads the chunked TIFF stack (``frame_0000.tiff``, ``frame_0001.tiff``, ...) of one channel."""

    files: list[Path]

    @functools.cached_property
    def _chunks(self) -> list[tuple[Path, int, int]]:
        """(file, index of its first frame, number of frames) of each chunk."""
        import tifffile

        chunks = []
        start = 0
        for file in self.files:
            with tifffile.TiffFile(file) as tif:
                count = len(tif.pages)
            chunks.append((file, start, count))
            start += count
        return chunks

    @property
    def number_of_frames(self) -> int:
        return sum(count for _, _, count in self._chunks)

    def get_frames(self, frame_indices: list[int]) -> list[np.ndarray]:
        import tifffile

        frames = []
        for index in frame_indices:
            for file, first, count in self._chunks:
                if first <= index < first + count:
                    frames.append(tifffile.imread(file, key=index - first))
                    break
            else:
                raise IndexError(f"Frame {index} is out of range ({self.number_of_frames} frames).")
        return frames


class TiffStack(DataStream[TiffStackReader, FilePathBaseParam]):
    """Raw frames of one channel, stored as a directory of TIFF chunks."""

    make_params = FilePathBaseParam

    @staticmethod
    def _reader(params: FilePathBaseParam) -> TiffStackReader:
        directory = Path(params.path)
        if not directory.is_dir():
            raise FileNotFoundError(f"Directory {directory} does not exist.")
        files = sorted(directory.glob("*.tiff"))
        if not files:
            raise FileNotFoundError(f"No TIFF files found in {directory}.")
        return TiffStackReader(files=files)


def _channel_stream(root: Path, channel: int) -> DataStream:
    return TiffStack(
        f"channel_{channel}",
        reader_params=TiffStack.make_params(path=root / f"channel_{channel}"),
        description=f"Raw frames of channel {channel}, the frames acquired while the light source on cuTTLefish "
        f"channel {channel} is on.",
    )


def acquired_channels(root: os.PathLike) -> list[int]:
    """Channels acquired in an epoch: the cuTTLefish channels with a light source in its rig.

    Falls back to channels 1 to 5 when the epoch has no rig input (e.g. to render the contract).
    """
    rig_path = Path(root) / RIG_INPUT
    if rig_path.is_file():
        rig = AindPhysioHsfpRig.model_validate_json(rig_path.read_text(encoding="utf-8-sig"))
        return list(rig.light_sources())
    return list(range(1, 6))


def dataset(root: os.PathLike, channels: Sequence[int] | None = None) -> Dataset:
    """Data contract of one HSFP acquisition epoch.

    Args:
        root: Path to the epoch directory (``<session>/fib/hsfp_<datetime>``).
        channels: Acquired channels, i.e. the cuTTLefish channels with a light source. By default, they are read
            from the epoch's rig input (see :func:`acquired_channels`).
    """
    root = Path(root)
    if channels is None:
        channels = acquired_channels(root)
    return Dataset(
        version=__semver__,
        name="hsfp",
        data_streams=[
            *[_channel_stream(root, channel) for channel in channels],
            csv.Csv(
                "frame_metadata",
                reader_params=csv.CsvParams(path=root / "frame_metadata.csv", delimiter=","),
                description="One row per frame, for all channels: camera frame counter (FrameCounter), camera "
                "timestamp (Timestamp), harp time of the light source trigger (Seconds) and cuTTLefish channel of the "
                "light source (Channel).",
            ),
            Json(
                "camera_applied",
                reader_params=JsonParams(path=root / CAMERA_APPLIED_SETTINGS, encoding="utf-8-sig"),
                description="Camera properties applied by the DCAM driver, read back at the start of the acquisition.",
            ),
            Text(
                "camera_settings",
                reader_params=TextParams(path=root / "camera_settings.xml"),
                description="Copy of the Hamamatsu camera settings file given in the rig.",
            ),
            csv.Csv(
                "camera_parameters",
                reader_params=csv.CsvParams(path=root / "camera_parameters.csv", delimiter=","),
                description="Camera parameters reported by the camera node.",
            ),
            PydanticModel(
                "session_input",
                reader_params=PydanticModel.make_params(path=root / "Logs/session_input.json", model=Session),
                description="Session input parameters for the HSFP experiment.",
            ),
            PydanticModel(
                "rig_input",
                reader_params=PydanticModel.make_params(path=root / RIG_INPUT, model=AindPhysioHsfpRig),
                description="Rig input parameters for the HSFP experiment.",
            ),
            PydanticModel(
                "task_input",
                reader_params=PydanticModel.make_params(
                    path=root / "Logs/task_input.json", model=AindPhysioHsfpTaskLogic
                ),
                description="Task input parameters for the HSFP experiment.",
            ),
            HarpDevice(
                "Cuttlefish",
                reader_params=HarpDevice.make_params(path=root / "Cuttlefish.harp", device_yml_hint=DeviceYmlByFile()),
                description="Events of the cuTTLefish board that triggers the camera and the light sources.",
            ),
            HarpDevice(
                "CuttlefishCommands",
                reader_params=HarpDevice.make_params(
                    path=root / "HarpCommands/Cuttlefish.harp", device_yml_hint=DeviceYmlByFile()
                ),
                description="Commands sent to the cuTTLefish board, including the PWM settings of each channel.",
            ),
            DataStreamCollection(
                name="SoftwareEvents",
                description="Software events generated by the workflow. The timestamps of these events are low "
                "precision and should not be used to align to physiology data.",
                data_streams=[
                    SoftwareEvents(
                        name="StartSessionTime",
                        description="An event emitted at the start of the session. Data contains a UTC timestamp "
                        "of the session start time.",
                        reader_params=SoftwareEvents.make_params(path=root / "SoftwareEvents/StartSessionTime.jsonl"),
                    ),
                    SoftwareEvents(
                        name="EndSessionTime",
                        description="An event emitted at the end of the session. Data contains a UTC timestamp "
                        "of the session end time.",
                        reader_params=SoftwareEvents.make_params(path=root / "SoftwareEvents/EndSessionTime.jsonl"),
                    ),
                    SoftwareEvents(
                        name="RepositoryStatus",
                        description="An event emitted at the start of the session. Data is true if the repository "
                        "had no uncommitted changes.",
                        reader_params=SoftwareEvents.make_params(path=root / "SoftwareEvents/RepositoryStatus.jsonl"),
                    ),
                    SoftwareEvents(
                        name="HardwareQA",
                        description="An event emitted when a hardware check fails and the experimenter chooses to "
                        "continue, e.g. starting while the cuTTLefish is not synchronized with the Harp clock. This file "
                        "is optional.",
                        reader_params=SoftwareEvents.make_params(path=root / "SoftwareEvents/HardwareQA.jsonl"),
                    ),
                    SoftwareEvents(
                        name="Lasers",
                        description="One event per reply of an OBIS laser (or of the laser box it is connected to), "
                        "timestamped when the reply arrived. Each names the light source's cuTTLefish channel and the "
                        "laser's serial number, model, wavelength, port and channel on that port, whether the laser "
                        "or its box replied (device), the command (e.g. GetInfo, GetState, GetPower, SYST:INF:SNUM?) "
                        "and its value, or the error if the command was rejected. When the session starts, every "
                        "read-only command is sent; after that, GetPower (the measured output power, in W) every 10 s.",
                        reader_params=SoftwareEvents.make_params(path=root / "SoftwareEvents/Lasers.jsonl"),
                    ),
                    SoftwareEvents(
                        name="Annotations",
                        description="An event emitted whenever an annotation is made during the session.",
                        reader_params=SoftwareEvents.make_params(path=root / "SoftwareEvents/Annotations.jsonl"),
                    ),
                ],
            ),
        ],
    )


def render_dataset() -> str:
    """Renders the dataset as a tree-like structure for visualization."""
    from contraqctor.contract.utils import print_data_stream_tree_html

    return print_data_stream_tree_html(dataset(Path("<RootPath>")), show_missing_indicator=False, show_type=True)
