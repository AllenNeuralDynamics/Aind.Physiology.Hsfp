import logging
import os
from datetime import datetime
from pathlib import Path

import pydantic
import pydantic_settings

from ._acquisition import AindAcquisitionDataMapper as AindAcquisitionDataMapper

logger = logging.getLogger(__name__)

ACQUISITION_FILE_NAME = "acquisition_fib.json"


class DataMapperCli(pydantic_settings.BaseSettings, cli_kebab_case=True):
    """CLI for generating AIND metadata from raw HSFP data."""

    data_path: os.PathLike = pydantic.Field(description="Path to the session data directory.")
    repository_path: Path = pydantic.Field(
        default=Path("."),
        description="Root of the Aind.Physiology.Hsfp checkout the session was acquired with. Defaults to the "
        "current directory.",
    )
    instrument_path: Path | None = pydantic.Field(
        default=None,
        description="Optional instrument metadata (instrument_fib.json), used to check device names and to read "
        "laser powers not set in the rig.",
    )
    session_end_time: datetime | None = pydantic.Field(
        default=None,
        description="Session end time in ISO format. Defaults to the end of the last HSFP epoch.",
    )
    acquisition_type: str = pydantic.Field(default="", description="Acquisition type.")

    def cli_cmd(self):
        acquisition_mapped = AindAcquisitionDataMapper(
            data_path=Path(self.data_path),
            repository_path=self.repository_path,
            instrument_path=self.instrument_path,
            session_end_time=self.session_end_time,
            acquisition_type=self.acquisition_type,
        ).map()
        logger.info("Writing %s to %s", ACQUISITION_FILE_NAME, self.data_path)
        with open(Path(self.data_path) / ACQUISITION_FILE_NAME, "w", encoding="utf-8") as f:
            f.write(acquisition_mapped.model_dump_json(indent=2))


def main() -> None:
    pydantic_settings.CliApp().run(DataMapperCli)


if __name__ == "__main__":
    main()
