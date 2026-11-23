from typing import Literal, Self

from aind_behavior_services import rig
from aind_behavior_services.rig import harp
from pydantic import BaseModel, Field, model_validator

from . import __semver__

CAMERA_CHANNEL = 0
"""cuTTLefish channel reserved for the camera trigger."""

LIGHT_SOURCE_CHANNELS = tuple(range(1, 8))
"""cuTTLefish channels that can trigger a light source."""


class PwmSchedule(BaseModel):
    """Settings of one cuTTLefish PWM output, in microseconds (mirrors the firmware PwmSettings register)."""

    offset_us: int = Field(ge=0, description="Time (us) the output stays LOW before going HIGH in each period.")
    on_duration_us: int = Field(ge=1, description="Time (us) the output stays HIGH in each period.")
    off_duration_us: int = Field(ge=0, description="Time (us) the output stays LOW in each period.")

    @property
    def period_us(self) -> int:
        """Duration of one period, in microseconds."""
        return self.on_duration_us + self.off_duration_us


class TriggerTiming(BaseModel):
    """Timing shared by the camera and every light source. All values are integer microseconds, so the PWM outputs
    of the cuTTLefish board are derived exactly, without rounding.

    The camera is triggered once per frame on channel 0. The light sources fire in turn, one per frame, in channel
    order: with L light sources, the k-th one fires in frames k, k + L, k + 2L... so its period is exactly L frame
    periods.
    """

    frame_period_us: int = Field(
        default=6667,
        ge=10,
        le=1_000_000,
        description="Time (us) between consecutive camera triggers. The frame rate is 1e6 / frame_period_us, and each "
        "light source is sampled at 1e6 / (frame_period_us * number of light sources).",
    )
    camera_trigger_duration_us: int = Field(
        default=100, ge=1, description="Time (us) the camera trigger stays HIGH in each frame."
    )
    light_source_on_duration_us: int = Field(
        default=1000, ge=1, description="Time (us) each light source stays on in its frame."
    )
    light_source_delay_us: int = Field(
        default=0,
        ge=0,
        description="Time (us) from the rising edge of the camera trigger to the light source turning on, in the "
        "light source's frame.",
    )

    @model_validator(mode="after")
    def _validate_fits_frame(self) -> Self:
        if self.camera_trigger_duration_us >= self.frame_period_us:
            raise ValueError(
                f"The camera trigger ({self.camera_trigger_duration_us} us) must be shorter than the frame period "
                f"({self.frame_period_us} us)."
            )
        if self.light_source_delay_us + self.light_source_on_duration_us > self.frame_period_us:
            raise ValueError(
                f"The light source pulse (delay {self.light_source_delay_us} us + on "
                f"{self.light_source_on_duration_us} us) must end within its frame ({self.frame_period_us} us), or it "
                "would overlap the next light source."
            )
        return self

    def camera_schedule(self) -> PwmSchedule:
        """PWM output of the camera trigger (channel 0)."""
        return PwmSchedule(
            offset_us=0,
            on_duration_us=self.camera_trigger_duration_us,
            off_duration_us=self.frame_period_us - self.camera_trigger_duration_us,
        )

    def light_source_schedule(self, index: int, count: int) -> PwmSchedule:
        """PWM output of the ``index``-th of ``count`` light sources, in firing order."""
        if not 0 <= index < count:
            raise IndexError(f"Light source index {index} is out of range for {count} light sources.")
        return PwmSchedule(
            offset_us=index * self.frame_period_us + self.light_source_delay_us,
            on_duration_us=self.light_source_on_duration_us,
            off_duration_us=count * self.frame_period_us - self.light_source_on_duration_us,
        )


class CameraRegion(BaseModel):
    """Rectangular region of the camera sensor, defined by its top-left corner, width and height (px)."""

    x: int = Field(default=0, ge=0, description="X coordinate of the top-left corner (px).")
    y: int = Field(default=0, ge=0, description="Y coordinate of the top-left corner (px).")
    width: int = Field(default=0, ge=0, description="Width of the region (px).")
    height: int = Field(default=0, ge=0, description="Height of the region (px).")


