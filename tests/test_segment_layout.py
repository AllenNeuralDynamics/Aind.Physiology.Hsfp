"""Direct segment folders agree across Python allocation, metadata and Bonsai."""

import json
from pathlib import Path

import pytest
from aind_physiology_hsfp.data_contract import allocate_segment
from aind_physiology_hsfp.data_mappers import map_acquisition


def test_allocate_direct_segments_without_overwriting_existing_payload(tmp_path):
    sid, first = allocate_segment(tmp_path)
    assert sid == "segment_0001" and first == tmp_path / "fib/segment_0001"
    payload = first / "segment_input.json"
    payload.write_text("keep me")
    sid, second = allocate_segment(tmp_path)
    assert sid == "segment_0002" and second == tmp_path / "fib/segment_0002"
    assert payload.read_text() == "keep me"
    assert not (tmp_path / "fib/segments").exists()


def test_bonsai_reserved_path_matches_python_layout():
    source = (Path(__file__).parents[1] / "src/Extensions/HsfpRecording.cs").read_text()
    assert 'var segment = Path.Combine(root, "fib", SegmentId);' in source
    assert 'Path.Combine(root, "fib", "segments", SegmentId)' not in source


def test_mapper_emits_resolvable_direct_paths_for_all_segments(recording):
    from datetime import UTC, datetime

    root, _, add_segment = recording
    add_segment(datetime(2026, 10, 17, 1, 0, tzinfo=UTC))
    output = json.loads(map_acquisition(root).read_text())
    for i, stream in enumerate(output["data_streams"], 1):
        values = stream["code"][0]["parameters"]["hsfp"]
        assert values["source_directory"] == f"fib/segment_{i:04d}"
        assert values["camera_readback"] == f"fib/segment_{i:04d}/camera_applied.json"
        assert (root / values["source_directory"]).is_dir()
        assert (root / values["camera_readback"]).is_file()
    assert not (root / "fib/segments").exists()


def test_unmoved_old_layout_blocks_resume_and_mapping_without_changing_data(recording):
    root, _, _ = recording
    output = map_acquisition(root)
    original = output.read_bytes()
    previous = root / "fib/segments"
    previous.mkdir()
    old = previous / "segment_0001"
    (root / "fib/segment_0001").rename(old)
    snapshot = (old / "segment_input.json").read_bytes()
    for action in (allocate_segment, map_acquisition):
        with pytest.raises(ValueError, match="Move existing"):
            action(root)
    assert (old / "segment_input.json").read_bytes() == snapshot
    assert output.read_bytes() == original
    assert not (root / "fib/segment_0001").exists()
    # Manual folder move followed by offline recovery refreshes references.
    old.rename(root / "fib/segment_0001")
    previous.rmdir()
    values = json.loads(map_acquisition(root).read_text())["data_streams"][0]["code"][0]["parameters"]["hsfp"]
    assert values["source_directory"] == "fib/segment_0001"
