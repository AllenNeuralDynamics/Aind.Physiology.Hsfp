import typing as t
from pathlib import Path

import pydantic
from aind_behavior_services.schema import BonsaiSgenSerializers, convert_pydantic_to_bonsai
from aind_behavior_services.session import Session

from aind_physiology_hsfp import rig
from aind_physiology_hsfp.extra_types import FiberRoi, RoiSettings
from aind_physiology_hsfp.task_logic import AindPhysioHsfpTaskLogic

SCHEMA_ROOT = Path("./schema/")
EXTENSIONS_ROOT = Path("./src/Extensions/")
NAMESPACE_PREFIX = "AindPhysiologyHsfp"


def main():
    models = [rig.AindPhysioHsfpRig, FiberRoi, RoiSettings, AindPhysioHsfpTaskLogic, Session]

    model = pydantic.RootModel[t.Union[tuple(models)]]  # noqa: UP007
    convert_pydantic_to_bonsai(
        model,
        model_name="aind_physiology_hsfp",
        root_element="Root",
        cs_namespace=NAMESPACE_PREFIX,
        json_schema_output_dir=SCHEMA_ROOT,
        cs_output_dir=EXTENSIONS_ROOT,
        cs_serializer=[BonsaiSgenSerializers.JSON],
    )


if __name__ == "__main__":
    main()
