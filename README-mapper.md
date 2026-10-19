# HSFP acquisition metadata

The `feat-acquisition-mapper` implementation adds the installable `aind_physiology_hsfp` package and recording extensions. `session_input.py` stores confirmed operator metadata; `rig_config.py` stores runtime configuration. The offline `data_mappers/_acquisition.py` maps saved sources to root `acquisition_fib.json` using aind-data-schema 2.9.1.

Use the sibling Aind.Experiment.Hsfp clabe launcher for new recordings. The workflow now requires a reserved `AcquisitionRoot` and `SegmentId`, alongside SettingsPath, ChannelCount, ChunkSize, TeensyPort and CuttlefishPort. It does not independently generate a competing root name. The launcher finalizes `<subject>_YYYY-MM-DD_HH-MM-SS` from the first recorded start in the configured local timezone, after writers exit. Acquisition metadata preserves acquisition_start_tz as the IANA name, and raw lifecycle timestamps remain UTC.

`HsfpCamera` subclasses the pinned Hamamatsu 0.5.0 node, records applied DCAM property IDs/values/text/units on the first frame and journals that first accepted-frame boundary. This is a host UTC recording boundary, not a clock-synchronized exposure timestamp. `HsfpRecordingEnd` records a stop on normal pipeline completion, never on cancellation/error. A Windows build and rig pilot must verify camera readback and recording completion before use. The csproj dependency is aligned with Bonsai.config's Hamamatsu 0.5.0.

Bonsai compiles `src/Extensions/*.cs` when loading the workflow, separately from the SDK build. `Extensions.csproj` limits the SDK build to C# 5 so it rejects newer syntax before the rig launch. Keep property defaults in constructors rather than auto-property initializers. The SDK build does not replace a successful load in Bonsai; check the launcher's stderr for script compilation errors if the editor reports missing externalized properties.

One acquisition contains multiple segment streams. Raw payloads are under `fib/segments/segment_NNNN/fib/`; the inner fib preserves current modality suffixes. Sources/readbacks are isolated per segment. The root instrument snapshot remains fixed across resumes. Existing processing readers require explicit support for this layout before pipeline use.

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

The output retains the agreed two patch-cord configurations with five channels each, fiber-based channel names, camera/laser settings, fixed optical-device references, physical connections, observed rates, biological map, recording bounds, timezone, software identity, firmware/storage/LUT settings and calibration dates. Shared per-channel detector, laser and optical fields remain in their agreed schema locations.

Stream-level LaserConfig records are retained alongside the matching channel configurations: the pinned FIB schema requires a stream-level light-source configuration and does not count channel-nested configurations toward that requirement. Full camera properties remain in each segment's `camera_applied.json`, referenced by `code.parameters.hsfp.camera_readback`. Power calibration entries retain only their root `instrument_fib.json` source and calibration date; actual powers and measurement locations remain in the channel LaserConfig objects.

Indicator source/change provenance, full calibration/readback records, validation reports, hashes and other detailed evidence stay in the existing saved session/segment inputs, source files and logs. The acquisition JSON no longer duplicates the indicator-provenance table, fiber maps, mapper hash, validation-report path, explanatory flags or rate-estimator string. Code.commit_hash is retained; the custom acquisition-provenance entry retains only the dirty-state flag. Detailed end provenance is included only for an explicitly verified inferred end.

Unused optional model fields are omitted. Null biological-map values and literal string `None` remain distinct and all six color keys are preserved. The agreed null standalone subject-detail values are also retained. The reduced payload receives full Acquisition schema validation before atomic replacement. This changes metadata serialization only; it does not alter Bonsai acquisition, source snapshots or raw data.
