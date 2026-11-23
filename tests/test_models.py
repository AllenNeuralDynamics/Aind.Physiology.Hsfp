import pytest
from aind_behavior_services.rig.harp import HarpCuttlefish
from aind_physiology_hsfp.rig import AindPhysioHsfpRig, HsfpCamera, LaserChannel, PwmSettings
from aind_physiology_hsfp.task_logic import AindPhysioHsfpTaskParameters, Measurement
from pydantic import ValidationError


def _rig(**kwargs) -> AindPhysioHsfpRig:
    return AindPhysioHsfpRig(
        data_directory="C:/data",
        rig_name="HSFP-1",
        computer_name="test",
        camera=HsfpCamera(settings_path="camera.xml"),
        cuttlefish=HarpCuttlefish(port_name="COM1"),
        **kwargs,
    )


def test_default_rig_is_five_interleaved_channels():
    rig = _rig()
    assert [laser.wavelength for laser in rig.lasers] == [405, 445, 488, 514, 561]
    assert {laser.pwm.period_us for laser in rig.lasers} == {5 * rig.camera.trigger.period_us}


def test_laser_period_must_match_channel_count():
    lasers = [
        LaserChannel(
            laser_name=f"L{i}",
            wavelength=400 + i,
            pwm=PwmSettings(offset_us=6667 * i, on_duration_us=1000, off_duration_us=32335),
        )
        for i in range(3)
    ]
    with pytest.raises(ValidationError, match="require 20001 us"):
        _rig(lasers=lasers)


def test_measurements_must_use_distinct_fibers_and_patch_cords():
    with pytest.raises(ValidationError, match="patch_cord"):
        AindPhysioHsfpTaskParameters(
            measurements=[Measurement(fiber_number=0, patch_cord=0), Measurement(fiber_number=1, patch_cord=0)]
        )
