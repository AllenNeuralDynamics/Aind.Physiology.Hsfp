"""Maps an HSFP session to an aind-data-schema Acquisition.

Each HSFP epoch in the session (``<session>/fib/hsfp_<datetime>``) becomes one data stream.

Classes:
    AindAcquisitionDataMapper: Maps a session directory to an ``Acquisition``.
"""

import json
import logging
import math
import os
import platform
from datetime import datetime
from pathlib import Path
from typing import Any, cast

import git
import pandas as pd
import pydantic
from aind_behavior_services.session import Session
from aind_data_schema.components import configs
from aind_data_schema.components.connections import Connection
from aind_data_schema.components.identifiers import Code
from aind_data_schema.core import acquisition
from aind_data_schema_models.modalities import Modality
from aind_data_schema_models.units import PowerUnit, SizeUnit, TimeUnit
from aind_physiology_hsfp import __semver__
from aind_physiology_hsfp.data_contract import dataset
from aind_physiology_hsfp.rig import AindPhysioHsfpRig, LightSource
from aind_physiology_hsfp.task_logic import AindPhysioHsfpTaskLogic, Measurement
from clabe.data_mapper.helpers import snapshot_bonsai_environment

from aind_physiology_hsfp_data_mappers._base import DataMapper
from aind_physiology_hsfp_data_mappers._utils import (
    COMMON_OPTICS,
    TTL_BUFFER,
    fiber_name,
    patch_cord_name,
    patch_cord_optics,
)

logger = logging.getLogger(__name__)

# DCAM property ids, see the DCAM-API reference.
_DCAM_TRIGGER_SOURCE = 0x00100110
_DCAM_EXPOSURE_TIME = 0x001F0110
_DCAM_SUBARRAY_HPOS = 0x00402110
_DCAM_SUBARRAY_HSIZE = 0x00402120
_DCAM_SUBARRAY_VPOS = 0x00402130
_DCAM_SUBARRAY_VSIZE = 0x00402140


class _HsfpEpoch(pydantic.BaseModel):
    """Inputs and timing of one HSFP epoch, which becomes one data stream."""

    model_config = pydantic.ConfigDict(arbitrary_types_allowed=True)

    path: Path
    start_time: pydantic.AwareDatetime
    end_time: pydantic.AwareDatetime
    session: Session
    rig: AindPhysioHsfpRig
    task_logic: AindPhysioHsfpTaskLogic
    detector: configs.DetectorConfig
    frame_rate: float
    lasers: dict[int, dict[str, Any]]
    """The laser of each light source, keyed by cuTTLefish channel: its serial number, model and wavelength, and the
    value of its first GetState reply ("state"), from the rows of the Lasers software event."""


