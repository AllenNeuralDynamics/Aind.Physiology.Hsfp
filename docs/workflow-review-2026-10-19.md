# Acquisition workflow review — 2026-10-19

Scope: the acquisition-mapper branch of Aind.Physiology.Hsfp, including the pending fixes, reviewed against `src/main.bonsai` at `3e7fb1f` before the mapper implementation. This review covers the workflow, custom recording nodes, SDK dependencies, launcher arguments, and output destinations. It does not certify live hardware behavior.

## Findings and corrections

1. The original serial-port wrappers were unnecessary. They introduced additional property layers, disconnected mappings, and a missing Cuttlefish command input. The final workflow uses direct aliases on the existing serial devices: `PortName` displayed as `TeensyPort` or `CuttlefishPort`. Their original command/event connections and node settings are preserved. There are no new serial-device groups to supply inputs to.
2. The recording-root group's property mapping is connected to `HsfpRootPath`. It exposes `Subject`, `AcquisitionRoot` and `SegmentId` through Init. Its `LoggingRootPath` AsyncSubject remains visible to the other groups because Bonsai GroupWorkflow uses the parent variable context.
3. The SDK project's Vision, Vision.Design, Design and LibTiff versions differed from those already loaded by `.bonsai/Bonsai.config`. The project now uses the configured versions, 2.9.0 and 2.4.660 respectively. The Bonsai environment and package configuration were not changed. The earlier Hamamatsu 0.5.0 alignment, Newtonsoft.Json dependency and C# 5 restriction remain.
4. The earlier unsupported auto-property initializer has already been moved into the HsfpCamera constructor. No further C# syntax changes were needed in this review.

The added workflow sections have also been expanded from compressed XML so the property connections can be inspected directly.

## Property contract

| Launcher property | Source | Final destination |
| --- | --- | --- |
| `Subject` | Saved session subject | `HsfpRootPath.Subject`; root naming uses the launcher's saved subject |
| `AcquisitionRoot` | Registered acquisition directory | `HsfpRootPath.AcquisitionRoot` |
| `SegmentId` | Launcher-reserved segment | `HsfpRootPath.SegmentId` |
| `ChannelCount` | Supported mode-c contract: 5 | Init IntProperty; camera channel grouping and replay buffer |
| `ChunkSize` | `rig_config.json` `chunk_size` | TiffChunkSize subject → each existing TiffWriter |
| `SettingsPath` | Segment's saved `sources/camera_settings.xml` | CameraSettingsPath subject → camera SettingsPath; existing settings-copy logger |
| `LookupTablePath` | Optional segment's saved `sources/lookup_table.csv` | CameraLutPath subject → camera LookupTablePath |
| `TeensyPort` | `rig_config.json` `teensy_port` | Existing CreateSerialPort.PortName, through a direct display-name alias |
| `CuttlefishPort` | `rig_config.json` `cuttlefish_port` | Existing Cuttlefish Device.PortName, through a direct display-name alias |

The Teensy's logical connection name stays `Teensy`. Only its physical `PortName` changes. SerialReadLine and SerialWriteLine continue using that logical name. The Cuttlefish commands still feed the device directly and its events still feed CuttlefishEvents. Defaults serialized in the workflow do not replace the values passed by the launcher.

## Recording/output review

