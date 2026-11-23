from typing import Self

from pydantic import BaseModel, Field, model_validator

from .rig import CameraRegion


class FiberRoi(BaseModel):
    """Regions of interest of one fiber: one rectangle of the camera image per acquisition channel."""

    fiber_number: int = Field(ge=0, description="Number of the implanted fiber (Fiber_N in the procedures).")
    regions: list[CameraRegion] = Field(
        min_length=1, description="Region of the camera image of this fiber, per acquisition channel (index = channel)."
    )
    enabled: bool = Field(default=True, description="Whether the fiber is recorded.")


class RoiSettings(BaseModel):
    """Regions of interest used to integrate the fluorescence of each fiber, in each channel."""

    rois: list[FiberRoi] = Field(default_factory=list, description="Regions of interest, one entry per fiber.")

    @model_validator(mode="after")
    def _validate_rois(self) -> Self:
        fibers = [roi.fiber_number for roi in self.rois]
        if len(set(fibers)) != len(fibers):
            raise ValueError(f"Duplicated fiber_number found in ROIs: {fibers}")
        if len({len(roi.regions) for roi in self.rois}) > 1:
            raise ValueError("All ROIs must define the same number of channel regions.")
        return self