class AindAcquisitionDataMapper(DataMapper[acquisition.Acquisition]):
    """Maps an HSFP session directory to an aind-data-schema ``Acquisition``."""

    def __init__(
        self,
        data_path: os.PathLike,
        repository_path: os.PathLike,
        instrument_path: os.PathLike | None = None,
        session_end_time: datetime | None = None,
        acquisition_type: str = "",
    ):
        """Create a mapper for one session directory.

        Args:
            data_path: Session directory holding the ``fib/hsfp_*`` acquisition epochs.
            repository_path: Root of the Aind.Physiology.Hsfp checkout the session was acquired with. Its remote,
                commit and Bonsai environment are recorded as the code that produced the data.
            instrument_path: Optional instrument metadata (``instrument_fib.json``). When given, each laser is
                named after the instrument component with its serial number, device names are checked against it,
                and laser powers not set in the rig are taken from its latest calibrations. Without it, each laser is
                named after the model and serial number it reports.
            session_end_time: When the session ended, as known to whoever launched it. Left unset, the acquisition
                ends with its last data stream.
            acquisition_type: Acquisition type. Left blank by default, as the behavior task HSFP runs alongside
                supplies it.
        """
        self._data_path = Path(data_path)
        self._repository_path = Path(repository_path)
        self._instrument = (
            json.loads(Path(instrument_path).read_text(encoding="utf-8-sig")) if instrument_path else None
        )
        self._session_end_time = session_end_time
        self._acquisition_type = acquisition_type
        self._mapped: acquisition.Acquisition | None = None

    def map(self) -> acquisition.Acquisition:
        """Map the session into an AIND acquisition."""
        logger.info("Mapping HSFP acquisition to AIND schema.")
        self._mapped = self._map()
        return self._mapped

    def _map(self) -> acquisition.Acquisition:
        paths = sorted(path for path in (self._data_path / "fib").glob("hsfp_*") if path.is_dir())
        epochs = [self._read_epoch(path) for path in paths]
        if not epochs:
            raise ValueError(f"No HSFP epoch found under {self._data_path / 'fib'}.")
        session = epochs[0].session
        if any(epoch.session.subject != session.subject for epoch in epochs):
            raise ValueError("All epochs of a session must have the same subject.")

        code = self._get_code()
        return acquisition.Acquisition(
            subject_id=session.subject,
            acquisition_start_time=min(epoch.start_time for epoch in epochs),
            acquisition_end_time=self._session_end_time or max(epoch.end_time for epoch in epochs),
            experimenters=session.experimenter,
            notes=session.notes,
            instrument_id=epochs[0].rig.rig_name,
            acquisition_type=self._acquisition_type,
            data_streams=[self._get_data_stream(epoch, code) for epoch in epochs],
        )

    @staticmethod
    def _read_epoch(path: Path) -> _HsfpEpoch:
        contract = dataset(path)
        rig = cast(AindPhysioHsfpRig, contract["rig_input"].read())
        channels = list(rig.light_sources())
        start_time, end_time = AindAcquisitionDataMapper._read_session_times(contract)
        return _HsfpEpoch(
            path=path,
            start_time=start_time,
            end_time=end_time,
            session=cast(Session, contract["session_input"].read()),
            rig=rig,
            task_logic=cast(AindPhysioHsfpTaskLogic, contract["task_input"].read()),
            detector=detector_config(contract["camera_applied"].read()),
            frame_rate=AindAcquisitionDataMapper._camera_frame_rate(contract, channels),
            lasers=AindAcquisitionDataMapper._read_lasers(contract, rig),
        )

    @staticmethod
    def _read_lasers(contract, rig: AindPhysioHsfpRig) -> dict[int, dict[str, Any]]:
        """The laser of each light source, from the rows of the Lasers software event (one row per reply of a laser
        or of its laser box), checked against the rig's serial numbers."""
        stream = contract["SoftwareEvents"]["Lasers"]
        events = stream.read()
        if events.empty:
            raise ValueError(f"No Lasers software event in {stream.reader_params.path}.")
        lasers: dict[int, dict[str, Any]] = {}
        for row in events["data"]:
            channel = int(row["channel"])
            laser = lasers.setdefault(
                channel,
                {
                    "serial_number": row["serial_number"],
                    "model": row.get("model"),
                    "wavelength": row["wavelength"],
                    "state": None,
                },
            )
            if laser["state"] is None and row.get("device") == "laser" and row.get("command") == "GetState":
                laser["state"] = row.get("value")
        for channel, source in rig.light_sources().items():
            laser = lasers.get(channel)
            if laser is None:
                raise ValueError(f"The Lasers software event has no laser for channel {channel}.")
            if str(laser["serial_number"]).strip().lower() != source.serial_number.strip().lower():
                raise ValueError(
                    f"Channel {channel}: the rig's laser is S/N {source.serial_number}, but the Lasers software event "
                    f"reports S/N {laser['serial_number']}."
                )
        return lasers

    @staticmethod
    def _read_session_times(contract) -> tuple[datetime, datetime]:
        """Start and end (UTC) of an epoch, from the StartSessionTime and EndSessionTime software events."""
        times = []
        for name in ("StartSessionTime", "EndSessionTime"):
            events = contract["SoftwareEvents"][name].read()
            if events.empty:
                raise ValueError(f"No {name} software event in {contract['SoftwareEvents'][name].reader_params.path}.")
            times.append(pd.Timestamp(events["data"].iloc[0]).to_pydatetime())
        return times[0], times[1]

    @staticmethod
    def _camera_frame_rate(contract, channels: list[int]) -> float:
        """Observed camera frame rate (Hz), counting the frames of all channels on the camera clock."""
        metadata = contract["frame_metadata"].read()
        missing = sorted(set(channels) - set(metadata["Channel"].astype(int)))
        if missing:
            raise ValueError(f"No frames recorded for channels {missing}.")
        timestamps = metadata["Timestamp"].to_numpy(dtype=float)
        if len(timestamps) < 2:
            raise ValueError("At least two frames are needed to compute the frame rate.")
        rate = (len(timestamps) - 1) / (timestamps.max() - timestamps.min())
        if not math.isfinite(rate) or rate <= 0:
            raise ValueError(f"Invalid camera frame rate {rate}.")
        return rate

    def _get_code(self) -> list[Code]:
        """The workflow and the package that produced the session, at the commit it was acquired with."""
        repository = git.Repo(self._repository_path)
        if repository.working_tree_dir is None:
            raise ValueError(f"{self._repository_path} has no working tree to read the Bonsai environment from.")
        url = repository.remote().url
        commit_hash = repository.head.commit.hexsha
        bonsai = snapshot_bonsai_environment(Path(repository.working_tree_dir) / ".bonsai" / "Bonsai.config")
        return [
            Code(
                url=url,
                name="Aind.Physiology.Hsfp",
                version=__semver__,
                commit_hash=commit_hash,
                language="Bonsai",
                language_version=bonsai.get("Bonsai"),
                run_script=Path("src/main.bonsai"),
            ),
            Code(
                url=url,
                name="aind-physiology-hsfp",
                version=__semver__,
                commit_hash=commit_hash,
                language="Python",
                language_version=platform.python_version(),
            ),
        ]

    def _get_data_stream(self, epoch: _HsfpEpoch, code: list[Code]) -> acquisition.DataStream:
        """One epoch: the patch cords acquired, and how each of their channels was excited and imaged."""
        sources = epoch.rig.light_sources()
        lasers = [self._get_laser_config(source, epoch.lasers[channel]) for channel, source in sources.items()]
        measurements = self._get_recorded_measurements(epoch)

        configurations: list = [*lasers]
        connections = [
            Connection(source_device=TTL_BUFFER, source_port=f"IO{channel}", target_device=laser.device_name)
            for channel, laser in zip(sources, lasers, strict=True)
        ]
        active_devices = [epoch.detector.device_name, *(laser.device_name for laser in lasers), TTL_BUFFER]
        active_devices.extend(COMMON_OPTICS)

        for measurement in measurements:
            patch_cord, implant = patch_cord_name(measurement.patch_cord), fiber_name(measurement.fiber_number)
            active_devices.extend([patch_cord, *patch_cord_optics(measurement.patch_cord), implant])
            connections.append(Connection(source_device=patch_cord, target_device=implant))
            configurations.append(
                configs.PatchCordConfig(
                    device_name=patch_cord,
                    channels=[
                        configs.Channel(
                            channel_name=f"{implant}_{laser.wavelength}",
                            detector=epoch.detector,
                            light_sources=[laser],
                            variable_power=False,
                        )
                        for laser in lasers
                    ],
                )
            )
        self._check_devices_in_instrument(active_devices)

        # The pinned FIB schema has no structured field for the intended measurement of each color band, nor for
        # the interleaved imaging of each laser, so these are recorded as acquisition parameters.
        parameters: dict[str, Any] = {
            "hsfp": {
                "epoch": epoch.path.name,
                "intended_measurements_by_fiber": {
                    fiber_name(m.fiber_number): m.intended_measurements.model_dump() for m in measurements
                },
                "imaging": {
                    "sampling_strategy": configs.InterleavedStrategy(
                        frame_rate=epoch.frame_rate, image_index_sequence=list(range(len(lasers)))
                    ).model_dump(mode="json"),
                    "channels": [
                        {
                            "channel": channel,
                            "light_source": laser.device_name,
                            "cuttlefish_channel": channel,
                            **_laser_settings(epoch.lasers[channel]),
                        }
                        for channel, laser in zip(sources, lasers, strict=True)
                    ],
                },
                "tiff_chunk_size": epoch.rig.tiff_chunk_size,
            }
        }
        stream_code = [code[0].model_copy(update={"parameters": parameters}), *code[1:]]

        return acquisition.DataStream(
            stream_start_time=epoch.start_time,
            stream_end_time=epoch.end_time,
            modalities=[Modality.FIB],
            code=stream_code,
            active_devices=list(dict.fromkeys(active_devices)),
            configurations=configurations,
            connections=connections,
        )

    def _get_laser_config(self, source: LightSource, laser: dict[str, Any]) -> configs.LaserConfig:
        """Light source settings. The name comes from the instrument component with the laser's serial number (or
        else from what the laser reports), the wavelength from the laser, and the power from the rig, or else from
        the instrument's calibrations."""
        if self._instrument is not None:
            name = _instrument_laser_name(self._instrument, source.serial_number)
        else:
            name = f"{laser.get('model') or 'OBIS laser'} S/N {source.serial_number}"
        power, measured_at = source.power, None
        if power is None and self._instrument is not None:
            power, measured_at = _calibrated_power(self._instrument, name)
        return configs.LaserConfig(
            device_name=name,
            wavelength=round(float(laser["wavelength"])),
            power=power,
            power_unit=PowerUnit.UW if power is not None else None,
            power_measured_at=measured_at,
        )

    @staticmethod
    def _get_recorded_measurements(epoch: _HsfpEpoch) -> list[Measurement]:
        """The enabled measurements. aind-data-schema requires at least one patch cord in a FIB stream."""
        measurements = [m for m in epoch.task_logic.task_parameters.measurements if m.enabled]
        if not measurements:
            raise ValueError(
                f"No enabled measurement in {epoch.path.name}, and aind-data-schema requires at least one patch cord "
                "in a fiber photometry stream."
            )
        return measurements

    def _check_devices_in_instrument(self, device_names: list[str]) -> None:
        if self._instrument is None:
            return
        known = _named_devices(self._instrument)
        missing = sorted({name for name in device_names if name not in known and not name.startswith("Fiber_")})
        if missing:
            raise ValueError(f"Devices not found in the instrument metadata: {missing}")


