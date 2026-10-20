"""Static Bonsai contracts; these do not replace a Windows load/recording pilot."""

import copy
import xml.etree.ElementTree as E
from graphlib import TopologicalSorter
from pathlib import Path

import pytest

REPOSITORY = Path(__file__).parents[1]
NS = {"b": "https://bonsai-rx.org/2018/workflow"}
XSI = "{http://www.w3.org/2001/XMLSchema-instance}type"


def workflow():
    return E.parse(REPOSITORY / "src/main.bonsai").getroot().find("b:Workflow", NS)


def named_group(nodes, name):
    return next(node for node in nodes if node.findtext("b:Name", namespaces=NS) == name)


def canonical(node):
    children = [canonical(child) for child in node]
    if node.tag.endswith("}Edges"):
        children.sort()
    return node.tag, tuple(sorted(node.attrib.items())), (node.text or "").strip(), tuple(children)


def resolve_property(graph, name):
    nodes, edges = graph.find("b:Nodes", NS), graph.find("b:Edges", NS)
    matches = []
    for index, node in enumerate(nodes):
        if node.get(XSI) != "ExternalizedMapping":
            continue
        for prop in node.findall("b:Property", NS):
            if prop.get("DisplayName", prop.get("Name")) != name:
                continue
            for edge in edges:
                if int(edge.get("From")) != index:
                    continue
                target = nodes[int(edge.get("To"))]
                if target.get(XSI) == "GroupWorkflow":
                    matches.extend(resolve_property(target.find("b:Workflow", NS), prop.get("Name")))
                else:
                    combinator = target.find("b:Combinator", NS)
                    kind = combinator.get(XSI) if combinator is not None else target.get(XSI)
                    member = target.findtext("b:MemberName", namespaces=NS)
                    matches.append((kind, prop.get("Name"), member))
    assert matches, f"Property {name} has no connected leaf target"
    return matches


@pytest.mark.parametrize(
    "name,target",
    [
        ("Subject", ("p1:HsfpRootPath", "Subject", None)),
        ("AcquisitionRoot", ("p1:HsfpRootPath", "AcquisitionRoot", None)),
        ("SegmentId", ("p1:HsfpRootPath", "SegmentId", None)),
        ("ChannelCount", ("IntProperty", "Value", None)),
        ("ChunkSize", ("PropertySource", "Value", "ChunkSize")),
        ("SettingsPath", ("PropertySource", "Value", "SettingsPath")),
        ("LookupTablePath", ("PropertySource", "Value", "LookupTablePath")),
        ("TeensyPort", ("port:CreateSerialPort", "PortName", None)),
        ("CuttlefishPort", ("p6:Device", "PortName", None)),
    ],
)
def test_launcher_properties_resolve_to_the_correct_leaf(name, target):
    assert resolve_property(workflow(), name) == [target]


def test_every_workflow_is_acyclic_and_nested_groups_accept_data_inputs():
    for path in (REPOSITORY / "src").rglob("*.bonsai"):
        root = E.parse(path).getroot()
        for graph in root.findall(".//b:Workflow", NS):
            nodes, edges = graph.find("b:Nodes", NS), graph.find("b:Edges", NS)
            if nodes is None:
                continue
            edges = [] if edges is None else list(edges)
            predecessors = {index: set() for index in range(len(nodes))}
            labels = set()
            for edge in edges:
                source, target = int(edge.get("From")), int(edge.get("To"))
                assert source in predecessors and target in predecessors, path
                key = target, edge.get("Label")
                assert key not in labels, f"{path}: duplicate target argument {key}"
                labels.add(key)
                predecessors[target].add(source)
            list(TopologicalSorter(predecessors).static_order())
            for index, node in enumerate(nodes):
                incoming = [
                    source for source in predecessors[index]
                    if nodes[source].get(XSI) not in ("ExternalizedMapping", "PropertyMapping")
                ]
                if node.get(XSI) == "GroupWorkflow":
                    nested = node.find("b:Workflow/b:Nodes", NS)
                    inputs = [item for item in nested if item.get(XSI) == "WorkflowInput"]
                    assert len(incoming) <= len(inputs), f"{path}: group input capacity"
                if node.get(XSI) in ("ExternalizedMapping", "SubscribeSubject", "WorkflowInput"):
                    assert not incoming, f"{path}: data input to a source-only node"


