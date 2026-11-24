import pytest
from aind_behavior_services.rig.harp import HarpCuttlefish
from aind_physiology_hsfp.rig import (
    LIGHT_SOURCE_CHANNELS,
    AindPhysioHsfpRig,
    CuttlefishChannels,
    HsfpCamera,
    LightSource,
    PwmSchedule,
    TriggerTiming,
)
from aind_physiology_hsfp.task_logic import AindPhysioHsfpTaskParameters, Measurement
from pydantic import ValidationError


def _rig(**kwargs) -> AindPhysioHsfpRig:
    kwargs.setdefault("channels", _channels([1, 2, 3, 4, 5]))
    return AindPhysioHsfpRig(
        data_directory="C:/data",
        rig_name="HSFP-1",
        computer_name="test",
        camera=HsfpCamera(settings_path="camera.xml"),
        cuttlefish=HarpCuttlefish(port_name="COM1"),
        **kwargs,
    )


def test_five_light_sources_match_the_previous_hard_coded_schedule():
    rig = _rig()
    assert list(rig.light_sources()) == [1, 2, 3, 4, 5]
    assert rig.camera_schedule() == PwmSchedule(offset_us=0, on_duration_us=100, off_duration_us=6567)
    assert rig.light_source_schedules() == {
        i + 1: PwmSchedule(offset_us=6667 * i, on_duration_us=1000, off_duration_us=32335) for i in range(5)
    }


def _channels(used: list[int]) -> CuttlefishChannels:
    return CuttlefishChannels(
        **{f"channel_{c}": LightSource(serial_number=f"SN{c}") if c in used else None for c in LIGHT_SOURCE_CHANNELS}
    )


@pytest.mark.parametrize("used", [[1], [7], [2, 5], [1, 3, 4, 7], list(LIGHT_SOURCE_CHANNELS)])
@pytest.mark.parametrize("frame_period_us", [6667, 10000, 3331])
@pytest.mark.parametrize("delay", [0, 17])
def test_schedules_are_phase_locked(used, frame_period_us, delay):
    timing = TriggerTiming(
        frame_period_us=frame_period_us, light_source_on_duration_us=1000, light_source_delay_us=delay
    )
    rig = _rig(channels=_channels(used), timing=timing)
    camera = rig.camera_schedule()
    schedules = rig.light_source_schedules()
    assert list(schedules) == used
    assert camera.period_us == frame_period_us
    for index, schedule in enumerate(schedules.values()):
        # Exactly one camera period per light source, no remainder, so no light source drifts against the camera.
        assert schedule.period_us == len(used) * camera.period_us
        # The k-th used channel fires `delay` after the camera trigger of frame k and ends within that frame.
        assert schedule.offset_us == index * camera.period_us + delay
        assert schedule.offset_us + schedule.on_duration_us <= (index + 1) * camera.period_us


def test_light_source_pulse_must_fit_its_frame():
    with pytest.raises(ValidationError, match="must end within its frame"):
        TriggerTiming(frame_period_us=5000, light_source_on_duration_us=4000, light_source_delay_us=1500)


def test_camera_trigger_must_be_shorter_than_the_frame():
    with pytest.raises(ValidationError, match="camera trigger"):
        TriggerTiming(frame_period_us=100, camera_trigger_duration_us=100, light_source_on_duration_us=10)


def test_at_least_one_light_source():
    with pytest.raises(ValidationError, match="At least one"):
        _channels([])


def test_the_rig_needs_its_channels():
    with pytest.raises(ValidationError, match="channels"):
        AindPhysioHsfpRig(
            data_directory="C:/data",
            rig_name="HSFP-1",
            computer_name="test",
            camera=HsfpCamera(settings_path="camera.xml"),
            cuttlefish=HarpCuttlefish(port_name="COM1"),
        )


def test_a_light_source_is_identified_by_its_serial_number_only():
    with pytest.raises(ValidationError, match="serial_number"):
        LightSource()
    with pytest.raises(ValidationError, match="serial_number"):
        LightSource(serial_number="")
    assert LightSource(serial_number="123456").power is None
    assert set(LightSource.model_fields) == {"serial_number", "power"}


def test_laser_serial_numbers_must_be_unique():
    with pytest.raises(ValidationError, match="serial numbers must be unique: 123456"):
        CuttlefishChannels(
            channel_1=LightSource(serial_number="123456"),
            channel_2=LightSource(serial_number="123456"),
        )


def test_measurements_must_use_distinct_fibers_and_patch_cords():
    with pytest.raises(ValidationError, match="patch_cord"):
        AindPhysioHsfpTaskParameters(
            measurements=[Measurement(fiber_number=0, patch_cord=0), Measurement(fiber_number=1, patch_cord=0)]
        )
