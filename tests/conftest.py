"""Complete synthetic recording bundles; no TIFF payloads or live services required."""

import json
import shutil
from datetime import datetime, timedelta, timezone
from pathlib import Path

import pytest
from aind_physiology_hsfp.data_contract import (
    allocate_segment,
    append_event,
    atomic_json,
    sha256,
)
from aind_physiology_hsfp.session_input import (
    INDICATOR_COLORS,
    JournalEvent,
    SessionInput,
)


@pytest.fixture
def recording(tmp_path):
    root = tmp_path / ".hsfp-pending-test"
    root.mkdir()
    shutil.copy2(Path(__file__).parent / "fixtures/instrument_fib.json", root / "instrument_fib.json")
    procedures = json.loads((Path(__file__).parent / "fixtures/procedures.json").read_text())
    atomic_json(root / "fib/Logs/procedures_source.json", procedures)
    session = SessionInput(
        subject_id="876770",
        experimenters=["Operator"],
        ethics_review_id=["1234"],
        ethics_source="operator",
        intended_measurements_by_fiber={
            f"Fiber_{n}": {**dict.fromkeys(INDICATOR_COLORS), "blue": "None", "green": "glutamate"} for n in [0, 1]
        },
        fiber_names={"Fiber_0": "Implant 0", "Fiber_1": "Implant 1"},
        connections={"Mono Fiber-optic Patch Cord 0": "Implant 1", "Mono Fiber-optic Patch Cord 1": "Implant 0"},
        configuration_confirmed=True,
    )
    atomic_json(root / "fib/Logs/session_input.json", session.model_dump(mode="json"))

    def add_segment(start=datetime(2026, 10, 17, 0, 30, tzinfo=timezone.utc), stop=True):
        sid, segment = allocate_segment(root)
        config = {
            "session": session.model_dump(mode="json"),
            "instrument_sha256": sha256(root / "instrument_fib.json"),
            "channel_order": [{"buffer_port": f"IO{i}", "metadata_csv": f"fib/metadata_ch{i}.csv"} for i in range(5)],
            "first_frame_buffer_port": "IO0",
            "active_device_names": ["Teensy 4.1", "cuTTLefish TTL Buffer"],
            "acquisition_code": {"commit_hash": "a" * 40, "dirty": False},
            "writer": {"format": "BigTIFF", "compression": "none", "chunk_size": 1000},
            "firmware": {"mode": "c"},
            "lookup_table": {"supplied": False},
        }
        atomic_json(segment / "segment_input.json", config)
        props = {0x00100110: 2, 0x001F0110: 0.004, 0x00402110: 64, 0x00402130: 884, 0x00402120: 1972, 0x00402140: 232}
        atomic_json(
            segment / "camera_applied.json",
            {
                "source": "dcam_applied_readback",
                "device_name": "Hamamatsu Orca Flash 4 v3",
                "properties": [{"id": pid, "value": value} for pid, value in props.items()],
            },
        )
        (segment / "fib").mkdir()
        for n in range(5):
            (segment / f"fib/metadata_ch{n}.csv").write_text(
                "FrameCounter,Timestamp\n0,0.0\n5,0.0333333333333\n10,0.0666666666667\n"
            )
        append_event(root, JournalEvent(segment_id=sid, event="recording_start", timestamp=start))
        if stop:
            append_event(
                root, JournalEvent(segment_id=sid, event="recording_stop", timestamp=start + timedelta(minutes=1))
            )
        return sid, segment

    add_segment()
    return root, session, add_segment
