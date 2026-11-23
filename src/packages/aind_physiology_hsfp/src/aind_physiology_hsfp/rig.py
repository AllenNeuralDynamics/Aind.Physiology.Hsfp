from typing import Literal, Self

from aind_behavior_services import rig
from aind_behavior_services.rig import harp
from pydantic import BaseModel, Field, model_validator

from . import __semver__


class PwmSettings(BaseModel):
    """Settings of one PWM output of the cuTTLefish board (mirrors the firmware PwmSettings register)."""

    offset_us: int = Field(
        default=0, ge=0, description="Time (us) the output stays LOW before going HIGH in each period."
    )
    on_duration_us: int = Field(ge=1, description="Time (us) the output stays HIGH in each period.")
    off_duration_us: int = Field(ge=0, description="Time (us) the output stays LOW in each period.")
    cycles: int = Field(default=0, ge=0, description="Number of pulses to produce. 0 pulses until disabled.")
    invert: bool = Field(
        default=False, description="Whether the output is inverted (HIGH refers to the output being LOW)."
    )

    @property
    def period_us(self) -> int:
        """Duration of one period, in microseconds."""
        return self.on_duration_us + self.off_duration_us


class CameraRegion(BaseModel):
    """Rectangular region of the camera sensor, defined by its top-left corner, width and height (px)."""

    x: int = Field(default=0, ge=0, description="X coordinate of the top-left corner (px).")
    y: int = Field(default=0, ge=0, description="Y coordinate of the top-left corner (px).")
    width: int = Field(default=0, ge=0, description="Width of the region (px).")
    height: int = Field(default=0, ge=0, description="Height of the region (px).")


class LaserChannel(BaseModel):
    """A laser triggered by one cuTTLefish PWM output. Each laser defines one acquired channel.

    The N-th laser of the rig (channel N) is triggered by cuTTLefish pin N + 1. Pin 0 triggers the camera.
    """

    laser_name: str = Field(description="Name of the laser, as it appears in the instrument metadata.")
    wavelength: int = Field(gt=0, description="Excitation wavelength (nm).")
    pwm: PwmSettings = Field(description="PWM settings of the laser trigger.")
    power: float | None = Field(
        default=None, ge=0, description="Power (uW) at the fiber tip. If not set, it is taken from the instrument."
    )


class HsfpCamera(rig.Device):
    """Hamamatsu camera that images the dispersed fiber output."""

    device_type: Literal["HsfpCamera"] = "HsfpCamera"
    device_name: str = Field(
        default="Hamamatsu Orca Flash 4 v3", description="Name of the camera, as it appears in the instrument metadata."
    )
    settings_path: str = Field(description="Path to the Hamamatsu camera settings (.xml) file.")
    lookup_table_path: str | None = Field(default=None, description="Optional path to a lookup table (.csv) file.")
    region: CameraRegion = Field(
        default=CameraRegion(x=63, y=881, width=1976, height=236),
        validate_default=True,
        description="Region of the sensor that is cropped and recorded (px).",
    )
    trigger: PwmSettings = Field(
        default=PwmSettings(offset_us=0, on_duration_us=100, off_duration_us=6567),
        validate_default=True,
        description="PWM settings of the camera trigger, on cuTTLefish pin 0.",
    )


def _default_lasers() -> list[LaserChannel]:
    lasers = [
        ("OBIS 405 LX", 405),
        ("OBIS 445 LX", 445),
        ("OBIS 488 LX", 488),
        ("OBIS 514 LX", 514),
        ("OBIS 561 LS", 561),
    ]
    return [
        LaserChannel(
            laser_name=name,
            wavelength=wavelength,
            pwm=PwmSettings(offset_us=6667 * i, on_duration_us=1000, off_duration_us=32335),
        )
        for i, (name, wavelength) in enumerate(lasers)
    ]


class AindPhysioHsfpRig(rig.Rig):
    """Complete rig configuration model for the AIND HSFP system."""

    version: Literal[__semver__] = __semver__
    camera: HsfpCamera = Field(title="Camera", description="Camera that records the dispersed fiber output.")
    cuttlefish: harp.HarpCuttlefish = Field(
        title="cuTTLefish", description="cuTTLefish board that triggers the camera and the lasers."
    )
    lasers: list[LaserChannel] = Field(
        default_factory=_default_lasers,
        min_length=1,
        max_length=7,
        validate_default=True,
        description="Lasers, in the order in which they fire. Laser N is triggered by cuTTLefish pin N + 1 and "
        "defines acquisition channel N.",
    )
    tiff_chunk_size: int = Field(default=1000, gt=0, description="Number of frames per TIFF file.")

    @model_validator(mode="after")
    def _validate_lasers(self) -> Self:
        expected = self.camera.trigger.period_us * len(self.lasers)
        for laser in self.lasers:
            if laser.pwm.period_us != expected:
                raise ValueError(
                    f"{laser.laser_name} period is {laser.pwm.period_us} us, but {len(self.lasers)} channels at a camera "
                    f"period of {self.camera.trigger.period_us} us require {expected} us."
                )
        return self
