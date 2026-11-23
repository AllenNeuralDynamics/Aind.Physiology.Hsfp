"""Bonsai workflow checks: static graph structure, and a deserialization round trip through Bonsai."""

import sys
import xml.etree.ElementTree as ET
from graphlib import TopologicalSorter
from pathlib import Path

import pytest
from aind_behavior_services.session import Session
from aind_behavior_services.utils import run_bonsai_process
from aind_physiology_hsfp.rig import AindPhysioHsfpRig
from aind_physiology_hsfp.task_logic import AindPhysioHsfpTaskLogic
from pydantic import BaseModel, ValidationError

REPOSITORY = Path(__file__).parents[1]
BONSAI_EXE = REPOSITORY / ".bonsai/Bonsai.exe"
NS = {"b": "https://bonsai-rx.org/2018/workflow"}
XSI_TYPE = "{http://www.w3.org/2001/XMLSchema-instance}type"
PROPERTY_SOURCES = ("ExternalizedMapping", "PropertyMapping")
WORKFLOWS = sorted((REPOSITORY / "src").rglob("*.bonsai"))


@pytest.mark.parametrize("path", WORKFLOWS, ids=lambda p: p.name)
def test_workflow_graphs_are_valid(path: Path):
    """Every (nested) graph has valid edges, unique input labels and no cycles."""
    root = ET.parse(path).getroot()
    for graph in root.iter(f"{{{NS['b']}}}Workflow"):
        nodes, edges = graph.find("b:Nodes", NS), graph.find("b:Edges", NS)
        if nodes is None:
            continue
        predecessors: dict[int, set[int]] = {index: set() for index in range(len(nodes))}
        labels = set()
        for edge in [] if edges is None else list(edges):
            source, target = int(edge.get("From")), int(edge.get("To"))
            assert source in predecessors and target in predecessors, f"{path}: edge {source}->{target}"
            key = (target, edge.get("Label"))
            assert key not in labels, f"{path}: duplicate input {key}"
            labels.add(key)
            predecessors[target].add(source)
        list(TopologicalSorter(predecessors).static_order())
        for index, node in enumerate(nodes):
            data_inputs = [i for i in predecessors[index] if nodes[i].get(XSI_TYPE) not in PROPERTY_SOURCES]
            if node.get(XSI_TYPE) in ("ExternalizedMapping", "SubscribeSubject", "WorkflowInput"):
                assert not data_inputs, f"{path}: data input to source-only node {index}"


@pytest.mark.parametrize("path", WORKFLOWS, ids=lambda p: p.name)
def test_workflows_do_not_save_absolute_paths(path: Path):
    text = path.read_text(encoding="utf-8-sig")
    for forbidden in ("C:\\output_data", "C:/output_data", "C:/git/", "C:\\git\\"):
        assert forbidden not in text, f"{path} contains an absolute path ({forbidden})"


def test_sdk_dependencies_match_the_bonsai_environment():
    project = ET.parse(REPOSITORY / "src/Extensions.csproj").getroot()
    packages = ET.parse(REPOSITORY / ".bonsai/Bonsai.config").getroot()
    versions = {node.get("id"): node.get("version") for node in packages.findall("Packages/Package")}
    for reference in project.findall("ItemGroup/PackageReference"):
        assert reference.get("Version") == versions[reference.get("Include")], reference.get("Include")


@pytest.mark.skipif(not BONSAI_EXE.exists(), reason="Bonsai is not set up (run .bonsai/setup.ps1)")
def test_deserialization(monkeypatch, tmp_path):
    """The example rig, session and task logic deserialize identically through the Bonsai workflow."""
    sys.path.append(str(REPOSITORY))
    from examples import example

    monkeypatch.setattr(sys, "argv", ["example.py", "--path-seed", str(tmp_path / "{schema}.json")])
    example.main()

    models: dict[str, type[BaseModel]] = {
        "RigPath": AindPhysioHsfpRig,
        "SessionPath": Session,
        "TaskPath": AindPhysioHsfpTaskLogic,
    }
    completed = run_bonsai_process(
        workflow_file=(REPOSITORY / "src/test_deserialization.bonsai").resolve(),
        is_editor_mode=False,
        layout=None,
        additional_properties={prop: str(tmp_path / f"{model.__name__}.json") for prop, model in models.items()},
        bonsai_exe=BONSAI_EXE.resolve(),
    )
    lines = [line for line in completed.stdout.decode().splitlines() if line.strip()]
    for model in models.values():
        expected = model.model_validate_json((tmp_path / f"{model.__name__}.json").read_text(encoding="utf-8"))
        matches = []
        for line in lines:
            try:
                matches.append(model.model_validate_json(line, strict=True) == expected)
            except ValidationError:
                continue
        assert any(matches), f"{model.__name__} did not round trip through Bonsai. stderr: {completed.stderr!r}"
