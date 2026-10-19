# HSFP acquisition metadata

The `feat-acquisition-mapper` implementation adds the installable `aind_physiology_hsfp` package and recording extensions. `session_input.py` stores confirmed operator metadata; `rig_config.py` stores runtime configuration. The offline `data_mappers/_acquisition.py` maps saved sources to root `acquisition_fib.json` using aind-data-schema 2.9.1.

Use the sibling Aind.Experiment.Hsfp clabe launcher for new recordings. The workflow now requires a reserved `AcquisitionRoot` and `SegmentId`, alongside SettingsPath, ChannelCount, ChunkSize, TeensyPort and CuttlefishPort. It does not independently generate a competing root name. The launcher finalizes `<subject>_YYYY-MM-DD_HH-MM-SS` from the first recorded start in the configured local timezone, after writers exit. Acquisition metadata preserves acquisition_start_tz as the IANA name, and raw lifecycle timestamps remain UTC.

`HsfpCamera` subclasses the pinned Hamamatsu 0.5.0 node, records applied DCAM property IDs/values/text/units on the first frame and journals that first accepted-frame boundary. This is a host UTC recording boundary, not a clock-synchronized exposure timestamp. `HsfpRecordingEnd` records a stop on normal pipeline completion, never on cancellation/error. A Windows build and rig pilot must verify camera readback and recording completion before use. The csproj dependency is aligned with Bonsai.config's Hamamatsu 0.5.0.

One acquisition contains multiple segment streams. Raw payloads are under `fib/segments/segment_NNNN/fib/`; the inner fib preserves current modality suffixes. Sources/readbacks are isolated per segment. The root instrument snapshot remains fixed across resumes. Existing processing readers require explicit support for this layout before pipeline use.

Run offline:

```powershell
uv run hsfp-map-acquisition C:/output_data/<acquisition-root>
```

The mapper has no service calls or hardware control. It requires saved session/segment inputs, schema-valid instrument and Procedures snapshots, applied camera readback and recorded per-channel Timestamp CSVs. The launcher fetches Procedures by subject from the aind-metadata-service before acquisition and preserves the response and validated snapshot under fib/Logs; operators do not supply a Procedures file. Resume and offline mapping reuse the snapshot. Power calibration values apply to both fibers as confirmed; original measurement location/date remain provenance. Color map string `None` and JSON null remain distinct. Channel names `Fiber_N_[wavelength]` derive from connected implanted fiber, not cord index. Only observed frame rate is reported.

For standalone subject_details, weight measurements remain null. The pinned schema does not permit a null weight_unit; the unused unit is omitted from the written JSON, rather than fabricating a measured weight. The required object_type discriminator is schema-generated.

Incomplete/malformed sources cause an error before atomically replacing metadata. A crash end is never inferred from process exit or folder time. A manually reviewed `end_inferred` journal event requires source paths for the last valid sample and trustworthy UTC mapping plus an explicit verified flag. Fully automatic inference is intentionally unavailable without validated clock evidence. General notes are acquisition-level; stream notes remain unset. Timing alignment belongs to processing. Instrument generation remains in aind-hsfp-metadata.

The mapper validates its Acquisition contribution; it does not merge behavior metadata or certify a complete derived asset. Full metadata/device-reference checks against other core documents and Windows hardware acceptance remain separate checks.
