import json
from datetime import datetime, timezone

import pytest
from aind_physiology_hsfp.data_contract import canonical_name, finalize_root
from aind_physiology_hsfp.data_mappers import build_acquisition, map_acquisition
from aind_physiology_hsfp.data_mappers._acquisition import observed_rate


def test_swapped_connections_color_semantics_and_shared_camera(recording):
    root, _, _ = recording
    acquisition = build_acquisition(root)
    stream = acquisition.data_streams[0]
    cords = [v for v in stream.configurations if v.object_type == "Patch cord configuration"]
    # Object-type strings are schema-owned; use channels capability for clarity.
    cords = [v for v in stream.configurations if hasattr(v, "channels")]
    assert cords[0].device_name == "Mono Fiber-optic Patch Cord 0"
    assert [c.channel_name for c in cords[0].channels] == [f"Fiber_1_{w}" for w in [405, 445, 488, 514, 561]]
    assert all(c.detector.device_name == "Hamamatsu Orca Flash 4 v3" for cord in cords for c in cord.channels)
    assert all(
        c.intended_measurement is None and c.emission_wavelength is None for cord in cords for c in cord.channels
    )
    values = stream.code[0].parameters.model_dump()["hsfp"]["intended_measurements_by_fiber"]
    assert values["Fiber_0"]["blue"] == "None"
    assert values["Fiber_0"]["yellow"] is None
    assert len(stream.configurations) == 7


def test_timezone_roundtrip_and_empty_fields(recording):
    root, _, _ = recording
    output = json.loads(map_acquisition(root).read_text())
    assert output["acquisition_start_tz"] == "America/Los_Angeles"
    assert output["acquisition_start_time"].startswith("2026-10-16T17:30")
    assert output["subject_details"]["mouse_platform_name"] == "mouse_tube_foraging"
    assert output["subject_details"]["animal_weight_prior"] is None
    assert "weight_unit" not in output["subject_details"]
    assert output["manipulations"] == output["maintenance"] == output["stimulus_epochs"] == []


def test_multisegment_bounds_and_observed_rates(recording):
    root, _, add_segment = recording
    add_segment(datetime(2026, 10, 17, 1, 0, tzinfo=timezone.utc))
    acq = build_acquisition(root)
    assert len(acq.data_streams) == 2
    assert (acq.data_streams[1].stream_start_time - acq.data_streams[0].stream_end_time).total_seconds() == 1740
    parameters = acq.data_streams[0].code[0].parameters.model_dump()["hsfp"]
    assert parameters["observed_per_channel_rate_hz"]["frame_ch0"]["rate_hz"] == pytest.approx(30)
    assert "configured_camera_trigger_rate_hz" not in parameters


def test_incomplete_segment_does_not_replace_valid_output(recording):
    root, _, add_segment = recording
    output = map_acquisition(root)
    original = output.read_bytes()
    add_segment(datetime(2026, 10, 17, 1, 0, tzinfo=timezone.utc), stop=False)
    with pytest.raises(ValueError, match="trustworthy end"):
        map_acquisition(root)
    assert output.read_bytes() == original


def test_camera_xml_is_not_silently_treated_as_readback(recording):
    root, _, _ = recording
    path = root / "fib/segment_0001/camera_applied.json"
    d = json.loads(path.read_text())
    d["source"] = "input_xml"
    path.write_text(json.dumps(d))
    with pytest.raises(ValueError, match="applied readback"):
        build_acquisition(root)


def test_naming_local_date_dst_and_no_overwrite(recording):
    root, session, _ = recording
    assert canonical_name(session, datetime(2026, 10, 17, 0, 30, tzinfo=timezone.utc)) == "876770_2026-10-16_17-30-00"
    assert canonical_name(session, datetime(2026, 12, 17, 0, 30, tzinfo=timezone.utc)) == "876770_2026-12-16_16-30-00"
    target = root.parent / "876770_2026-10-16_17-30-00"
    target.mkdir()
    with pytest.raises(FileExistsError):
        finalize_root(root, session)
    target.rmdir()
    final = finalize_root(root, session)
    assert final.name == "876770_2026-10-16_17-30-00"
    assert finalize_root(final, session) == final


def test_unknown_instrument_reference_rejected(recording):
    root, _, _ = recording
    path = root / "fib/segment_0001/segment_input.json"
    d = json.loads(path.read_text())
    d["session"]["buffer_lasers"]["IO0"] = "Unknown laser"
    path.write_text(json.dumps(d))
    with pytest.raises(ValueError, match="Unknown active laser"):
        build_acquisition(root)


def test_nonmonotonic_camera_timestamps_rejected(tmp_path):
    p = tmp_path / "metadata.csv"
    p.write_text("Timestamp\n0\n0.1\n0.1\n")
    with pytest.raises(ValueError, match="nonmonotonic"):
        observed_rate(p)


