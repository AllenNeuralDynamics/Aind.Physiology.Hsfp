"""A synthetic HSFP session, laid out as the Bonsai workflow writes it. No raw frames are included."""

import json
import sys
from datetime import UTC, datetime, timedelta
from pathlib import Path

import pytest
from aind_behavior_services.data_types import SoftwareEvent

sys.path.append(str(Path(__file__).parents[1]))
from examples import example

FIXTURES = Path(__file__).parent / "fixtures"
START = datetime(2026, 10, 17, 0, 30, tzinfo=UTC)

# DCAM trigger source (external), exposure (s), and subarray offset/size.
CAMERA_PROPERTIES = {
    0x00100110: 2,
    0x001F0110: 0.004,
    0x00402110: 64,
    0x00402130: 884,
    0x00402120: 1972,
    0x00402140: 232,
}


def _software_event(name: str, data, timestamp: float) -> str:
    event = SoftwareEvent(name=name, data=data, timestamp=timestamp, timestamp_source="harp")
    return event.model_dump_json() + "\n"


def write_epoch(session_root: Path, start: datetime, rig=None, task_logic=None) -> Path:
    epoch = session_root / "fib" / f"hsfp_{start:%Y-%m-%dT%H%M%S}"
    rig = rig or example.mock_rig()
    logs = epoch / "Logs"
    logs.mkdir(parents=True)
    for name, model in [
        ("session_input", example.mock_session()),
        ("rig_input", rig),
        ("task_input", task_logic or example.mock_task_logic()),
    ]:
        (logs / f"{name}.json").write_text(model.model_dump_json(indent=2), encoding="utf-8")

    events = epoch / "SoftwareEvents"
    events.mkdir()
    end = start + timedelta(minutes=1)
    (events / "StartSessionTime.jsonl").write_text(_software_event("StartSessionTime", start.isoformat(), 1.0))
    (events / "EndSessionTime.jsonl").write_text(_software_event("EndSessionTime", end.isoformat(), 61.0))

    (epoch / "camera_applied.json").write_text(
        json.dumps(
            {
                "source": "dcam_applied_readback",
                "device_name": rig.camera.device_name,
                "properties": [{"id": pid, "value": value} for pid, value in CAMERA_PROPERTIES.items()],
            }
        ),
        encoding="utf-8",
    )
    # The light sources of the rig interleaved at 150 Hz on the camera clock, tagged with their cuTTLefish channel.
    channels = list(rig.light_sources())
    rows = ["FrameCounter,Timestamp,Seconds,Channel"]
    for frame in range(10 * len(channels)):
        rows.append(f"{frame},{frame / 150:.9f},{1 + frame / 150:.9f},{channels[frame % len(channels)]}")
    (epoch / "frame_metadata.csv").write_text("\n".join(rows) + "\n", encoding="utf-8")
    return epoch


@pytest.fixture
def session_root(tmp_path) -> Path:
    root = tmp_path / "test_2026-10-17T003000Z"
    write_epoch(root, START)
    return root


@pytest.fixture
def instrument_path() -> Path:
    return FIXTURES / "instrument_fib.json"


@pytest.fixture
def session_start() -> datetime:
    return START


@pytest.fixture
def make_epoch():
    return write_epoch