def detector_config(snapshot: dict) -> configs.DetectorConfig:
    """Camera settings, decoded from the DCAM properties the camera applied (``camera_applied.json``)."""
    if snapshot.get("source") != "dcam_applied_readback":
        raise ValueError("Camera settings require the DCAM applied readback (camera_applied.json).")
    props = {int(p["id"]): p["value"] for p in snapshot["properties"]}

    def value(pid: int) -> float:
        if pid not in props:
            raise ValueError(f"Required applied camera property missing: {pid:#010x}")
        return props[pid]

    trigger = {1: configs.TriggerType.INTERNAL, 2: configs.TriggerType.EXTERNAL}.get(int(value(_DCAM_TRIGGER_SOURCE)))
    if trigger is None:
        raise ValueError("Unsupported camera trigger source.")
    return configs.DetectorConfig(
        device_name=snapshot["device_name"],
        exposure_time=value(_DCAM_EXPOSURE_TIME) * 1000,
        exposure_time_unit=TimeUnit.MS,
        trigger_type=trigger,
        crop_offset_x=int(value(_DCAM_SUBARRAY_HPOS)),
        crop_offset_y=int(value(_DCAM_SUBARRAY_VPOS)),
        crop_width=int(value(_DCAM_SUBARRAY_HSIZE)),
        crop_height=int(value(_DCAM_SUBARRAY_VSIZE)),
        crop_unit=SizeUnit.PX,
    )


