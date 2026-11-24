from datetime import timedelta
from pathlib import Path

import pytest
from aind_data_schema.components.configs import LaserConfig, PatchCordConfig
from aind_physiology_hsfp_data_mappers import AindAcquisitionDataMapper

REPOSITORY = Path(__file__).parents[1]


def _map(session_root, instrument_path=None):
    return AindAcquisitionDataMapper(
        data_path=session_root, repository_path=REPOSITORY, instrument_path=instrument_path
    ).map()


def test_maps_one_stream_per_epoch(session_root, instrument_path, make_epoch, session_start):
    make_epoch(session_root, session_start + timedelta(minutes=5))
    acquisition = _map(session_root, instrument_path)
    assert acquisition.subject_id == "test"
    assert acquisition.instrument_id == "HSFP-1"
    assert len(acquisition.data_streams) == 2
    assert acquisition.acquisition_start_time == session_start
    assert acquisition.acquisition_end_time == session_start + timedelta(minutes=6)


def test_stream_configuration(session_root, instrument_path):
    stream = _map(session_root, instrument_path).data_streams[0]
    lasers = [c for c in stream.configurations if isinstance(c, LaserConfig)]
    cords = [c for c in stream.configurations if isinstance(c, PatchCordConfig)]
    assert [laser.wavelength for laser in lasers] == [405, 445, 488, 514, 561]
    # Laser powers are not set in the example rig, so they come from the instrument calibrations.
    assert lasers[0].power == 10.0
    assert [cord.device_name for cord in cords] == ["Mono Fiber-optic Patch Cord 0", "Mono Fiber-optic Patch Cord 1"]
    assert [ch.channel_name for ch in cords[0].channels] == [f"Fiber_0_{w}" for w in (405, 445, 488, 514, 561)]
    detector = cords[0].channels[0].detector
    assert detector.exposure_time == pytest.approx(4.0)
    assert detector.crop_width == 1972
    parameters = stream.code[0].parameters["hsfp"]
    assert parameters["imaging"]["sampling_strategy"]["frame_rate"] == pytest.approx(150)
    assert parameters["intended_measurements_by_fiber"]["Fiber_0"]["green"] == "dopamine"


def test_without_instrument_power_is_unset(session_root):
    stream = _map(session_root).data_streams[0]
    lasers = [c for c in stream.configurations if isinstance(c, LaserConfig)]
    assert all(laser.power is None for laser in lasers)
    # Without the instrument, a laser is named after the model and serial number it reports.
    assert lasers[0].device_name == "OBIS 405nm LX 100mW S/N FS230411028"


def test_laser_settings_are_recorded(session_root, instrument_path):
    channels = _map(session_root, instrument_path).data_streams[0].code[0].parameters["hsfp"]["imaging"]["channels"]
    assert channels[0]["light_source"] == "OBIS 405 LX"
    assert channels[0]["serial_number"] == "FS230411028"
    assert channels[0]["power_setpoint_w"] == pytest.approx(0.01)
    assert channels[0]["emission"] is True
    assert channels[0]["operating_mode"] == "Digital"


def test_a_laser_missing_from_the_instrument_raises(session_root, instrument_path, tmp_path):
    import json

    instrument = json.loads(instrument_path.read_text(encoding="utf-8-sig"))
    for component in instrument["components"]:
        if component.get("serial_number") == "FS230411028":
            component["serial_number"] = None
    path = tmp_path / "instrument.json"
    path.write_text(json.dumps(instrument), encoding="utf-8")
    with pytest.raises(ValueError, match="No component with serial number FS230411028"):
        _map(session_root, path)


def test_the_lasers_must_match_the_rig(session_root):
    events = next(session_root.glob("fib/hsfp_*")) / "SoftwareEvents" / "Lasers.jsonl"
    events.write_text(events.read_text(encoding="utf-8").replace("FS230411028", "OTHER"), encoding="utf-8")
    with pytest.raises(ValueError, match="reports S/N OTHER"):
        _map(session_root)


def test_no_epoch_raises(tmp_path):
    with pytest.raises(ValueError, match="No HSFP epoch"):
        _map(tmp_path)


def _rig_with_gaps():
    from aind_physiology_hsfp.rig import CuttlefishChannels, LightSource

    from examples import example

    rig = example.mock_rig()
    rig.channels = CuttlefishChannels(
        channel_1=None,
        channel_2=LightSource(serial_number="FS230321025"),
        channel_3=None,
        channel_4=None,
        channel_5=LightSource(serial_number="466789"),
    )
    return rig


def test_unused_channels_are_skipped(tmp_path, instrument_path, make_epoch, session_start):
    make_epoch(tmp_path, session_start, rig=_rig_with_gaps())
    stream = _map(tmp_path, instrument_path).data_streams[0]
    lasers = [c for c in stream.configurations if isinstance(c, LaserConfig)]
    # Each laser is named after the instrument component with its serial number; its wavelength is the one it reports.
    assert [(laser.device_name, laser.wavelength) for laser in lasers] == [("OBIS 445 LX", 445), ("OBIS 561 LS", 561)]
    ports = {c.target_device: c.source_port for c in stream.connections if c.source_port}
    assert ports["OBIS 445 LX"] == "IO2" and ports["OBIS 561 LS"] == "IO5"
    channels = stream.code[0].parameters["hsfp"]["imaging"]["channels"]
    assert [c["channel"] for c in channels] == [2, 5]
