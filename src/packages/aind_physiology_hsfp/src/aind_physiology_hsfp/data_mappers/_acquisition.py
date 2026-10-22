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
    InterleavedStrategy,
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
from aind_data_schema.core.procedures import Procedures
from aind_data_schema.core.subject import Subject
from aind_data_schema_models.modalities import Modality
from aind_data_schema_models.units import PowerUnit, SizeUnit, TimeUnit

from ..data_contract import atomic_json, read_events, sha256, validate_segment_layout
from ..procedures_source import fiber_implants_from_procedures
from ..session_input import CalibrationInput, recording_input

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


def _timestamp_span(path: Path) -> tuple[int, float, float]:
    """Validate one saved channel and summarize its observed camera timestamps."""
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
    return count, first, last


def observed_rate(path: Path) -> float:
    """Saved-frame rate over the recorded timestamp span; gaps remain in the denominator."""
    count, first, last = _timestamp_span(path)
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


def write_calibration_metadata(root: Path, session: CalibrationInput) -> None:
    """Create AIND calibration Subject and empty Procedures without replacing different documents."""
    models = {
        "subject.json": Subject(subject_id="calibration", subject_details=session.calibration_object),
        "procedures.json": Procedures(subject_id="calibration"),
    }
    payloads = {name: json.loads(model.model_dump_json(exclude_none=True)) for name, model in models.items()}
    for name, model in models.items():
        path = root / name
        if path.exists() and type(model).model_validate(load_json(path)) != model:
            raise ValueError(f"Existing {name} differs from confirmed calibration inputs; refusing to overwrite")
    for name, payload in payloads.items():
        path = root / name
        if not path.exists():
            atomic_json(path, payload)


def build_acquisition(root: Path) -> Acquisition:
    root = root.resolve()
    validate_segment_layout(root)
    inputs = load_json(root / "fib/Logs/session_input.json")
    session = recording_input(inputs)
    calibration = isinstance(session, CalibrationInput)
    if not session.configuration_confirmed:
        raise ValueError("Session configuration was not confirmed")
    instrument = load_json(root / "instrument_fib.json")
    Instrument.model_validate(instrument)
    if calibration:
        procedures = Procedures.model_validate(load_json(root / "procedures.json"))
        subject = Subject.model_validate(load_json(root / "subject.json"))
        if procedures.subject_id != "calibration" or procedures.subject_procedures or procedures.specimen_procedures:
            raise ValueError("Calibration requires empty Procedures for subject calibration")
        if subject.subject_id != "calibration" or subject.subject_details != session.calibration_object:
            raise ValueError("Calibration Subject differs from confirmed calibration object")
        implants = {}
    else:
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
        segment = root / "fib" / sid
        config = load_json(segment / "segment_input.json")
        saved = recording_input(config["session"])
        if isinstance(saved, CalibrationInput) != calibration:
            raise ValueError("Recording mode differs from acquisition")
        if calibration and saved.calibration_object != session.calibration_object:
            raise ValueError("Calibration object differs between segments")
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
        images = []
        timing = []
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
            rate_path = safe_path(segment, item["metadata_csv"])
            count, first, last = _timestamp_span(rate_path)
            timing.append((count, first, last))
            images.append(
                {
                    "channel_name": f"frame_ch{index}",
                    "buffer_port": item["buffer_port"],
                    "wavelength_nm": wavelength,
                    "metadata_csv": item["metadata_csv"],
                    "observed_frame_rate_hz": (count - 1) / (last - first),
                }
            )
        # All channel timestamps share this segment's camera clock. Count each
        # exposure once; the two fiber traces are present in the same image.
        camera_rate = (sum(count for count, _, _ in timing) - 1) / (
            max(last for _, _, last in timing) - min(first for _, first, _ in timing)
        )
        imaging = {
            # Serialize the concrete strategy directly: ImagingConfig's base-typed
            # sampling_strategy drops image_index_sequence in schema 2.9.1.
            "sampling_strategy": InterleavedStrategy(
                frame_rate=camera_rate, image_index_sequence=list(range(len(images)))
            ).model_dump(mode="json"),
            "images": images,
        }
        cord_configs = []
        optical_active = []
        connections = []
        inverse = {} if calibration else {name: fiber for fiber, name in saved.fiber_names.items()}
        cord_connections = (
            {name: None for name in devices if name.startswith("Mono Fiber-optic Patch Cord ")}
            if calibration else saved.connections
        )
        if not cord_connections:
            raise ValueError("No physical patch cords found in instrument")
        for cord, implant in cord_connections.items():
            if cord not in devices or (not calibration and implant not in implants):
                raise ValueError(f"Connection references unknown device: {cord} → {implant}")
            fiber = f"PatchCord_{cord.rsplit(' ', 1)[-1]}" if calibration else inverse[implant]
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
            optical_active.extend(optical_names)
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
                            additional_device_names=None,
                        )
                        for laser in lasers
                    ],
                )
            )
            if not calibration:
                connections.append(Connection(source_device=cord, target_device=implant))
        for port, laser in saved.buffer_lasers.items():
            connections.append(Connection(source_device="cuTTLefish TTL Buffer", source_port=port, target_device=laser))
        extra_active = [*config["active_device_names"], *optical_active]
        if not set(extra_active) <= set(devices):
            raise ValueError("Active device names do not resolve in instrument")
        active = list(
            dict.fromkeys(
                [
                    detector.device_name,
                    *[v.device_name for v in lasers],
                    *cord_connections,
                    *[implant for implant in cord_connections.values() if implant is not None],
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
                # Calibration has no implanted fibers or intended biological measurements.
                **({} if calibration else {"intended_measurements_by_fiber": saved.intended_measurements_by_fiber}),
                "imaging": imaging,
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
    if not calibration and session.acquisition_type == "HSFP":
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
        ethics_review_id=None if calibration else session.ethics_review_id,
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
    # Explicitly retain the requested null, while omitting other unused optional fields.
    for stream in payload["data_streams"]:
        for config in stream["configurations"]:
            if config["object_type"] == "Patch cord config":
                for channel in config["channels"]:
                    channel["additional_device_names"] = None
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
