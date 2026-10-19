"""Offline acquisition mapper. No network access or hardware operations."""

import csv
import json
import math
from datetime import datetime
from pathlib import Path
from zoneinfo import ZoneInfo

from aind_data_schema.components.configs import (
    Channel,
    DetectorConfig,
    DeviceConfig,
    LaserConfig,
    PatchCordConfig,
    TriggerType,
)
from aind_data_schema.components.connections import Connection
from aind_data_schema.components.identifiers import Code
from aind_data_schema.core.acquisition import (
    Acquisition,
    AcquisitionSubjectDetails,
    DataStream,
)
from aind_data_schema.core.instrument import Instrument
from aind_data_schema_models.modalities import Modality
from aind_data_schema_models.units import PowerUnit, SizeUnit, TimeUnit

from ..data_contract import atomic_json, read_events, sha256
from ..procedures_source import fiber_implants_from_procedures
from ..session_input import SessionInput

REPOSITORY_URL = "https://github.com/AllenNeuralDynamics/Aind.Physiology.Hsfp"


def load_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def safe_path(root: Path, relative: str) -> Path:
    path = (root / relative).resolve()
    if not path.is_relative_to(root.resolve()):
        raise ValueError(f"Source escapes acquisition root: {relative}")
    return path


def named_devices(value) -> dict[str, dict]:
    """Find exact named objects in Instrument/Procedures, without inventing identities."""
    found = {}
    if isinstance(value, dict):
        if isinstance(value.get("name"), str):
            found[value["name"]] = value
        for child in value.values():
            found.update(named_devices(child))
    elif isinstance(value, list):
        for child in value:
            found.update(named_devices(child))
    return found


def observed_rate(path: Path) -> float:
    """Saved-frame rate over the recorded timestamp span; gaps remain in the denominator."""
    previous = first = last = None
    count = 0
    with path.open(encoding="utf-8-sig", newline="") as stream:
        rows = csv.DictReader(stream)
        if "Timestamp" not in (rows.fieldnames or []):
            raise ValueError(f"Missing camera Timestamp column: {path}")
        for row in rows:
            value = float(row["Timestamp"])
            if not math.isfinite(value) or (previous is not None and value <= previous):
                raise ValueError(f"Invalid/duplicate/nonmonotonic camera timestamp in {path}")
            if first is None:
                first = value
            previous = last = value
            count += 1
    if count < 2:
        raise ValueError(f"At least two valid timestamps needed for observed rate: {path}")
    return (count - 1) / (last - first)


def camera_config(snapshot: dict) -> DetectorConfig:
    """Decode explicit DCAM values, not dropdown indices or HC Image positions."""
    if snapshot.get("source") != "dcam_applied_readback":
        raise ValueError("Camera requires final DCAM applied readback; copied XML alone is input provenance")
    props = {int(p["id"]): p for p in snapshot["properties"]}

    def val(pid):
        if pid not in props:
            raise ValueError(f"Required applied camera property missing: {pid}")
        return props[pid]["value"]

    trigger = {1: TriggerType.INTERNAL, 2: TriggerType.EXTERNAL}.get(val(0x00100110))
    if trigger is None:
        raise ValueError("Unsupported camera trigger source; do not force software/master pulse into external")
    return DetectorConfig(
        device_name=snapshot["device_name"],
        exposure_time=val(0x001F0110) * 1000,
        exposure_time_unit=TimeUnit.MS,
        trigger_type=trigger,
        crop_offset_x=int(val(0x00402110)),
        crop_offset_y=int(val(0x00402130)),
        crop_width=int(val(0x00402120)),
        crop_height=int(val(0x00402140)),
        crop_unit=SizeUnit.PX,
    )


def _calibration(instrument: dict, name: str) -> tuple[dict, float]:
    matches = [c for c in instrument.get("calibrations", []) if c.get("device_name") == name]
    if not matches:
        raise ValueError(f"No power calibration for active laser {name}")
    latest = max(matches, key=lambda c: datetime.fromisoformat(c["calibration_date"].replace("Z", "+00:00")))
    # A single calibrated operating point is unambiguous. Curves require explicit selection.
    if len(latest.get("output", [])) != 1 or latest.get("output_unit") != "microwatt":
        raise ValueError(f"Expected one calibrated microwatt output for {name}")
    power = float(latest["output"][0])
    if not math.isfinite(power) or power < 0:
        raise ValueError(f"Invalid calibration output for {name}")
    return latest, power