def _named_devices(value: Any) -> set[str]:
    """Names of every named object in the instrument metadata."""
    found = set()
    if isinstance(value, dict):
        if isinstance(value.get("name"), str):
            found.add(value["name"])
        for child in value.values():
            found |= _named_devices(child)
    elif isinstance(value, list):
        for child in value:
            found |= _named_devices(child)
    return found


def _instrument_laser_name(instrument: dict, serial_number: str) -> str:
    """Name of the instrument component with a laser's serial number."""
    wanted = serial_number.strip().lower()
    for component in instrument.get("components", []):
        if isinstance(component, dict) and str(component.get("serial_number") or "").strip().lower() == wanted:
            return component["name"]
    raise ValueError(f"No component with serial number {serial_number} in the instrument metadata.")


def _laser_settings(laser: dict[str, Any]) -> dict[str, Any]:
    """Settings of a laser in the session, from its first GetState reply: serial number, power setpoint, emission and
    operating mode. A setting the laser did not report is None."""
    state = laser.get("state")
    if not isinstance(state, dict):
        state = {}
    setpoint = state.get("PowerSetpoint")
    return {
        "serial_number": laser.get("serial_number"),
        "model": laser.get("model"),
        "power_setpoint_w": float(setpoint) if isinstance(setpoint, (int, float)) else None,
        "emission": state.get("Emission"),
        "operating_mode": state.get("OperatingMode"),
    }


def _calibrated_power(instrument: dict, laser_name: str) -> tuple[float, str | None]:
    """Power (uW) of the latest single-point calibration of a laser in the instrument metadata."""
    calibrations = [c for c in instrument.get("calibrations", []) if c.get("device_name") == laser_name]
    if not calibrations:
        raise ValueError(f"No power calibration for laser {laser_name} in the instrument metadata.")
    latest = max(calibrations, key=lambda c: datetime.fromisoformat(c["calibration_date"]))
    if len(latest.get("output", [])) != 1 or latest.get("output_unit") != "microwatt":
        raise ValueError(f"Expected a single calibrated output in microwatt for laser {laser_name}.")
    return float(latest["output"][0]), latest.get("measured_at")
