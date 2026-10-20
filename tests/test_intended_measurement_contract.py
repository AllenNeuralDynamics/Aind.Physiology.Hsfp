"""Compact channel metadata and backwards-compatible intended-measurement inputs."""

import json
from copy import deepcopy

import pytest
from aind_data_schema.core.acquisition import Acquisition
from aind_physiology_hsfp.data_mappers import map_acquisition
from aind_physiology_hsfp.session_input import SessionInput
from pydantic import ValidationError


def test_new_saved_inputs_use_canonical_name_and_preserve_empty_semantics(recording):
    root, session, _ = recording
    saved = session.model_dump(mode="json")
    assert saved["contract_version"] == "1.1.0"
    assert "indicators_by_fiber" not in saved
    assert saved["intended_measurements_by_fiber"]["Fiber_0"]["blue"] == "None"
    assert saved["intended_measurements_by_fiber"]["Fiber_0"]["yellow"] is None
    output = json.loads(map_acquisition(root).read_text())
    Acquisition.model_validate(output)
    for stream in output["data_streams"]:
        params = stream["code"][0]["parameters"]["hsfp"]
        assert params["intended_measurements_by_fiber"] == saved["intended_measurements_by_fiber"]
        assert "indicators_by_fiber" not in params
        cords = [c for c in stream["configurations"] if c["object_type"] == "Patch cord config"]
        channels = [c for cord in cords for c in cord["channels"]]
        assert len(channels) == 10
        assert all("additional_device_names" in c and c["additional_device_names"] is None for c in channels)
        # Active optical devices are listed once at stream level, not repeated in channels.
        assert stream["active_devices"].count("Prism") == 1
        assert len(stream["active_devices"]) == len(set(stream["active_devices"]))


def test_legacy_saved_inputs_rebuild_without_rewriting_original_sources(recording):
    root, _, _ = recording
    paths = [root / "fib/Logs/session_input.json", root / "fib/segment_0001/segment_input.json"]
    for path in paths:
        source = json.loads(path.read_text())
        session = source["session"] if path.name == "segment_input.json" else source
        session["indicators_by_fiber"] = session.pop("intended_measurements_by_fiber")
        session["contract_version"] = "1.0.0"
        path.write_text(json.dumps(source))
    originals = {p: p.read_bytes() for p in paths}
    parsed = SessionInput.model_validate_json(paths[0].read_text())
    assert parsed.intended_measurements_by_fiber["Fiber_0"]["blue"] == "None"
    assert "indicators_by_fiber" not in parsed.model_dump()
    output = json.loads(map_acquisition(root).read_text())
    assert "indicators_by_fiber" not in json.dumps(output)
    assert (
        output["data_streams"][0]["code"][0]["parameters"]["hsfp"]["intended_measurements_by_fiber"]
        == parsed.intended_measurements_by_fiber
    )
    assert all(p.read_bytes() == original for p, original in originals.items())


def test_ambiguous_old_and_new_input_fields_are_rejected(recording):
    _, session, _ = recording
    values = session.model_dump(mode="json")
    values["indicators_by_fiber"] = deepcopy(values["intended_measurements_by_fiber"])
    values["indicators_by_fiber"]["Fiber_0"]["blue"] = "conflicting value"
    with pytest.raises(ValidationError):
        SessionInput.model_validate(values)


def test_pinned_schema_rejects_unregistered_measurement_map_in_configurations(recording):
    root, _, _ = recording
    output = json.loads(map_acquisition(root).read_text())
    stream = output["data_streams"][0]
    cord = next(c for c in stream["configurations"] if c["object_type"] == "Patch cord config")
    cord["intended_measurements_by_fiber"] = stream["code"][0]["parameters"]["hsfp"]["intended_measurements_by_fiber"]
    with pytest.raises(ValidationError):
        Acquisition.model_validate(output)
