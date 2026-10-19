"""Typed clabe rig configuration: runtime locations, not duplicate hardware metadata."""

from pathlib import Path
from typing import Literal
from zoneinfo import ZoneInfo

from aind_behavior_services.rig import Rig
from pydantic import Field, field_validator


class HsfpRigConfig(Rig):
    version: Literal["1.0.0"] = "1.0.0"
    site_timezone: str = "America/Los_Angeles"
    acquisition_repository: Path
    instrument_directory: Path
    camera_settings: Path
    trigger_firmware_source: Path
    bonsai_executable: Path
    teensy_port: str
    cuttlefish_port: str
    minimum_free_bytes: int = Field(default=50_000_000_000, gt=0)
    chunk_size: int = Field(default=1000, gt=0)
    lookup_table: Path | None = None

    @field_validator("site_timezone")
    @classmethod
    def named_timezone(cls, value: str) -> str:
        ZoneInfo(value)
        return value
