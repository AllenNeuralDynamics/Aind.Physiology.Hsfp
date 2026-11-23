from aind_physiology_hsfp.data_contract import dataset
from aind_physiology_hsfp.rig import AindPhysioHsfpRig


def test_dataset_reads_epoch(session_root):
    epoch = next((session_root / "fib").glob("hsfp_*"))
    contract = dataset(epoch)
    assert isinstance(contract["rig_input"].read(), AindPhysioHsfpRig)
    assert contract["task_input"].read().task_parameters.measurements[0].fiber_number == 0
    metadata = contract["frame_metadata"].read()
    assert len(metadata) == 50
    assert sorted(metadata["Channel"].unique()) == [1, 2, 3, 4, 5]
    start = contract["SoftwareEvents"]["StartSessionTime"].read()
    assert start["name"].iloc[0] == "StartSessionTime"


def test_software_events_are_jsonl(tmp_path):
    contract = dataset(tmp_path)
    for stream in contract["SoftwareEvents"]:
        assert str(stream.reader_params.path).endswith(".jsonl")


def test_tiff_stack_reads_frames_across_chunks(tmp_path):
    import numpy as np
    import tifffile

    (tmp_path / "channel_1").mkdir()
    for chunk in range(2):
        with tifffile.TiffWriter(tmp_path / "channel_1" / f"frame_{chunk:04d}.tiff", bigtiff=True) as writer:
            for i in range(3):  # one page per frame, as written by the Bonsai TiffWriter
                writer.write(np.full((4, 6), 10 * chunk + i, np.uint16), contiguous=False)
    reader = dataset(tmp_path)["channel_1"].read()
    assert reader.number_of_frames == 6
    assert [int(frame[0, 0]) for frame in reader.get_frames([0, 2, 3, 5])] == [0, 2, 10, 12]
