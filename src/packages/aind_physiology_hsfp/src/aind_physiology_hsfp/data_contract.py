"""Durable recording sources and safe AIND root finalization."""

import hashlib
import json
import logging
import os
import time
from datetime import UTC, datetime
from pathlib import Path
from uuid import uuid4
from zoneinfo import ZoneInfo

from .session_input import JournalEvent, SessionInput

logger = logging.getLogger(__name__)


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def atomic_json(path: Path, value: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(f".{path.name}.{uuid4().hex}.tmp")
    try:
        with temporary.open("x", encoding="utf-8") as stream:
            json.dump(value, stream, indent=2, allow_nan=False)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def append_event(root: Path, event: JournalEvent) -> None:
    path = root / "fib/recording_segments.jsonl"
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("a", encoding="utf-8") as stream:
        stream.write(event.model_dump_json() + "\n")
        stream.flush()
        os.fsync(stream.fileno())


def read_events(root: Path) -> list[JournalEvent]:
    path = root / "fib/recording_segments.jsonl"
    events = []
    for index, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        if line.strip():
            try:
                events.append(JournalEvent.model_validate_json(line))
            except ValueError as exc:
                raise ValueError(f"Invalid journal line {index}; preserve it for explicit recovery") from exc
    return events


def canonical_name(session: SessionInput, first_start: datetime) -> str:
    if first_start.tzinfo is None:
        raise ValueError("Cannot name an acquisition from a naive timestamp")
    local = first_start.astimezone(ZoneInfo(session.site_timezone))
    return f"{session.subject_id}_{local:%Y-%m-%d_%H-%M-%S}"


def finalization_target(root: Path, session: SessionInput) -> Path:
    starts = [e.timestamp for e in read_events(root) if e.event == "recording_start"]
    if not starts:
        raise ValueError("No recording start; acquisition root cannot be finalized")
    return root.parent / canonical_name(session, min(starts))


def finalize_root(root: Path, session: SessionInput) -> Path:
    """Call only after Bonsai and all writers exit. No overwrite or nonstandard suffixes."""
    target = finalization_target(root, session)
    if root == target:
        return root
    if not root.name.startswith(".hsfp-pending-"):
        raise ValueError("Existing acquisition name disagrees with its first start; do not silently rename")
    if target.exists():
        raise FileExistsError(f"Acquisition already exists: {target}")
    atomic_json(root / "fib/Logs/root_finalization.json", {"target": target.name, "first_start_source": "journal"})
    # Never overwrite an existing empty directory, including on POSIX.
    # The single-acquisition launcher owns this parent; exclusive reservation prevents races.
    lock = target.with_name(f".{target.name}.reservation")
    with lock.open("x", encoding="utf-8") as stream:
        stream.write(root.name)
    try:
        for attempt in range(6):
            if target.exists():
                raise FileExistsError(target)
            try:
                root.rename(target)
                break
            except PermissionError as exc:
                # Only retry Windows access/share locks, after all writers have exited.
                # ACL restrictions and persistent open handles still need operator action.
                if getattr(exc, "winerror", None) not in {5, 32, 33}:
                    raise
                if attempt == 5:
                    raise PermissionError(
                        f"Windows could not rename the completed recording folder. "
                        f"Recording retained at: {root}. Intended final folder: {target}. "
                        "Close Bonsai and any programs using files in this recording, then run "
                        "recover-metadata and select the retained folder. Do not start a new recording."
                    ) from exc
                logger.warning("Windows denied recording-folder rename; retrying in 1 second: %s", root)
                time.sleep(1)
    finally:
        lock.unlink(missing_ok=True)
    return target


def allocate_segment(root: Path) -> tuple[str, Path]:
    parent = root / "fib/segments"
    parent.mkdir(parents=True, exist_ok=True)
    for number in range(1, 1000000):
        sid = f"segment_{number:04d}"
        path = parent / sid
        try:
            path.mkdir()
            return sid, path
        except FileExistsError:
            continue
    raise RuntimeError("Segment namespace exhausted")


def utcnow() -> datetime:
    return datetime.now(UTC)
