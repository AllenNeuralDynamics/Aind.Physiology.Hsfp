"""Windows rename locks must not lose, overwrite or reacquire raw data."""

from pathlib import Path

import pytest
from aind_physiology_hsfp import data_contract


def windows_lock(code=5):
    error = PermissionError("Access is denied")
    error.winerror = code
    return error


@pytest.mark.parametrize("code", [5, 32, 33])
def test_finalization_retries_transient_windows_lock(recording, monkeypatch, code):
    root, session, _ = recording
    payload = root / "fib/segment_0001/fib/raw-sentinel.tiff"
    payload.write_bytes(b"unchanged raw payload")
    original_rename = Path.rename
    attempts, sleeps = [], []

    def rename(path, target):
        attempts.append((path, target))
        if len(attempts) < 3:
            raise windows_lock(code)
        return original_rename(path, target)

    monkeypatch.setattr(Path, "rename", rename)
    monkeypatch.setattr(data_contract.time, "sleep", sleeps.append)
    final = data_contract.finalize_root(root, session)
    assert len(attempts) == 3 and sleeps == [1, 1]
    assert (final / payload.relative_to(root)).read_bytes() == b"unchanged raw payload"
    assert not root.exists()
    assert not list(root.parent.glob("*.reservation"))


def test_persistent_lock_keeps_recording_and_explains_recovery(recording, monkeypatch):
    root, session, _ = recording
    before = {p.relative_to(root): p.read_bytes() for p in root.rglob("*") if p.is_file()}
    attempts = []

    def rename(path, target):
        attempts.append(target)
        raise windows_lock()

    monkeypatch.setattr(Path, "rename", rename)
    monkeypatch.setattr(data_contract.time, "sleep", lambda seconds: None)
    with pytest.raises(PermissionError, match="recover-metadata") as error:
        data_contract.finalize_root(root, session)
    assert str(root) in str(error.value)
    assert len(attempts) == 6
    assert root.exists() and not attempts[0].exists()
    assert all((root / path).read_bytes() == data for path, data in before.items())
    assert not list(root.parent.glob("*.reservation"))


def test_non_windows_permission_error_is_not_retried(recording, monkeypatch):
    root, session, _ = recording
    error = PermissionError("not a Windows sharing error")

    def rename(*args):
        raise error

    monkeypatch.setattr(Path, "rename", rename)
    monkeypatch.setattr(data_contract.time, "sleep", lambda seconds: pytest.fail("Must not retry"))
    with pytest.raises(PermissionError) as caught:
        data_contract.finalize_root(root, session)
    assert caught.value is error
    assert root.exists()
    assert not list(root.parent.glob("*.reservation"))


def test_collision_appearing_during_retry_is_never_overwritten(recording, monkeypatch):
    root, session, _ = recording
    target = data_contract.finalization_target(root, session)
    attempts = []

    def rename(*args):
        attempts.append(args)
        raise windows_lock()

    def create_collision(seconds):
        target.mkdir()
        (target / "unrelated.txt").write_text("preserve me")

    monkeypatch.setattr(Path, "rename", rename)
    monkeypatch.setattr(data_contract.time, "sleep", create_collision)
    with pytest.raises(FileExistsError):
        data_contract.finalize_root(root, session)
    assert len(attempts) == 1 and root.exists()
    assert (target / "unrelated.txt").read_text() == "preserve me"
    assert not list(root.parent.glob("*.reservation"))
