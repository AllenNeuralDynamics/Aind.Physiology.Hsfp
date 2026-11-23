"""Names of the HSFP instrument devices referenced by the acquisition metadata.

These must match the device names of the instrument metadata (``instrument_fib.json``).
"""

TTL_BUFFER = "cuTTLefish TTL Buffer"

COMMON_OPTICS = (
    "Galaxy 8 Laser Combiner",
    "Galaxy 8 Output Fiber",
    "FC/PC MM Patch Cord",
    "Laser De-Speckler",
    "1x2 Splitter Branching Patchcord Attenuating",
    "Bundle Branching Fiber-optic Patch Cord",
    "CFI60 Plan Apochromat Lambda D 10x",
    "Slit",
    "Prism",
)


def patch_cord_name(index: int) -> str:
    """Name of the patch cord with the given index."""
    return f"Mono Fiber-optic Patch Cord {index}"


def patch_cord_optics(index: int) -> tuple[str, ...]:
    """Optical components that belong to the light path of one patch cord."""
    return (f"Variable Optic Attenuator FC/PC MM {index}", f"Wideband MM Circulator {index}")


def fiber_name(fiber_number: int) -> str:
    """Name of an implanted fiber, as recorded in the subject's procedures."""
    return f"Fiber_{fiber_number}"
