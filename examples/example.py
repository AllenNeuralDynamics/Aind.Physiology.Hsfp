import argparse
import datetime
import os

from aind_behavior_services.rig.harp import HarpCuttlefish
from aind_behavior_services.session import Session
from aind_physiology_hsfp.rig import AindPhysioHsfpRig, CuttlefishChannels, HsfpCamera, LightSource
from aind_physiology_hsfp.task_logic import (
    AindPhysioHsfpTaskLogic,
    AindPhysioHsfpTaskParameters,
    IntendedMeasurements,
    Measurement,
)


def mock_session() -> Session:
    """Generates a mock Session model"""
    return Session(
        date=datetime.datetime(year=2025, month=1, day=1, hour=12, minute=0, second=0, tzinfo=datetime.UTC),
        experiment="AindPhysioHsfp",
        subject="test",
        notes="test session",
        allow_dirty_repo=True,
        skip_hardware_validation=False,
        experimenter=["Foo", "Bar"],
    )


def mock_channels() -> CuttlefishChannels:
    """Light source on each cuTTLefish channel (channel 0 is the camera trigger).

    Set serial_number to the serial number of each OBIS laser to control it from the Lasers tab; the workflow finds
    each laser at start and checks that it reports the light source's wavelength.
    """
    return CuttlefishChannels(
        channel_1=LightSource(name="OBIS 405 LX", wavelength=405, serial_number=None),
        channel_2=LightSource(name="OBIS 445 LX", wavelength=445, serial_number=None),
        channel_3=LightSource(name="OBIS 488 LX", wavelength=488, serial_number=None),
        channel_4=LightSource(name="OBIS 514 LX", wavelength=514, serial_number=None),
        channel_5=LightSource(name="OBIS 561 LS", wavelength=561, serial_number=None),
        channel_6=None,
        channel_7=None,
    )


def mock_rig() -> AindPhysioHsfpRig:
    """Generates a mock rig model, with five lasers"""
    return AindPhysioHsfpRig(
        data_directory=r"C:/data",
        rig_name="HSFP-1",
        computer_name="test_computer",
        camera=HsfpCamera(settings_path="../examples/HSFP-default-camera-config.xml"),
        cuttlefish=HarpCuttlefish(port_name="COM1"),
        channels=mock_channels(),
    )


def mock_task_logic() -> AindPhysioHsfpTaskLogic:
    """Generates a mock task logic model, with two fibers"""
    return AindPhysioHsfpTaskLogic(
        task_parameters=AindPhysioHsfpTaskParameters(
            measurements=[
                Measurement(
                    fiber_number=i,
                    patch_cord=i,
                    intended_measurements=IntendedMeasurements(green="dopamine", red="glutamate"),
                )
                for i in range(2)
            ]
        )
    )


def main():
    parser = argparse.ArgumentParser(description="Generate mock session, rig and task logic JSON files")
    parser.add_argument(
        "--path-seed",
        default="./local/{schema}.json",
        help="Path template for output files (default: ./local/{schema}.json)",
    )
    args = parser.parse_args()

    os.makedirs(os.path.dirname(args.path_seed), exist_ok=True)

    for model in [mock_session(), mock_rig(), mock_task_logic()]:
        with open(args.path_seed.format(schema=model.__class__.__name__), "w", encoding="utf-8") as f:
            f.write(model.model_dump_json(indent=2))


if __name__ == "__main__":
    main()