- `HsfpRootPath` accepts an existing reserved acquisition/segment, checks `segment_input.json`, rejects a segment that already contains a `fib` payload directory, and emits `root/fib/<segment_id>`. It does not create a competing timestamped acquisition name.
- The existing AppendModalitySuffix nodes then route payloads into `root/fib/<segment_id>/fib`. TIFFs, frame metadata, camera-settings copies, session parameters and hardware logs use the shared root path. The source snapshots and `camera_applied.json` remain outside that payload folder, at the segment level. The lifecycle journal is acquisition-level `fib/recording_segments.jsonl`.
- `HsfpCamera` still derives from the installed Hamamatsu 0.5.0 C13440 source. It forwards frames and their counters unchanged. It adds a one-time DCAM property snapshot and first-accepted-frame host UTC journal event. Its normal camera configuration remains supplied by the saved XML. The pinned camera source was checked at tag `0.5.0`.
- `HsfpRecordingEnd` observes completion of the existing logging TakeUntil branch. It records a normal stop on completion, not on an error or disposal. The launcher subsequently waits for the Bonsai process to exit before finalizing the root name and mapping metadata. The journal describes a host recording boundary, not a hardware exposure timestamp.
- The original FrameCounter modulo ChannelCount grouping is preserved. There is no new counter normalization or readiness gate. StartExperiment still sends `c`; EndExperiment still sends `s`, with the original stop/delay wiring.
- Camera ROI/crop defaults, bundling settings, serial baud rate/handshake, Cuttlefish register settings, TIFF compression/chunking behavior, visualizer settings and original timing delays remain unchanged. `Logging.bonsai`, `LogHsfpFrameStream.bonsai` and `TiffWriter.cs` are unchanged from the pre-mapper implementation.
- The launcher computes the final `<subject>_YYYY-MM-DD_HH-MM-SS` name from the earliest journal start in its configured local timezone after writers exit. Subsequent segments preserve the root instrument snapshot. Existing processing readers still need explicit support for the segmented data layout.

## Verification performed

The combined launcher and acquisition-package suite passed: **62 tests**. The new workflow contract tests check:

- all nine exposed properties through every group/alias to their exact final leaf settings;
- valid node indices, unique connection labels, acyclic graphs and group input capacity across every local `.bonsai` file;
- SDK dependency versions against `.bonsai/Bonsai.config`;
- preservation of the complete original main graph and node settings after accounting for the explicit root, camera-readback, stop-journal and property-exposure changes. The independent original XML is retained in `tests/fixtures/main_before_acquisition_mapper.bonsai` with provenance in the test.

The pre-existing disconnected-mapping checks remain in the suite. Ruff checks and `git diff --check` pass.

Separately, the actual installed clabe 0.13.8 command builder was exercised without executing its command. Cases used COM17/COM23, chunk size 321, segment_0007 and paths with spaces, with and without the optional LUT. All generated property names resolved to one intended leaf target and path arguments stayed intact. Clabe's current editor adapter includes `--start`, which explains why workflow build errors appear immediately on launch.

## Limits and rig acceptance

No Windows/Bonsai executable or .NET compiler is available on the review computer. The tests above are source/contract checks and Python integration checks; they are not a live Bonsai build or DCAM acquisition test. No hardware was started during this review.

On the feature branch at the rig, run the deployment build with the aligned package versions, then make a short recording through the launcher. Verify that Bonsai loads/builds, both serial devices connect using the configured ports, camera readback is captured once, all five channel TIFF/CSV outputs appear under the same segment, EndExperiment produces one normal stop, and closing Bonsai permits final naming and acquisition metadata generation. The first-frame/IO0 relationship and observed rates still require that real pilot. These checks do not require changing the original triggering procedure.

For reverting to the original acquisition on `main`, close Bonsai, switch branches and open `src/main.bonsai` directly. The branches have the same `.bonsai/Bonsai.config` and setup scripts; a package update is not required solely for that switch. The new launcher targets feature-branch properties and should not launch the original main workflow.

## Runtime source references

The review used Bonsai 2.9.0's actual implementations of [externalized property names](https://github.com/bonsai-rx/bonsai/blob/2.9.0/Bonsai.Core/Expressions/WorkflowTypeDescriptor.cs), [property mappings](https://github.com/bonsai-rx/bonsai/blob/2.9.0/Bonsai.Core/Expressions/ExternalizedMappingBuilder.cs), [group scope](https://github.com/bonsai-rx/bonsai/blob/2.9.0/Bonsai.Core/Expressions/GroupContext.cs), and [nested input capacity](https://github.com/bonsai-rx/bonsai/blob/2.9.0/Bonsai.Core/Expressions/WorkflowExpressionBuilder.cs).
