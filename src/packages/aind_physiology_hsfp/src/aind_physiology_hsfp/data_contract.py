"""Data contract of one HSFP acquisition epoch (``<session>/fib/hsfp_<datetime>``)."""

import dataclasses
import os
from collections.abc import Sequence
from pathlib import Path

import numpy as np
from aind_behavior_services.session import Session
from contraqctor.contract import Dataset, DataStream, DataStreamCollection, FilePathBaseParam, csv
from contraqctor.contract.harp import DeviceYmlByFile, HarpDevice
from contraqctor.contract.json import PydanticModel, SoftwareEvents

from aind_physiology_hsfp import __semver__
from aind_physiology_hsfp.extra_types import RoiSettings
from aind_physiology_hsfp.rig import AindPhysioHsfpRig
from aind_physiology_hsfp.task_logic import AindPhysioHsfpTaskLogic

CAMERA_APPLIED_SETTINGS = "camera_applied.json"


@dataclasses.dataclass
class TiffStackReader:
    """Reads the chunked TIFF stack (``frame_0000.tiff``, ``frame_0001.tiff``, ...) of one channel."""

    files: list[Path]

    @property
    def number_of_frames(self) -> int:
        import tifffile

        total = 0
        for file in self.files:
            with tifffile.TiffFile(file) as tif:
                total += len(tif.pages)
        return total

    def get_frames(self, frame_indices: list[int]) -> list[np.ndarray]:
        import tifffile

        offsets: list[tuple[Path, int, int]] = []
        start = 0
        for file in self.files:
            with tifffile.TiffFile(file) as tif:
                count = len(tif.pages)
            offsets.append((file, start, count))
            start += count
        frames = []
        for index in frame_indices:
            for file, first, count in offsets:
                if first <= index < first + count:
                    frames.append(tifffile.imread(file, key=index - first))
                    break
            else:
                raise IndexError(f"Frame {index} is out of range ({start} frames).")
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


def dataset(root: os.PathLike, channels: Sequence[int] = (1, 2, 3, 4, 5)) -> Dataset:
    """Data contract of one HSFP acquisition epoch.

    Args:
        root: Path to the epoch directory (``<session>/fib/hsfp_<datetime>``).
        channels: Acquired channels, i.e. the cuTTLefish channels with a light source in the rig
            (``list(rig.light_sources())``).
    """
    root = Path(root)
    return Dataset(
        version=__semver__,
        name="hsfp",
        data_streams=[
            *[_channel_stream(root, channel) for channel in channels],
            csv.Csv(
                "frame_metadata",
                reader_params=csv.CsvParams(path=root / "frame_metadata.csv", delimiter=","),
                description="One row per frame, for all channels: camera frame counter (FrameCounter), camera "
                "timestamp (Timestamp), harp time of the light source trigger (Seconds) and acquisition channel, i.e. cuTTLefish channel "
                "(Channel).",
            ),
            PydanticModel(
                "regions",
                reader_params=PydanticModel.make_params(path=root / "regions.json", model=RoiSettings),
                description="Regions of interest of each fiber at the start of the acquisition.",
            ),
            PydanticModel(
                "session_input",
                reader_params=PydanticModel.make_params(path=root / "Logs/session_input.json", model=Session),
                description="Session input parameters for the HSFP experiment.",
            ),
            PydanticModel(
                "rig_input",
                reader_params=PydanticModel.make_params(path=root / "Logs/rig_input.json", model=AindPhysioHsfpRig),
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
                        name="Regions",
                        description="An event emitted whenever the regions of interest change.",
                        reader_params=SoftwareEvents.make_params(path=root / "SoftwareEvents/Regions.jsonl"),
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