class LightSource(BaseModel):
    """A light source triggered by one cuTTLefish channel. Each light source defines one acquisition channel."""

    name: str = Field(description="Name of the light source, as it appears in the instrument metadata.")
    wavelength: int | None = Field(default=None, gt=0, description="Excitation wavelength (nm), if known.")
    power: float | None = Field(
        default=None, ge=0, description="Power (uW) at the fiber tip. If not set, it is taken from the instrument."
    )


class CuttlefishChannels(BaseModel):
    """Light source triggered by each cuTTLefish channel, or None if the channel is unused.

    Channel 0 is reserved for the camera trigger. Light sources fire in channel order, skipping unused channels.
    """

    channel_1: LightSource | None = Field(
        default=LightSource(name="OBIS 405 LX", wavelength=405), description="Light source on channel 1."
    )
    channel_2: LightSource | None = Field(
        default=LightSource(name="OBIS 445 LX", wavelength=445), description="Light source on channel 2."
    )
    channel_3: LightSource | None = Field(
        default=LightSource(name="OBIS 488 LX", wavelength=488), description="Light source on channel 3."
    )
    channel_4: LightSource | None = Field(
        default=LightSource(name="OBIS 514 LX", wavelength=514), description="Light source on channel 4."
    )
    channel_5: LightSource | None = Field(
        default=LightSource(name="OBIS 561 LS", wavelength=561), description="Light source on channel 5."
    )
    channel_6: LightSource | None = Field(default=None, description="Light source on channel 6.")
    channel_7: LightSource | None = Field(default=None, description="Light source on channel 7.")

    def light_sources(self) -> dict[int, LightSource]:
        """Light source of each used channel, keyed by channel, in firing order."""
        sources = {channel: getattr(self, f"channel_{channel}") for channel in LIGHT_SOURCE_CHANNELS}
        return {channel: source for channel, source in sources.items() if source is not None}

    @model_validator(mode="after")
    def _validate_light_sources(self) -> Self:
        sources = self.light_sources()
        if not sources:
            raise ValueError("At least one cuTTLefish channel must have a light source.")
        names = [source.name for source in sources.values()]
        duplicates = sorted({name for name in names if names.count(name) > 1})
        if duplicates:
            raise ValueError(f"Light source names must be unique: {', '.join(duplicates)}.")
        return self


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


class AindPhysioHsfpRig(rig.Rig):
    """Complete rig configuration model for the AIND HSFP system."""

    version: Literal[__semver__] = __semver__
    camera: HsfpCamera = Field(title="Camera", description="Camera that records the dispersed fiber output.")
    cuttlefish: harp.HarpCuttlefish = Field(
        title="cuTTLefish", description="cuTTLefish board that triggers the camera and the light sources."
    )
    channels: CuttlefishChannels = Field(
        default=CuttlefishChannels(),
        validate_default=True,
        title="cuTTLefish channels",
        description="Light source on each cuTTLefish channel. Channel 0 triggers the camera. Each light source "
        "defines the acquisition channel with the same number.",
    )
    timing: TriggerTiming = Field(
        default=TriggerTiming(),
        validate_default=True,
        title="Trigger timing",
        description="Timing of the camera and light source triggers, shared by all light sources.",
    )
    tiff_chunk_size: int = Field(default=1000, gt=0, description="Number of frames per TIFF file.")

    def light_sources(self) -> dict[int, LightSource]:
        """Light source of each used cuTTLefish channel, keyed by channel, in firing order."""
        return self.channels.light_sources()

    def camera_schedule(self) -> PwmSchedule:
        """PWM output of the camera trigger (cuTTLefish channel 0)."""
        return self.timing.camera_schedule()

    def light_source_schedules(self) -> dict[int, PwmSchedule]:
        """PWM output of each used cuTTLefish channel, keyed by channel."""
        channels = list(self.light_sources())
        return {
            channel: self.timing.light_source_schedule(index, len(channels)) for index, channel in enumerate(channels)
        }
