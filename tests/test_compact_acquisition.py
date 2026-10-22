import json

from aind_data_schema.core.acquisition import Acquisition
from aind_physiology_hsfp.data_mappers import map_acquisition


def test_compact_output_keeps_agreed_values_and_source_evidence(recording):
    root, _, _ = recording
    source_names = [
        'instrument_fib.json', 'fib/Logs/session_input.json', 'fib/Logs/procedures_source.json',
        'fib/segment_0001/segment_input.json', 'fib/segment_0001/camera_applied.json',
    ]
    camera_path = root / source_names[-1]
    camera = json.loads(camera_path.read_text())
    camera['properties'].extend(
        {'id': 9990000 + i, 'name': 'extra DCAM property', 'value': i, 'value_text': 'readback', 'unit_code': 0}
        for i in range(80)
    )
    camera_path.write_text(json.dumps(camera))
    session_path = root / 'fib/Logs/session_input.json'
    session = json.loads(session_path.read_text())
    session['indicator_provenance'] = {
        fiber: {color: 'service' for color in values}
        for fiber, values in session['intended_measurements_by_fiber'].items()
    }
    session_path.write_text(json.dumps(session))
    originals = {name: (root / name).read_bytes() for name in source_names}
    output = json.loads(map_acquisition(root).read_text())
    Acquisition.model_validate(output)
    stream = output['data_streams'][0]
    hsfp = stream['code'][0]['parameters']['hsfp']
    assert set(hsfp) == {
        'segment_id', 'intended_measurements_by_fiber', 'imaging',
        'writer', 'firmware', 'lookup_table',
        'source_directory', 'acquisition_provenance',
    }
    assert hsfp['intended_measurements_by_fiber'] == session['intended_measurements_by_fiber']
    assert hsfp['intended_measurements_by_fiber']['Fiber_0']['yellow'] is None
    assert hsfp['intended_measurements_by_fiber']['Fiber_0']['blue'] == 'None'
    assert 'camera_readback' not in hsfp
    assert 'power_calibration_sources' not in hsfp
    assert hsfp['acquisition_provenance'] == {'dirty': False}
    assert 'version' not in stream['code'][0]  # unused optional field
    assert stream['code'][0]['commit_hash'] == 'a' * 40
    instrument = json.loads(originals['instrument_fib.json'])
    for laser in (c for c in stream['configurations'] if c['object_type'] == 'Laser config'):
        calibration = max(
            (c for c in instrument['calibrations'] if c['device_name'] == laser['device_name']),
            key=lambda c: c['calibration_date'],
        )
        assert laser['power'] == calibration['output'][0]
        assert laser['power_unit'] == calibration['output_unit']
        assert laser['power_measured_at'] == calibration['measured_at']
    cords = [c for c in stream['configurations'] if c['object_type'] == 'Patch cord config']
    channels = [channel for cord in cords for channel in cord['channels']]
    assert len(cords) == 2 and len(channels) == 10
    assert all(c['additional_device_names'] is None for c in channels)
    assert all(c['variable_power'] is False for c in channels)
    assert len([c for c in stream['configurations'] if c['object_type'] == 'Laser config']) == 5
    assert output['subject_details']['animal_weight_prior'] is None
    assert output['subject_details']['animal_weight_post'] is None
    assert output['calibrations'] == output['maintenance'] == output['manipulations'] == output['stimulus_epochs'] == []
    for name, original in originals.items():
        assert (root / name).read_bytes() == original


def test_verified_inferred_end_keeps_its_evidence(recording):
    root, _, _ = recording
    journal = root / 'fib/recording_segments.jsonl'
    events = [json.loads(line) for line in journal.read_text().splitlines()]
    evidence = {
        'verified': True,
        'last_sample_source': 'fib/last_sample.csv',
        'utc_mapping_source': 'fib/utc_mapping.json',
    }
    (root / evidence['last_sample_source']).write_text('FrameCounter,Timestamp\n10,0.1\n')
    (root / evidence['utc_mapping_source']).write_text('{"reviewed": true}')
    events[-1].update(event='end_inferred', source='operator', details=evidence)
    journal.write_text(''.join(json.dumps(event) + '\n' for event in events))
    output = json.loads(map_acquisition(root).read_text())
    hsfp = output['data_streams'][0]['code'][0]['parameters']['hsfp']
    assert hsfp['end_provenance'] == {'event': 'end_inferred', 'source': 'operator', 'details': evidence}
