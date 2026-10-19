"""Validate HSFP implant inputs while reporting unrelated upstream schema errors."""

import json
import logging
from copy import deepcopy
from pathlib import Path

from aind_data_schema.components.devices import FiberProbe
from aind_data_schema.components.surgery_procedures import ProbeImplant
from aind_data_schema.core.procedures import Procedures
from pydantic import ValidationError

from .data_contract import atomic_json

logger = logging.getLogger(__name__)


def fiber_implants_from_procedures(
    procedures: dict, subject_id: str, *, report_path: Path | None = None
) -> dict[str, dict]:
    """Require matching subject and valid, uniquely named fiber implants; never repair the source."""
    if not isinstance(procedures, dict):
        raise ValueError("Procedures source must be one document")
    # Schema migrations may mutate input dictionaries, so validate copies only.
    full_errors = []
    try:
        Procedures.model_validate(deepcopy(procedures))
    except ValidationError as exc:
        full_errors = json.loads(exc.json(include_url=False, include_context=False, include_input=False))

    errors = []
    implants = {}
    locations = {}
    if procedures.get("object_type") != "Procedures":
        errors.append("Expected a Procedures document")
    if procedures.get("subject_id") != subject_id:
        errors.append(f"Procedures subject {procedures.get('subject_id')!r} does not match acquisition {subject_id!r}")
    surgeries = procedures.get("subject_procedures")
    if not isinstance(surgeries, list):
        errors.append("Procedures subject_procedures must be a list")
        surgeries = []
    for i, surgery in enumerate(surgeries):
        if not isinstance(surgery, dict):
            errors.append(f"subject_procedures[{i}] must be an object")
            continue
        children = surgery.get("procedures", [])
        if not isinstance(children, list):
            errors.append(f"subject_procedures[{i}].procedures must be a list")
            continue
        for j, procedure in enumerate(children):
            location = f"subject_procedures[{i}].procedures[{j}]"
            if not isinstance(procedure, dict):
                errors.append(f"{location} must be an object")
                continue
            device = procedure.get("implanted_device")
            if not isinstance(device, dict):
                if procedure.get("object_type") == "Probe implant":
                    errors.append(f"{location} lacks an identifiable implanted probe")
                continue
            device_type = device.get("object_type")
            if device_type != "Fiber probe":
                if procedure.get("object_type") == "Probe implant" and device_type != "Ephys probe":
                    errors.append(f"{location} has an unknown implanted probe type")
                continue
            if surgery.get("object_type") != "Surgery" or procedure.get("object_type") != "Probe implant":
                errors.append(f"{location}: Fiber probe must belong to a Surgery / Probe implant record")
                continue
            try:
                fiber = FiberProbe.model_validate(deepcopy(device))
                implant = ProbeImplant.model_validate(deepcopy(procedure))
            except ValidationError as exc:
                details = json.loads(exc.json(include_url=False, include_context=False, include_input=False))
                errors.append({"location": location, "errors": details})
                continue
            if not fiber.name.strip():
                errors.append(f"{location}: Fiber probe name must not be blank")
                continue
            if implant.device_config.device_name != fiber.name:
                errors.append(f"{location}: Probe config device_name does not match implanted fiber {fiber.name!r}")
                continue
            if fiber.name in implants:
                errors.append(f"Duplicate implanted fiber name {fiber.name!r}; connection identity is ambiguous")
                continue
            implants[fiber.name] = deepcopy(device)
            locations[fiber.name] = location
    if not implants:
        errors.append("Procedures contains no valid Fiber probe implants")
    report = {
        "report_type": "HSFP procedures source validation",
        "scope": "Subject identity and implanted fiber devices/configurations required by the acquisition mapper",
        "subject_id": subject_id,
        "source_subject_id": procedures.get("subject_id"),
        "full_document_schema_valid": not full_errors,
        "full_document_schema_errors": full_errors,
        "required_fields_valid": not errors,
        "required_field_errors": errors,
        "fiber_implant_locations": locations,
        "source_modified": False,
        "note": "HSFP acquisition validation does not certify the entire upstream Procedures document.",
    }
    if report_path is not None:
        atomic_json(report_path, report)
    if errors:
        raise ValueError(f"Procedures required subject/fiber validation failed: {errors[0]}")
    if full_errors and report_path is not None:
        logger.warning(
            "Procedures has %d upstream schema errors; required subject/fiber checks passed. "
            "Source preserved unchanged; see %s.",
            len(full_errors), report_path or "full_document_schema_errors",
        )
    return implants
