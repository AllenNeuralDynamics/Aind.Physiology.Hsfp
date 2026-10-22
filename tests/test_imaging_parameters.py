"""Observed interleaved camera timing and exact source-to-image identity."""

import json

import pytest
from aind_data_schema.core.acquisition import Acquisition
from aind_physiology_hsfp.data_mappers import map_acquisition


@pytest.mark.parametrize("dropped_frame", [False, True])
def test_interleaved_camera_rate_counts_exposures_and_preserves_per_channel_rates(recording, dropped_frame):
    root, _, _ = recording
    for channel in range(5):
        # Fifteen total exposures span 14/150 seconds. Optionally remove one
        # middle exposure from channel 2 without changing the recording bounds.
        counters = [channel, channel + 5, channel + 10]
        if dropped_frame and channel == 2:
            counters.remove(7)
        csv_path = root / f"fib/segment_0001/fib/metadata_ch{channel}.csv"
        csv_path.write_text("FrameCounter,Timestamp\n" + "".join(f"{c},{c / 150:.12f}\n" for c in counters))
    source_paths = [root / "instrument_fib.json", *root.glob("fib/segment_0001/fib/metadata_ch*.csv")]
    original = {path: path.read_bytes() for path in source_paths}

    payload = json.loads(map_acquisition(root).read_text())
    restored = Acquisition.model_validate(payload)
    params = restored.data_streams[0].code[0].parameters.model_dump()["hsfp"]
    assert "channel_order" not in params
    assert "observed_per_channel_rate_hz" not in params
    imaging = params["imaging"]
    strategy = imaging["sampling_strategy"]
    assert strategy["object_type"] == "Interleaved strategy"
    assert strategy["frame_rate_unit"] == "hertz"
    assert strategy["image_index_sequence"] == [0, 1, 2, 3, 4]
    assert strategy["frame_rate"] == pytest.approx(975 / 7 if dropped_frame else 150)
    assert len(imaging["images"]) == 5  # Both fibers share each exposure.
    for index, wavelength in enumerate([405, 445, 488, 514, 561]):
        image = imaging["images"][index]
        assert image == {
            "channel_name": f"frame_ch{index}",
            "buffer_port": f"IO{index}",
            "wavelength_nm": wavelength,
            "metadata_csv": f"fib/metadata_ch{index}.csv",
            "observed_frame_rate_hz": pytest.approx(15 if dropped_frame and index == 2 else 30),
        }
    assert all(path.read_bytes() == content for path, content in original.items())


def test_imaging_wavelengths_follow_confirmed_laser_connections(recording):
    root, _, _ = recording
    path = root / "fib/segment_0001/segment_input.json"
    source = json.loads(path.read_text())
    mapping = source["session"]["buffer_lasers"]
    mapping["IO0"], mapping["IO4"] = mapping["IO4"], mapping["IO0"]
    path.write_text(json.dumps(source))
    original = path.read_bytes()
    output = json.loads(map_acquisition(root).read_text())
    images = output["data_streams"][0]["code"][0]["parameters"]["hsfp"]["imaging"]["images"]
    assert [image["wavelength_nm"] for image in images] == [561, 445, 488, 514, 405]
    assert [image["buffer_port"] for image in images] == ["IO0", "IO1", "IO2", "IO3", "IO4"]
    assert path.read_bytes() == original
