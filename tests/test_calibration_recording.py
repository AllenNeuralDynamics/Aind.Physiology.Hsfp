"""Hardware-only recordings stay distinct from the biological Acquisition contract."""

import json
from datetime import UTC, datetime

import pytest
from aind_physiology_hsfp.data_contract import canonical_name
from aind_physiology_hsfp.data_mappers import map_acquisition
from aind_physiology_hsfp.session_input import CalibrationInput


def test_calibration_generates_valid_subject_empty_procedures_and_acquisition(recording, monkeypatch):
    from aind_data_schema.core.acquisition import Acquisition
    from aind_data_schema.core.procedures import Procedures
    from aind_data_schema.core.subject import Subject
    from aind_physiology_hsfp.data_contract import atomic_json
    from aind_physiology_hsfp.data_mappers._acquisition import write_calibration_metadata

    root, _, _ = recording
    session = CalibrationInput(experimenters=['Operator'], configuration_confirmed=True)
    assert canonical_name(session, datetime(2026, 10, 20, 19, 30, tzinfo=UTC)) == 'calibration_2026-10-20_12-30-00'
    atomic_json(root / 'fib/Logs/session_input.json', session.model_dump(mode='json'))
    segment_path = root / 'fib/segment_0001/segment_input.json'
    segment = json.loads(segment_path.read_text())
    segment['session'] = session.model_dump(mode='json')
    atomic_json(segment_path, segment)
    write_calibration_metadata(root, session)
    monkeypatch.setattr(
        'aind_physiology_hsfp.data_mappers._acquisition.fiber_implants_from_procedures',
        lambda *a, **k: pytest.fail('Calibration must not resolve mouse implants'),
    )
    payload = json.loads(map_acquisition(root).read_text())
    model = Acquisition.model_validate(payload)
    assert model.subject_id == 'calibration'
    assert model.acquisition_type == 'HSFP_calibration'
    assert model.subject_details is None
    assert model.ethics_review_id is None
    subject = Subject.model_validate_json((root / 'subject.json').read_text())
    assert subject.subject_details.empty is True
    assert subject.subject_details.description == 'HSFP calibration without a mouse'
    procedures = Procedures.model_validate_json((root / 'procedures.json').read_text())
    assert procedures.subject_id == 'calibration'
    assert procedures.subject_procedures == procedures.specimen_procedures == []
    stream = payload['data_streams'][0]
    assert 'intended_measurements_by_fiber' not in stream['code'][0]['parameters']['hsfp']
    assert not any(name.startswith('Fiber_') or name.startswith('Implant') for name in stream['active_devices'])
    channels = [channel for config in stream['configurations'] if config['object_type'] == 'Patch cord config' for channel in config['channels']]
    assert len(channels) == 10
    assert channels[0]['channel_name'] == 'PatchCord_0_405'
    assert all(connection['target_device'] not in ['Implant 0', 'Implant 1'] for connection in stream['connections'])
    originals = {name: (root / name).read_bytes() for name in ['subject.json', 'procedures.json']}
    write_calibration_metadata(root, session)
    assert all((root / name).read_bytes() == data for name, data in originals.items())


def test_calibration_documents_are_not_silently_replaced(tmp_path):
    from aind_physiology_hsfp.data_mappers._acquisition import write_calibration_metadata

    session = CalibrationInput(experimenters=['Operator'])
    (tmp_path / 'subject.json').write_text('{"wrong": "document"}')
    with pytest.raises(ValueError):
        write_calibration_metadata(tmp_path, session)
    assert (tmp_path / 'subject.json').read_text() == '{"wrong": "document"}'
    assert not (tmp_path / 'procedures.json').exists()