def test_behavior_owns_subject_details(recording):
    root, _, _ = recording
    path = root / "fib/Logs/session_input.json"
    data = json.loads(path.read_text())
    data["acquisition_type"] = "HSFP_VrForaging"
    path.write_text(json.dumps(data))
    assert build_acquisition(root).subject_details is None


def test_deterministic_rebuild(recording):
    root, _, _ = recording
    path = map_acquisition(root)
    before = path.read_bytes()
    map_acquisition(root)
    assert path.read_bytes() == before


def test_missing_camera_enum_is_not_guessed(recording):
    root, _, _ = recording
    path = root / "fib/segment_0001/camera_applied.json"
    data = json.loads(path.read_text())
    data["properties"][0]["value"] = 0
    path.write_text(json.dumps(data))
    with pytest.raises(ValueError, match="Unsupported camera trigger"):
        build_acquisition(root)


def test_instrument_change_after_first_start_rejected(recording):
    root, _, _ = recording
    path = root / "instrument_fib.json"
    data = json.loads(path.read_text())
    data["notes"] = "modified during acquisition"
    path.write_text(json.dumps(data))
    with pytest.raises(ValueError, match="Instrument snapshot changed"):
        build_acquisition(root)


def test_workflow_edges_and_exposed_root_contract():
    import xml.etree.ElementTree as E
    from pathlib import Path

    root = E.parse(Path(__file__).parents[1] / "src/main.bonsai").getroot()
    ns = {"b": "https://bonsai-rx.org/2018/workflow"}
    xsi = "{http://www.w3.org/2001/XMLSchema-instance}type"
    for workflow in root.findall(".//b:Workflow", ns):
        nodes, edges = workflow.find("b:Nodes", ns), workflow.find("b:Edges", ns)
        if nodes is not None and edges is not None:
            for edge in edges:
                assert 0 <= int(edge.attrib["From"]) < len(nodes)
                assert 0 <= int(edge.attrib["To"]) < len(nodes)
    exposed = {p.attrib["Name"] for p in root.findall("b:Workflow/b:Nodes/b:Expression/b:Property", ns)}
    assert {"AcquisitionRoot", "SegmentId", "TeensyPort", "CuttlefishPort", "LookupTablePath"} <= exposed
    assert any(c.get(xsi) == "p1:HsfpCamera" for c in root.findall(".//b:Combinator", ns))
    assert not any(
        e.attrib.get("Path", "").endswith("GenerateRootLoggingPath.bonsai") for e in root.findall(".//b:Expression", ns)
    )



def test_externalized_properties_exist_on_the_connected_nested_workflow():
    import xml.etree.ElementTree as E
    from pathlib import Path

    root = E.parse(Path(__file__).parents[1] / "src/main.bonsai").getroot()
    ns = {"b": "https://bonsai-rx.org/2018/workflow"}
    xsi = "{http://www.w3.org/2001/XMLSchema-instance}type"
    for workflow in root.findall(".//b:Workflow", ns):
        nodes, edges = workflow.find("b:Nodes", ns), workflow.find("b:Edges", ns)
        if nodes is None or edges is None:
            continue
        for edge in edges:
            source, target = nodes[int(edge.get("From"))], nodes[int(edge.get("To"))]
            if source.get(xsi) != "ExternalizedMapping" or target.get(xsi) != "GroupWorkflow":
                continue
            exposed = {
                prop.get("DisplayName", prop.get("Name"))
                for mapping in target.findall("b:Workflow/b:Nodes/b:Expression", ns)
                if mapping.get(xsi) == "ExternalizedMapping"
                for prop in mapping.findall("b:Property", ns)
            }
            requested = {prop.get("Name") for prop in source.findall("b:Property", ns)}
            missing = requested - exposed
            assert not missing, f"{target.findtext('b:Name', namespaces=ns) or 'unnamed group'} lacks {missing}"


def test_externalized_mappings_have_connected_targets():
    import xml.etree.ElementTree as E
    from pathlib import Path

    root = E.parse(Path(__file__).parents[1] / "src/main.bonsai").getroot()
    ns = {"b": "https://bonsai-rx.org/2018/workflow"}
    xsi = "{http://www.w3.org/2001/XMLSchema-instance}type"
    disconnected = []
    for workflow in root.findall(".//b:Workflow", ns):
        nodes = workflow.find("b:Nodes", ns)
        edges = workflow.find("b:Edges", ns)
        if nodes is None:
            continue
        connected_sources = {int(edge.get("From")) for edge in edges} if edges is not None else set()
        for index, node in enumerate(nodes):
            if node.get(xsi) == "ExternalizedMapping" and index not in connected_sources:
                properties = [prop.get("Name") for prop in node.findall("b:Property", ns)]
                disconnected.append(properties)
    assert not disconnected, f"Externalized mappings have no target: {disconnected}"