def test_sdk_dependencies_match_the_bonsai_runtime_configuration():
    project = E.parse(REPOSITORY / "src/Extensions.csproj").getroot()
    packages = E.parse(REPOSITORY / ".bonsai/Bonsai.config").getroot()
    versions = {node.get("id"): node.get("version") for node in packages.findall("Packages/Package")}
    for reference in project.findall("ItemGroup/PackageReference"):
        assert reference.get("Version") == versions[reference.get("Include")]
    assert project.findtext("PropertyGroup/LangVersion") == "5"


def test_original_acquisition_graph_preserved_except_explicit_metadata_hooks():
    # Independent source: main.bonsai at 3e7fb1f, before the mapper implementation.
    original = E.parse(REPOSITORY / "tests/fixtures/main_before_acquisition_mapper.bonsai").getroot()
    expected = original.find("b:Workflow", NS)
    actual = copy.deepcopy(workflow())
    nodes, edges = actual.find("b:Nodes", NS), actual.find("b:Edges", NS)
    old_nodes = expected.find("b:Nodes", NS)
    for prop in list(nodes[0]):
        if prop.get("Name") in ("AcquisitionRoot", "SegmentId"):
            nodes[0].remove(prop)
    init = nodes[1].find("b:Workflow/b:Nodes", NS)
    old_init = old_nodes[1].find("b:Workflow/b:Nodes", NS)
    init.remove(init[1])
    init.insert(1, copy.deepcopy(old_init[1]))
    for prop in list(init[0]):
        if prop.get("Name") in ("AcquisitionRoot", "SegmentId"):
            init[0].remove(prop)
    for prop in list(nodes[2]):
        if prop.get("Name") == "LookupTablePath":
            nodes[2].remove(prop)
    camera = named_group(nodes, "Camera")
    custom = next(node for node in camera.iter() if node.get(XSI) == "p1:HsfpCamera")
    custom.set(XSI, "p2:C13440")
    default_settings = r"C:/git/Aind.Physiology.Hsfp/local/HSFP-default-camera-config.xml"
    settings = custom.find("{clr-namespace:AllenNeuralDynamics.HamamatsuCamera;assembly=AllenNeuralDynamics.HamamatsuCamera}SettingsPath")
    assert settings.text == default_settings
    source = next(
        node for node in camera.iter()
        if node.get(XSI) == "PropertySource" and node.findtext("b:MemberName", namespaces=NS) == "SettingsPath"
    )
    assert source.findtext("b:Value", namespaces=NS) == default_settings
    original_camera = named_group(old_nodes, "Camera")
    original_source = next(
        node for node in original_camera.iter()
        if node.get(XSI) == "PropertySource" and node.findtext("b:MemberName", namespaces=NS) == "SettingsPath"
    )
    source.find("b:Value", NS).text = original_source.findtext("b:Value", namespaces=NS)
    original_node = next(node for node in original_camera.iter() if node.get(XSI) == "p2:C13440")
    settings.text = original_node.find(settings.tag).text
    for name in ("Teensy", "Cuttlefish"):
        group = named_group(nodes, name)
        for mapping in group.findall("b:Workflow/b:Nodes/b:Expression", NS):
            if mapping.get(XSI) == "ExternalizedMapping":
                for prop in mapping.findall("b:Property", NS):
                    prop.attrib.pop("DisplayName", None)
    extras = list(nodes)[len(old_nodes):]
    assert len(extras) == 3
    assert extras[0].find("b:Combinator", NS).get(XSI) == "p1:HsfpRecordingEnd"
    assert [node.find("b:Property", NS).get("Name") for node in extras[1:]] == ["TeensyPort", "CuttlefishPort"]
    for node in extras:
        nodes.remove(node)
    for edge in list(edges):
        if int(edge.get("From")) >= len(old_nodes) or int(edge.get("To")) >= len(old_nodes):
            edges.remove(edge)
    E.SubElement(edges, "{" + NS["b"] + "}Edge", From="16", To="17", Label="Source1")
    assert canonical(actual) == canonical(expected)
