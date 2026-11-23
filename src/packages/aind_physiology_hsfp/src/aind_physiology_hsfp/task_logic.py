from typing import Literal, Self

from aind_behavior_services.task import Task, TaskParameters
from pydantic import BaseModel, Field, model_validator

from . import __semver__


class IntendedMeasurements(BaseModel):
    """Biological signal measured in each color band of one fiber. A null value means the band is not used."""

    blue: str | None = Field(default=None, description="Intended measurement in the blue band.")
    green: str | None = Field(default=None, description="Intended measurement in the green band.")
    yellow: str | None = Field(default=None, description="Intended measurement in the yellow band.")
    orange: str | None = Field(default=None, description="Intended measurement in the orange band.")
    red: str | None = Field(default=None, description="Intended measurement in the red band.")
    iso: str | None = Field(default=None, description="Intended measurement in the isosbestic band.")


class Measurement(BaseModel):
    """A recorded fiber: the implanted fiber and the patch cord connected to it."""

    fiber_number: int = Field(ge=0, description="Number of the implanted fiber (Fiber_N in the procedures).")
    patch_cord: int = Field(ge=0, description="Index of the patch cord connected to the fiber.")
    intended_measurements: IntendedMeasurements = Field(
        default_factory=IntendedMeasurements,
        validate_default=True,
        description="Intended measurement in each color band.",
    )
    enabled: bool = Field(default=True, description="Whether the fiber is recorded.")


class AindPhysioHsfpTaskParameters(TaskParameters):
    """Parameters of the HSFP task."""

    measurements: list[Measurement] = Field(default_factory=list, description="Fibers recorded in the experiment.")

    @model_validator(mode="after")
    def _validate_unique_fibers(self) -> Self:
        for attr in ("fiber_number", "patch_cord"):
            values = [getattr(m, attr) for m in self.measurements]
            duplicates = sorted({v for v in values if values.count(v) > 1})
            if duplicates:
                raise ValueError(f"Duplicated {attr} found in measurements: {duplicates}")
        return self


class AindPhysioHsfpTaskLogic(Task):
    """Task logic for the AIND HSFP system."""

    name: Literal["AindPhysioHsfp"] = "AindPhysioHsfp"
    version: Literal[__semver__] = __semver__
    task_parameters: AindPhysioHsfpTaskParameters = Field(
        default_factory=AindPhysioHsfpTaskParameters,
        validate_default=True,
        description="Parameters of the task",
    )