def build_acquisition(root: Path) -> Acquisition:
    root = root.resolve()
    session = SessionInput.model_validate(load_json(root / "fib/Logs/session_input.json"))
    if not session.configuration_confirmed:
        raise ValueError("Session configuration was not confirmed")
    instrument = load_json(root / "instrument_fib.json")
    Instrument.model_validate(instrument)
    procedures = load_json(root / "fib/Logs/procedures_source.json")
    implants = fiber_implants_from_procedures(
        procedures, session.subject_id, report_path=root / "fib/Logs/procedures_validation.json"
    )
    devices = named_devices(instrument)
    events = read_events(root)
    ids = list(dict.fromkeys(e.segment_id for e in events if e.event == "recording_start"))
    if not ids:
        raise ValueError("No recording segments")
    streams = []
    for sid in ids:
        starts = [e for e in events if e.segment_id == sid and e.event == "recording_start"]
        ends = [e for e in events if e.segment_id == sid and e.event in {"recording_stop", "end_inferred"}]
        if len(starts) != 1 or len(ends) != 1:
            raise ValueError(f"{sid}: require one start and one trustworthy end; never substitute process exit")
        start, end = starts[0], ends[0]
        if end.event == "end_inferred":
            details = end.details
            if details.get("verified") is not True or not all(
                details.get(k) for k in ["last_sample_source", "utc_mapping_source"]
            ):
                raise ValueError("Inferred end requires verified last-sample and UTC-mapping evidence")
            for key in ["last_sample_source", "utc_mapping_source"]:
                safe_path(root, details[key]).read_bytes()
        if end.timestamp < start.timestamp:
            raise ValueError("Segment ends before its start")
        segment = root / "fib/segments" / sid
        config = load_json(segment / "segment_input.json")
        saved = SessionInput.model_validate(config["session"])
        if saved.subject_id != session.subject_id or saved.site_timezone != session.site_timezone:
            raise ValueError("Segment subject/timezone differs from acquisition")
        if not saved.configuration_confirmed:
            raise ValueError("Unconfirmed segment configuration")
        if config["instrument_sha256"] != sha256(root / "instrument_fib.json"):
            raise ValueError("Instrument snapshot changed during acquisition")
        camera_path = segment / "camera_applied.json"
        camera = load_json(camera_path)
        detector = camera_config(camera)
        if detector.device_name not in devices:
            raise ValueError("Camera identity not present in instrument")
        order = config["channel_order"]
        if len(order) != len(saved.buffer_lasers):
            raise ValueError("Channel count does not match confirmed laser map")
        if [v["buffer_port"] for v in order] != [f"IO{i}" for i in range(5)]:
            raise ValueError("Unsupported firing order; current mode c requires verified IO0..IO4 order")
        if config.get("first_frame_buffer_port") != "IO0":
            raise ValueError("Unknown first-frame phase")
        lasers = []
        calibration_sources = {}
        rates = {}
        for index, item in enumerate(order):
            name = saved.buffer_lasers[item["buffer_port"]]
            if name not in devices:
                raise ValueError(f"Unknown active laser: {name}")
            wavelength = devices[name]["wavelength"]
            cal, power = _calibration(instrument, name)
            lasers.append(
                LaserConfig(
                    device_name=name,
                    wavelength=wavelength,
                    power=power,
                    power_unit=PowerUnit.UW,
                    power_measured_at=cal["measured_at"],
                )
            )
            calibration_sources[name] = {
                "source": "instrument_fib.json",
                "calibration_date": cal["calibration_date"],
            }
            rate_path = safe_path(segment, item["metadata_csv"])
            rates[f"frame_ch{index}"] = {"wavelength_nm": wavelength, "rate_hz": observed_rate(rate_path)}
        cord_configs = []
        connections = []
        inverse = {name: fiber for fiber, name in saved.fiber_names.items()}
        for cord, implant in saved.connections.items():
            if cord not in devices or implant not in implants:
                raise ValueError(f"Connection references unknown device: {cord} → {implant}")
            fiber = inverse[implant]
            common_optics = [
                "Galaxy 8 Laser Combiner",
                "Galaxy 8 Output Fiber",
                "FC/PC MM Patch Cord",
                "Laser De-Speckler",
                "1x2 Splitter Branching Patchcord Attenuating",
                "Bundle Branching Fiber-optic Patch Cord",
                "CFI60 Plan Apochromat Lambda D 10x",
                "Slit",
                "Prism",
            ]
            cord_index = cord.rsplit(" ", 1)[-1]
            optical_names = common_optics + [
                f"Variable Optic Attenuator FC/PC MM {cord_index}",
                f"Wideband MM Circulator {cord_index}",
            ]
            if any(name not in devices for name in optical_names):
                raise ValueError("Instrument optical profile changed; update mapping rather than guess components")
            cord_configs.append(
                PatchCordConfig(
                    device_name=cord,
                    channels=[
                        Channel(
                            channel_name=f"{fiber}_{laser.wavelength}",
                            detector=detector,
                            light_sources=[laser],
                            intended_measurement=None,
                            emission_wavelength=None,
                            variable_power=False,
                            additional_device_names=[DeviceConfig(device_name=n) for n in optical_names],
                        )
                        for laser in lasers
                    ],
                )
            )
            connections.append(Connection(source_device=cord, target_device=implant))
        for port, laser in saved.buffer_lasers.items():
            connections.append(Connection(source_device="cuTTLefish TTL Buffer", source_port=port, target_device=laser))
        optical_active = [
            v.device_name for cord in cord_configs for c in cord.channels for v in (c.additional_device_names or [])
        ]
        extra_active = [*config["active_device_names"], *optical_active]
        if not set(extra_active) <= set(devices):
            raise ValueError("Active device names do not resolve in instrument")
        active = list(
            dict.fromkeys(
                [
                    detector.device_name,
                    *[v.device_name for v in lasers],
                    *saved.connections,
                    *saved.connections.values(),
                    *extra_active,
                ]
            )
        )
        acquisition_code = config["acquisition_code"]
        if not acquisition_code.get("commit_hash"):
            raise ValueError("Acquisition code commit identity is missing")
        # Keep the agreed acquisition settings here; detailed evidence remains in saved sources.
        parameters = {
            "hsfp": {
                "segment_id": sid,
                "indicators_by_fiber": saved.indicators_by_fiber,
                "channel_order": order,
                "observed_per_channel_rate_hz": rates,
                "power_calibration_sources": calibration_sources,
                "camera_readback": camera_path.relative_to(root).as_posix(),
                "writer": config["writer"],
                "firmware": config["firmware"],
                "lookup_table": config["lookup_table"],
                "source_directory": segment.relative_to(root).as_posix(),
            }
        }
        if "dirty" in acquisition_code:
            parameters["hsfp"]["acquisition_provenance"] = {"dirty": acquisition_code["dirty"]}
        if end.event == "end_inferred":
            parameters["hsfp"]["end_provenance"] = {
                "event": end.event, "source": end.source, "details": end.details,
            }
        streams.append(
            DataStream(
                stream_start_time=start.timestamp,
                stream_end_time=end.timestamp,
                modalities=[Modality.FIB],
                active_devices=active,
                # The pinned FIB validator requires a stream-level light-source config;
                # channel-nested light sources do not satisfy that requirement.
                configurations=[*lasers, *cord_configs],
                connections=connections,
                code=[
                    Code(
                        url=REPOSITORY_URL,
                        name="Aind.Physiology.Hsfp",
                        run_script="src/main.bonsai",
                        commit_hash=acquisition_code["commit_hash"],
                        parameters=parameters,
                    )
                ],
            )
        )
    streams.sort(key=lambda s: s.stream_start_time)
    if any(b.stream_start_time < a.stream_end_time for a, b in zip(streams, streams[1:])):
        raise ValueError("Recording segments overlap")
    notes = "\n\n".join(v for v in [session.notes_before, session.notes_after] if v) or None
    tz = ZoneInfo(session.site_timezone)
    # Only platform is supplied by standalone HSFP. Override schema's default unit values to null.
    subject_details = None
    if session.acquisition_type == "HSFP":
        subject_details = AcquisitionSubjectDetails(mouse_platform_name=session.platform_name)
        for field in type(subject_details).model_fields:
            if field not in {"mouse_platform_name", "object_type", "weight_unit"}:
                setattr(subject_details, field, None)
    return Acquisition(
        subject_id=session.subject_id,
        instrument_id=instrument["instrument_id"],
        acquisition_start_time=streams[0].stream_start_time.astimezone(tz),
        acquisition_start_tz=session.site_timezone,
        acquisition_end_time=streams[-1].stream_end_time.astimezone(tz),
        experimenters=session.experimenters,
        acquisition_type=session.acquisition_type,
        ethics_review_id=session.ethics_review_id,
        notes=notes,
        subject_details=subject_details,
        data_streams=streams,
        stimulus_epochs=[],
        manipulations=[],
        maintenance=[],
        calibrations=[],
    )


def acquisition_payload(model: Acquisition) -> dict:
    """Omit unused model fields while retaining null values inside the biological map."""
    # Pydantic's exclude_none omits model fields, not entries in nested ordinary dicts.
    payload = json.loads(model.model_dump_json(exclude_none=True))
    # All other standalone subject details were explicitly agreed to remain null.
    if model.subject_details is not None:
        payload["subject_details"] = json.loads(model.subject_details.model_dump_json())
    # Required non-null schema default does not represent a weight measurement; omit the unused unit.
    if (
        payload.get("subject_details")
        and payload["subject_details"]["animal_weight_prior"] is None
        and payload["subject_details"]["animal_weight_post"] is None
    ):
        payload["subject_details"].pop("weight_unit", None)
    roundtrip = Acquisition.model_validate(payload)
    if str(roundtrip.acquisition_start_tz) != str(model.acquisition_start_tz):
        raise ValueError("Local timezone lost on serialization")
    return payload


def map_acquisition(root: Path) -> Path:
    # All source checks and the serialized schema round-trip precede replacement.
    payload = acquisition_payload(build_acquisition(root))
    target = root / "acquisition_fib.json"
    atomic_json(target, payload)
    return target
