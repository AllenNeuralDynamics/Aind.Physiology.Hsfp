"""Persisted operator inputs. Biological colors are independent of excitation lasers."""

from datetime import datetime
from typing import Self
from zoneinfo import ZoneInfo

from pydantic import AliasChoices, BaseModel, ConfigDict, Field, field_validator, model_validator

ACQUISITION_TYPES = ("HSFP", "HSFP_Pavlovian", "HSFP_DynamicForaging", "HSFP_VrForaging")
PLATFORM_NAMES = ("mouse_tube_foraging",)
INDICATOR_COLORS = ("blue", "green", "yellow", "orange", "red", "iso")
DEFAULT_LASERS = {
    "IO0": "OBIS 405 LX",
    "IO1": "OBIS 445 LX",
    "IO2": "OBIS 488 LX",
    "IO3": "OBIS 514 LX",
    "IO4": "OBIS 561 LS",
}


class StrictModel(BaseModel):
    model_config = ConfigDict(extra="forbid", validate_assignment=True)


class SessionInput(StrictModel):
    """One acquisition's confirmed inputs, preserved across recording restarts."""

    contract_version: str = "1.1.0"
    subject_id: str
    experimenters: list[str] = Field(min_length=1)
    acquisition_type: str = "HSFP"
    site_timezone: str = "America/Los_Angeles"
    ethics_review_id: list[str] = Field(min_length=1)
    ethics_source: str
    platform_name: str = "mouse_tube_foraging"
    notes_before: str | None = None
    notes_after: str | None = None
    # Values are biological measurements, literal "None" = empty, null = not provided.
    intended_measurements_by_fiber: dict[str, dict[str, str | None]] = Field(
        validation_alias=AliasChoices("intended_measurements_by_fiber", "indicators_by_fiber")
    )
    indicator_provenance: dict[str, dict[str, str]] = Field(default_factory=dict)
    # Physical instrument cord -> exact implanted device name in Procedures.
    connections: dict[str, str]
    # Fiber_N -> exact implanted device name, resolved from Procedures.
    fiber_names: dict[str, str]
    buffer_lasers: dict[str, str] = Field(default_factory=lambda: dict(DEFAULT_LASERS))
    configuration_confirmed: bool = False

    @field_validator("subject_id")
    @classmethod
    def subject_token(cls, value: str) -> str:
        if not value or any(c in value for c in '_<>:"/\\|?*') or value.endswith((" ", ".")):
            raise ValueError(
                "Subject must be a nonempty AIND filename token without underscores or reserved characters"
            )
        return value

    @field_validator("site_timezone")
    @classmethod
    def timezone_exists(cls, value: str) -> str:
        ZoneInfo(value)
        return value

    @field_validator("experimenters", "ethics_review_id")
    @classmethod
    def nonblank_values(cls, value: list[str]) -> list[str]:
        if any(not v.strip() for v in value):
            raise ValueError("Entries must not be blank")
        return value

    @model_validator(mode="after")
    def coherent(self) -> Self:
        if self.acquisition_type not in ACQUISITION_TYPES or self.platform_name not in PLATFORM_NAMES:
            raise ValueError("Unknown acquisition type or platform")
        if set(self.intended_measurements_by_fiber) != set(self.fiber_names):
            raise ValueError("Biological map and implanted-fiber map must have identical Fiber_N keys")
        for fiber, values in self.intended_measurements_by_fiber.items():
            if not fiber.startswith("Fiber_") or not fiber[6:].isdigit():
                raise ValueError("Fiber keys must be Fiber_N")
            if set(values) != set(INDICATOR_COLORS) or any(v == "" for v in values.values()):
                raise ValueError("Each fiber needs all six colors; use null for missing and string None for empty")
        if len(set(self.fiber_names.values())) != len(self.fiber_names):
            raise ValueError("Implant identities must be unique")
        if not self.connections or len(set(self.connections.values())) != len(self.connections):
            raise ValueError("Used cords must connect to distinct implanted fibers")
        if not set(self.connections.values()) <= set(self.fiber_names.values()):
            raise ValueError("Every connection must resolve an implanted fiber")
        # Current supported firmware c cycles exactly these five physical outputs.
        if set(self.buffer_lasers) != {f"IO{i}" for i in range(5)}:
            raise ValueError("Current mode c requires IO0 through IO4")
        if len(set(self.buffer_lasers.values())) != 5:
            raise ValueError("Each current buffer output must identify a distinct laser")
        return self


class JournalEvent(StrictModel):
    segment_id: str
    event: str
    timestamp: datetime
    source: str = "bonsai"
    details: dict = Field(default_factory=dict)

    @model_validator(mode="after")
    def valid(self) -> Self:
        if self.timestamp.tzinfo is None or self.timestamp.utcoffset() is None:
            raise ValueError("Journal timestamps must be timezone-aware")
        if self.event not in {"recording_start", "recording_stop", "interruption_detected", "end_inferred"}:
            raise ValueError("Unknown journal event")
        if not self.segment_id.startswith("segment_") or not self.segment_id[8:].isdigit():
            raise ValueError("Invalid segment ID")
        return self
