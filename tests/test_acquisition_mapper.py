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
    assert all(c.power is None for c in stream.configurations if isinstance(c, LaserConfig))


def test_no_epoch_raises(tmp_path):
    with pytest.raises(ValueError, match="No HSFP epoch"):
        _map(tmp_path)
