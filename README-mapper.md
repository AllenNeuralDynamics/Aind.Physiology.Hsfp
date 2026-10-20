# HSFP acquisition metadata

The `feat-acquisition-mapper` implementation adds the installable `aind_physiology_hsfp` package and recording extensions. `session_input.py` stores confirmed operator metadata; `rig_config.py` stores runtime configuration. The offline `data_mappers/_acquisition.py` maps saved sources to root `acquisition_fib.json` using aind-data-schema 2.9.1.

Use the sibling Aind.Experiment.Hsfp clabe launcher for new recordings. Launcher-managed recordings supply a reserved `AcquisitionRoot` and `SegmentId`, alongside SettingsPath, ChannelCount, ChunkSize, TeensyPort and CuttlefishPort. When those two properties are both empty, an independent hardware recording uses a fresh manual root; it does not generate biological Acquisition metadata. The launcher finalizes `<subject>_YYYY-MM-DD_HH-MM-SS` from the first recorded start in the configured local timezone, after writers exit. Acquisition metadata preserves acquisition_start_tz as the IANA name, and raw lifecycle timestamps remain UTC.

`HsfpCamera` subclasses the pinned Hamamatsu 0.5.0 node, records applied DCAM property IDs/values/text/units on the first frame and journals that first accepted-frame boundary. This is a host UTC recording boundary, not a clock-synchronized exposure timestamp. `HsfpRecordingEnd` records a stop on normal pipeline completion, never on cancellation/error. A Windows build and rig pilot must verify camera readback and recording completion before use. The csproj dependency is aligned with Bonsai.config's Hamamatsu 0.5.0.

Bonsai compiles `src/Extensions/*.cs` when loading the workflow, separately from the SDK build. `Extensions.csproj` limits the SDK build to C# 5 so it rejects newer syntax before the rig launch. Keep property defaults in constructors rather than auto-property initializers. The SDK build does not replace a successful load in Bonsai; check the launcher's stderr for script compilation errors if the editor reports missing externalized properties.

One acquisition contains multiple segment streams. Raw payloads are under `fib/segment_NNNN/fib/`; the inner fib preserves current modality suffixes. Sources/readbacks are isolated per segment. The root instrument snapshot remains fixed across resumes. Existing processing readers require explicit support for this layout before pipeline use.

### Moving recordings made with the previous segment layout

With Bonsai closed, move each complete `segment_NNNN` directory from `<root>/fib/segments/` into `<root>/fib/`, preserving its name and all contents. Remove the old `segments` directory when empty. Keep `fib/recording_segments.jsonl`, root metadata and `fib/Logs` in place. Never merge two directories with the same segment name.

Then use the updated wrapper's **recover-metadata** action and select the acquisition root to regenerate `acquisition_fib.json` with the new `source_directory` and `camera_readback` references. A plain folder move does not update those fields. If a manually recovered end references evidence under the old path, update those evidence paths as part of the reviewed migration. The allocator and mapper reject unmoved nested segment directories rather than mix both layouts. No automatic recording migration is performed.


See the [2026-10-19 workflow review](docs/workflow-review-2026-10-19.md) for the complete property contract, source audit, preserved acquisition settings, verification evidence and remaining rig checks.

Run offline:

```powershell
uv run hsfp-map-acquisition C:/output_data/<acquisition-root>
```

The mapper has no service calls or hardware control. It requires saved session/segment inputs, a schema-valid instrument snapshot, a preserved Procedures snapshot with matching subject and valid, uniquely named fiber implants/configurations, applied camera readback and recorded per-channel Timestamp CSVs. The launcher fetches Procedures by subject from the aind-metadata-service before acquisition and preserves the response and unchanged snapshot under fib/Logs; operators do not supply a Procedures file. Resume and offline mapping reuse the snapshot. Power calibration values apply to both fibers as confirmed; original measurement location/date remain provenance. Color map string `None` and JSON null remain distinct. Channel names `Fiber_N_[wavelength]` derive from connected implanted fiber, not cord index. Only observed frame rate is reported.

For standalone subject_details, weight measurements remain null. The pinned schema does not permit a null weight_unit; the unused unit is omitted from the written JSON, rather than fabricating a measured weight. The required object_type discriminator is schema-generated.

Incomplete/malformed sources cause an error before atomically replacing metadata. A crash end is never inferred from process exit or folder time. A manually reviewed `end_inferred` journal event requires source paths for the last valid sample and trustworthy UTC mapping plus an explicit verified flag. Fully automatic inference is intentionally unavailable without validated clock evidence. General notes are acquisition-level; stream notes remain unset. Timing alignment belongs to processing. Instrument generation remains in aind-hsfp-metadata.

The mapper validates its Acquisition contribution; it does not merge behavior metadata or certify a complete derived asset. Full metadata/device-reference checks against other core documents and Windows hardware acceptance remain separate checks.

