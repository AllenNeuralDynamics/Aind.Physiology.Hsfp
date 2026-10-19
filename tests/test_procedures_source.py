import json
from copy import deepcopy

import pytest
from aind_data_schema.core.acquisition import Acquisition
from aind_physiology_hsfp.data_mappers import map_acquisition
from aind_physiology_hsfp.procedures_source import fiber_implants_from_procedures


def test_unrelated_viral_errors_preserve_source_and_allow_valid_acquisition(recording, caplog):
    root, _, _ = recording
    path = root / "fib/Logs/procedures_source.json"
    source = json.loads(path.read_text())
    source["subject_procedures"][0]["procedures"].append({
        "object_type": "Brain injection",
        "injection_materials": [{"object_type": "Viral material", "name": None}],
        "dynamics": [], "coordinates": [], "coordinate_system_name": "TEST",
    })
    path.write_text(json.dumps(source))
    original = path.read_bytes()
    output = json.loads(map_acquisition(root).read_text())
    Acquisition.model_validate(output)
    assert path.read_bytes() == original
    report = json.loads((root / "fib/Logs/procedures_validation.json").read_text())
    assert report["full_document_schema_valid"] is False
    assert report["required_fields_valid"] is True
    assert report["source_modified"] is False
    assert len(report["full_document_schema_errors"]) == 1
    assert report["full_document_schema_errors"][0]["loc"][-1] == "name"
    assert "upstream schema errors" in caplog.text
    assert output["data_streams"][0]["code"][0]["parameters"]["hsfp"]["procedures_validation_report"] == (
        "fib/Logs/procedures_validation.json"
    )
    # Rebuilding with identical sources must not alter the scientific document or its report.
    before = (root / "acquisition_fib.json").read_bytes()
    map_acquisition(root)
    assert (root / "acquisition_fib.json").read_bytes() == before


@pytest.mark.parametrize("case", [
    "wrong_subject", "no_fibers", "duplicate_names", "missing_name", "missing_diameter",
    "missing_config", "config_name_mismatch", "unknown_probe_type", "missing_device",
])
def test_invalid_required_procedures_inputs_keep_previous_acquisition(recording, case):
    root, _, _ = recording
    output = map_acquisition(root)
    previous = output.read_bytes()
    path = root / "fib/Logs/procedures_source.json"
    source = json.loads(path.read_text())
    implants = source["subject_procedures"][0]["procedures"]
    if case == "wrong_subject":
        source["subject_id"] = "another-subject"
    elif case == "no_fibers":
        implants.clear()
    elif case == "duplicate_names":
        implants[1]["implanted_device"]["name"] = "Implant 0"
        implants[1]["device_config"]["device_name"] = "Implant 0"
    elif case == "missing_name":
        implants[0]["implanted_device"]["name"] = None
    elif case == "missing_diameter":
        implants[0]["implanted_device"].pop("core_diameter")
    elif case == "missing_config":
        implants[0].pop("device_config")
    elif case == "config_name_mismatch":
        implants[0]["device_config"]["device_name"] = "different-fiber"
    elif case == "unknown_probe_type":
        implants[0]["implanted_device"].pop("object_type")
    elif case == "missing_device":
        implants[0].pop("implanted_device")
    path.write_text(json.dumps(source))
    source_bytes = path.read_bytes()
    with pytest.raises(ValueError, match="Procedures required subject/fiber validation failed"):
        map_acquisition(root)
    assert output.read_bytes() == previous
    assert path.read_bytes() == source_bytes
    report = json.loads((root / "fib/Logs/procedures_validation.json").read_text())
    assert report["required_fields_valid"] is False
    assert report["required_field_errors"]


def test_schema_migrations_do_not_mutate_source_dictionary(recording):
    root, _, _ = recording
    source = json.loads((root / "fib/Logs/procedures_source.json").read_text())
    config = source["subject_procedures"][0]["procedures"][0]["device_config"]
    config["coordinate_system"] = config.pop("local_coordinate_system")
    previous = deepcopy(source)
    devices = fiber_implants_from_procedures(source, "876770")
    assert set(devices) == {"Implant 0", "Implant 1"}
    assert source == previous