The launcher and offline mapper share the same required subject/fiber validation. Full upstream Procedures schema errors are saved in `fib/Logs/procedures_validation.json` and logged as warnings when required implant checks pass. The report stays in the acquisition logs; the acquisition metadata still receives full Acquisition schema validation. Missing or ambiguous fiber identities/configurations remain errors. The source is never repaired or labeled fully compliant when full-document validation fails.

## Compact acquisition output

The output retains the agreed two patch-cord configurations with five channels each, fiber-based channel names, camera/laser settings, stream-level active device names, physical connections, observed rates, biological map, recording bounds, timezone, software identity, firmware/storage/LUT settings and calibration dates. Per-channel detector and laser settings remain in their schema locations; additional_device_names is explicitly null.

Stream-level LaserConfig records are retained alongside the matching channel configurations: the pinned FIB schema requires a stream-level light-source configuration and does not count channel-nested configurations toward that requirement. Full camera properties remain in each segment's `camera_applied.json`, referenced by `code.parameters.hsfp.camera_readback`. Power calibration entries retain only their root `instrument_fib.json` source and calibration date; actual powers and measurement locations remain in the channel LaserConfig objects.

Indicator source/change provenance, full calibration/readback records, validation reports, hashes and other detailed evidence stay in the existing saved session/segment inputs, source files and logs. The acquisition JSON no longer duplicates the indicator-provenance table, fiber maps, mapper hash, validation-report path, explanatory flags or rate-estimator string. Code.commit_hash is retained; the custom acquisition-provenance entry retains only the dirty-state flag. Detailed end provenance is included only for an explicitly verified inferred end.

Unused optional model fields are omitted. Null biological-map values and literal string `None` remain distinct and all six color keys are preserved. The agreed null standalone subject-detail values are also retained. The reduced payload receives full Acquisition schema validation before atomic replacement. This changes metadata serialization only; it does not alter Bonsai acquisition, source snapshots or raw data.

## Intended measurements and channel device references

Each channel explicitly writes `additional_device_names: null`. The fixed optical path remains in `instrument_fib.json`; active devices remain listed once at stream level. Channel names, detector/laser settings and physical cord-to-implant connections are unchanged.

The biological map is now named `intended_measurements_by_fiber`. New `session_input.json` and per-segment saved inputs use this name (input contract 1.1.0). Older `indicators_by_fiber` inputs are accepted when resuming or rebuilding metadata; the original source files are not rewritten by the mapper. Supplying both field names is rejected as ambiguous. String `"None"` and JSON null retain their distinct meanings.

In `aind-data-schema==2.9.1`, `DataStream.configurations` is a union of predefined device configuration models with extra fields forbidden. Neither PatchCordConfig nor Channel accepts this structured map; Channel.intended_measurement is a single optional string. Therefore the renamed map remains at `data_streams[].code[].parameters.hsfp.intended_measurements_by_fiber` until the upstream schema provides a suitable configuration field. No custom configuration type, JSON string encoding, or color-to-laser inference is introduced.

## Standalone and calibration recordings

The wrapper exposes `calibration` for no-mouse recordings. It saves hardware sources and segments without biological service lookups, then generates Acquisition with `subject_id: calibration`, a Subject containing the confirmed CalibrationObject, and empty Procedures. CalibrationObject is in `subject.json.subject_details`; Acquisition's optional animal details stay unset. The operator confirms empty versus an object and provides a description. Calibration inputs use a separate `CalibrationInput` model; existing animal SessionInput validation is unchanged. Calibration channels describe physical patch cords (for example PatchCord_0_405), not implanted fibers. See the wrapper README for operation and resume/recovery.

The top-level SettingsPath and camera-node SettingsPath both default to `C:/git/Aind.Physiology.Hsfp/local/HSFP-default-camera-config.xml`. The launcher overrides recording settings from rig_config.json's camera_settings through the segment's saved XML copy. For an independent manual override, set both workflow and camera-node paths to the same file; independent Bonsai does not load rig_config.json.

When opening Bonsai independently, leave `AcquisitionRoot` and `SegmentId` both empty. `HsfpRootPath` creates a unique `<subject>_<local-time>_<unique-id>` folder from the entered Subject under `C:/output_data` (or `HSFP_MANUAL_OUTPUT_DIRECTORY`), records the same image/timing payload and captures applied settings. The independent source marker is not a complete launcher metadata bundle. Reserved roots retain the strict source/overwrite checks. No new workflow properties or acquisition-graph changes are required.

`HsfpCameraEditor` delegates to the upstream camera editor with an explicit preview flag. Preview subscriptions bypass metadata capture, so inspecting settings neither requires a segment nor writes into a previous recording. Normal recordings still require an initialized recording root.

Bonsai and Extensions.csproj both use official HamamatsuCamera 0.5.0. The proposed custom package/build step has been removed. The settings preview bypass is local to HsfpCamera; it does not change upstream DCAM property grouping. Rig comparison reproduced the CreateGroupLabel dictionary error in both C13440 and HsfpCamera, but only C13440 displayed triggered preview frames before the preview fix. Treat the shared dictionary error and the custom node's preview failure as separate symptoms; verify the preview fix on Windows.
