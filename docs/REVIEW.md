# YTAHD — Architecture Reviews

Append-only collection of architecture reviews. Each review is a dated section; add new
reviews at the bottom. Use the **Findings** and **Refactoring plan** sections when planning
future refactoring work: every finding cites file/symbol evidence, and every plan step names
the ADR it requires (`F-`/`TD-`/`CR-` per repo convention).

Conventions for this file:

- **Append-only**: never delete or rewrite an existing review section. If a finding is later
  resolved, add a `Status: resolved by <ADR-id>` line under that finding.
- **Severity scale**: `critical` (protocol/data-integrity risk), `major` (maintainability or
  correctness risk that should be fixed before the next protocol change), `minor` (worth
  scheduling), `nit` (optional polish).
- Each refactoring step is intended to be independently shippable: one step = one commit =
  one ADR, with the test that proves it named inline.

## Review index

| #   | Date       | Scope                        | Findings (critical/major/minor/nit) |
| --- | ---------- | ---------------------------- | ----------------------------------- |
| 1   | 2026-09-13 | Full project (static review) | 0 / 5 / 6 / 2                       |

---

## Review 1 — 2026-09-13 — Full project review (baseline)

**Reviewer:** architect-review agent
**Scope:** `YTAHD.Core` (Application / Core / Modulation / Infrastructure / Audio), `YTAHD.Cli`,
`YTAHD.Tests` layout, `YTAHD.Perf`, `probe`, `docs/` (PLAN, BACKLOG, PROBLEM, TODO, 46 ADRs).
**Baseline evidence:** `dotnet build YTAHD.sln --no-restore` → exit 0, 0 warnings, 0 errors,
6.3 s; all four projects compile.
**Method:** static review traced through the real call paths (encode serial + parallel,
decode serial + parallel, durability strict + hole-tolerant, packet codec v1/v2, all four
modulators and their frame-bit decoders, child-process pipe infrastructure). No runtime
profiling in this pass; performance claims cited from ADR/README records only.

### Summary

The codebase is in strong health. Layering is clean — the Application service boundary holds,
modulators contain no FFmpeg leakage, and the protocol/durability machinery (strict per-frame
SHA-256 on v2 packets, redundant reconciled stream manifests, hole-tolerant parity recovery,
duplicate-run tracking, canonical separator frames, ordered aggregation with sequence-ordered
slot reservation) is unusually rigorous for a lossy-transport system. The hard-won
thread-pool-starvation lessons are institutionalized in `ChildPipeStream`,
`ChildProcessPipes`, and `InnerLoopParallelism` with the reasoning captured as comments and
ADRs. The main structural debt is duplication between the serial and parallel decode paths in
`DecodeStreamOrchestrator` — the two implementations of the same aggregation semantics have
already drifted once in shape and will drift again — plus a handful of dead code and
unwired-CLI items. Nothing found threatens the protocol contract or the real-FFmpeg
validation bar.

### Findings

**F1. major — Durability aggregation is implemented twice (serial and parallel decode paths).**

- Location: `YTAHD.Core/Core/DecodeStreamOrchestrator.cs` — `ProcessAsync` durability branch
  (the `uniqueDataLengths` walk + `TryDecodeFramePacketsWithHoles` + metrics population) and
  `ProcessParallelAsync` aggregator (the identical `uniqueDataLengths` walk inside the
  `_useDurabilityMatrix` tail).
- Evidence: both blocks independently decode every packet with
  `FramePacketCodec.TryDecodeWithTolerance`, build the same
  `(GroupId, SymbolId) → payloadLength` dictionary, sum recovered lengths, call
  `durabilityCodec.TryDecodeFramePacketsWithHoles(...)`, then populate near-identical
  `DecodeMetrics` field sets and call `VerifyAgainstManifest`.
- Impact: any semantic fix (as CR-20260913-02 was) must be applied twice and can silently
  diverge; the serial path is the deterministic-debug path, so drift there is exactly the
  kind that hides until someone debugs serially.
- Direction: extract a single shared aggregation helper (e.g. an internal
  `DurabilityAggregator` value-object in `YTAHD.Core/Core`) that both paths feed packets into
  and read metrics from. No protocol change.
- Status: planned — `CR-20260913-03-decode-aggregation-unification.md` (Accepted; PLAN
  Workstream G1; BACKLOG G1).

**F2. major — Legacy (non-durability) decode aggregation is also implemented twice.**

- Location: `DecodeStreamOrchestrator.ProcessAsync` legacy loop vs the `Aggregate` closure in
  `ProcessParallelAsync`.
- Evidence: both implement the same state machine — canonical-frame detection via
  `IsCanonicalFrameMemory`, `DuplicateFrameRunTracker.Update/Flush`, `invalidPacketCount`
  accounting, `DecodeRecoveryPolicy.ShouldStopDecoding`, final
  `FlushCurrentRun` + `ResolveExpectedOutputBytes` + `RecoverMissingPayloadFrames` +
  `AssembleOutput`. The parallel variant additionally carries the `stopRequested` latch.
- Impact: same drift risk as F1, on the path every Phase 1–3 decode uses.
- Direction: same shared-aggregator extraction as F1; do both in one refactor step or two
  adjacent steps so the two extractions share test infrastructure.
- Status: planned — `CR-20260913-03-decode-aggregation-unification.md` (Accepted; PLAN
  Workstream G1; BACKLOG G1).

**F3. major — `DecodeThresholds` is dead policy code in a durability-critical area.**

- Location: `YTAHD.Core/Core/DecodeMetrics.cs:81` (`DecodeThresholds.IsSatisfiedBy`).
- Evidence: the only references in the repo are the definition itself and
  `YTAHD.Tests/DecoderMetricsTests.cs`. Neither `DecoderEngine`, `DecodeStreamOrchestrator`,
  nor the CLI ever consults it; decode results are not gated on
  `MaxInvalidPacketRatio`/`MinDuplicateRunQuality`/`MaxRecoveredGroups`.
- Impact: a future reader can reasonably assume invalid-packet ratio is enforced somewhere;
  it is not. Dead enforcement-looking code is worse than no code in this area.
- Direction: either wire it into the CLI decode summary as an explicit advisory verdict, or
  delete it with its test. Decide, don't leave it ambiguous.
- Status: planned (wire as advisory verdict) — `CR-20260913-05-decode-thresholds-verdict.md`
  (Accepted; PLAN Workstream G4; BACKLOG G4).

**F4. major — Unused NuGet dependencies ship native payloads in the core library.**

- Location: `YTAHD.Core/YTAHD.Core.csproj` — `SkiaSharp 2.88.9` (+ Linux native assets) and
  `MathNet.Numerics 4.15.0`.
- Evidence: the only `SkiaSharp` reference in source is the unused `using SkiaSharp;` at
  `YTAHD.Core/Core/EncoderEngine.cs:9` (no `SK*` type is used anywhere); `MathNet` has zero
  references. All rendering is hand-rolled byte manipulation in the modulators.
- Impact: every consumer (CLI, tests, perf, any future host) pulls native Skia binaries for
  nothing; larger publish output, larger attack/audit surface, slower restore.
- Direction: remove both PackageReferences and the dead using. Trivial, zero protocol risk.
- Status: planned — `TD-20260913-01-remove-unused-dependencies.md` (Accepted; PLAN
  Workstream G3; BACKLOG G3).

**F5. major — CLI `--hwaccel` is parsed and advertised but never reaches the decode path.**

- Location: `YTAHD.Cli/Program.cs` — `TryParseHwaccel` (line 41), option registration
  (line 185), and the encode handler sets `HardwareAcceleration = hwaccel` (line 250); the
  decode handler never sets it, and `DecoderEngine.DecodeAsync` builds its ffmpeg arguments
  (`-hide_banner -loglevel error -i ... -f rawvideo -pix_fmt rgb24 ...`) without any
  `-hwaccel` flag. `FFmpegEncoderArguments.HwaccelValue` exists but has no decode-side caller.
- Impact: the CLI help text promises a decode-side acceleration experiment that does
  nothing — a misleading surface on a project that prides itself on honest diagnostics.
- Direction: either wire `HwaccelValue(...)` into the decode argument builder behind
  `DecodeOptions.HardwareAcceleration`, or remove the option until the decode-acceleration
  work actually lands. Prefer wiring it: the enum and mapping already exist and are tested.
- Status: planned (wire it) — `CR-20260913-04-decode-hwaccel-wiring.md` (Accepted; PLAN
  Workstream G2; BACKLOG G2).

**F6. minor — Manifest chunk reassembly relies on a stream-order heuristic.**

- Location: `YTAHD.Core/Core/DurabilityTransportCodec.cs` — `CollectSymbols`, the
  `copyKey = !seenSymbolFrame` logic.
- Evidence: manifest frames seen before any data/parity frame are treated as the "start"
  copy, everything after as the "end" copy. The comment documents the assumption ("Packets
  arrive in stream order"), which holds for the current encoder emission order, but the
  decoder's contract is packet-set semantics everywhere else (symbols are keyed, not
  ordered).
- Impact: if emission order ever changes (e.g. a future encoder emits manifests mid-stream,
  or a reordering proxy reorders frames), the two copies could be merged into one chunk set
  and a conflicting-copy corruption would be silently reconciled instead of failing.
- Direction: make the copy identity explicit in the wire format (e.g. a copy ordinal bit in
  the manifest chunk header) rather than inferred from arrival order. Small protocol
  addition, backward-compatible if the default ordinal is 0.
- Status: planned (deferred to next protocol revision) — tracked in `docs/BACKLOG.md`
  ("Manifest copy identity in the wire format"); deliberately not in the cleanup batch.

**F7. minor — `EncoderEngine.EncodeAsync` loads the whole payload into memory.**

- Location: `YTAHD.Core/Core/EncoderEngine.cs` — `var data = await File.ReadAllBytesAsync(inputFile);`
- Evidence: the durability path then slices it into symbols; the legacy path slices it into
  frame payloads. Nothing streams.
- Impact: encoding a multi-GB archive requires multi-GB RSS on top of the 4K frame buffers.
  The bounded-parallelism work (CR-20260911-06) bounded frame memory but not input memory.
- Direction: stream the input file into the packet producer (`EnumerateFramePackets` already
  yields lazily for the legacy path; the durability path can stream symbol extraction).
  Measure with `YTAHD.Perf` before/after; this is a throughput-neutral memory win.
- Status: open — needs a design pass (manifest needs `dataFrameCount` before emission);
  tracked in `docs/BACKLOG.md` as a candidate; deliberately not bundled with G1.

**F8. minor — `DecoderEngine` duplicates ffprobe path resolution.**

- Location: `YTAHD.Core/Core/DecoderEngine.cs` — `ResolveFfprobeExecutablePath` vs
  `YTAHD.Core/Infrastructure/FFmpegProbe.ResolveFfprobePath`.
- Evidence: two independent implementations of "find ffprobe next to ffmpeg, else PATH",
  with slightly different candidate ordering and fallback behavior.
- Impact: a fix to one (e.g. the CR-20260911-01 directory-override handling) can miss the
  other; the engine's copy also duplicates the `ffmpeg`-literal check.
- Direction: delete `ResolveFfprobeExecutablePath` and call `FFmpegProbe.ResolveFfprobePath`
  (it already handles the explicit-path and PATH cases).
- Status: planned — `TD-20260913-02-review-cleanup-batch.md` (Accepted; PLAN Workstream G7;
  BACKLOG G7).

**F9. minor — `PseudoQamModulator.GetPacketBufferLength` returns `BitsPerFrame`, not bytes.**

- Location: `YTAHD.Core/Modulation/PseudoQamModulator.cs:41` —
  `return Math.Max(geometry.BitsPerFrame, geometry.HeaderBytes);`
- Evidence: every other modulator returns `HeaderBytes + payloadBytesPerFrame` (a byte
  count); Phase 2 returns a bit count when `BitsPerFrame` dominates. Callers allocate
  `packet = new byte[framePacketBytes]` from this value, so Phase 2 allocates 8× more than
  needed and the packet buffer's tail is meaningless.
- Impact: works today because decode writes only `blocksX*blocksY` bytes into the buffer,
  but it is a latent geometry-contract inconsistency: the method's name and sibling
  implementations promise bytes.
- Direction: align to `HeaderBytes + payloadBytesPerFrame` with a regression test asserting
  buffer length parity across all four modulators. Verify against
  `DecoderPacketCompatibilityTests` before merging.
- Status: planned — `CR-20260913-06-packet-buffer-length-contract.md` (Accepted; PLAN
  Workstream G5; BACKLOG G5).

**F10. minor — `MotionTileBasis` keeps a mutable-looking static default table alongside the profile API.**

- Location: `YTAHD.Core/Modulation/MotionTileBasis.cs` — static `OffsetTable`/`Texture`
  built from `MotionTileProfile.Default`, plus per-profile `BuildOffsetTable`/`BuildTexture`
  calls in `GetAxisOffsets(profile)`/`GetTexture(profile)`.
- Evidence: `GetAxisOffsets(profile)` rebuilds the table on every call (it is called per
  frame in `MotionFrameBitDecoder` and per cell-search in `MotionVectorModulator`), while the
  parameterless overloads return cached statics.
- Impact: small but repeated allocation churn on the Phase 4 hot path; also two sources of
  truth for the same constants (`TextureSize`/`CellSize` consts vs `MotionTileProfile`
  derived properties).
- Direction: cache per-profile tables in a `ConcurrentDictionary<MotionTileProfile, ...>`
  (the profile is a small readonly record struct, safe as a key), and make the legacy consts
  delegate to `MotionTileProfile.Default`.
- Status: planned — `TD-20260913-02-review-cleanup-batch.md` (Accepted; PLAN Workstream G7;
  BACKLOG G7).

**F11. minor — `FrameBitDecoderFactory` decoder selection is an if/else type ladder.**

- Location: `YTAHD.Core/Core/FrameBitDecoderFactory.cs:44-63`.
- Evidence: `if (modulator is BinaryGridModulator) ... else if (modulator is PseudoQamModulator) ...`
  — adding a Phase 5 modulator requires editing this ladder or the factory throws
  `NotSupportedException` at decode time (a runtime failure, not a startup one).
- Impact: the failure mode for a forgotten registration is mid-decode, after FFmpeg has
  already run — the most expensive place to discover it.
- Direction: move decoder resolution onto the modulator itself (e.g. an optional
  `IFrameBitDecoderProvider` capability interface implemented by each modulator), keeping the
  factory as the fallback for decorator unwrapping. Fail at modulator construction, not at
  frame 400 of a decode.
- Status: planned — `CR-20260913-07-modulator-owned-decoder-resolution.md` (Accepted; PLAN
  Workstream G6; BACKLOG G6).

**F12. nit — `DecoderEngine`/`EncoderEngine` duplicate `NormalizeModulator`.**

- Location: both engines define identical `private static IModulator NormalizeModulator`.
- Evidence: identical bodies; both special-case `BinaryGridModulator` macroblock
  re-creation.
- Impact: cosmetic duplication; a third engine would copy it again.
- Direction: hoist to a shared internal helper next to `FrameBitDecoderFactory`.
- Status: planned — `TD-20260913-02-review-cleanup-batch.md` (Accepted; PLAN Workstream G7;
  BACKLOG G7).

**F13. nit — `test-output.txt` sits in the repo root.**

- Location: `test-output.txt` (repo root).
- Evidence: captured CLI output committed at root; not referenced by any doc.
- Impact: root clutter; risks going stale and confusing readers.
- Direction: delete it or move under `docs/` if it documents a real session.
- Status: planned (delete) — `TD-20260913-02-review-cleanup-batch.md` (Accepted; PLAN
  Workstream G7; BACKLOG G7).

### Refactoring plan

Ordered so each step is independently shippable (one step = one commit = one ADR), each with
the test that proves it. Steps R1–R3 are the "before next protocol change" set; R4–R6 are
scheduled cleanup.

**R1. Extract shared decode aggregation (fixes F1 + F2).**

- Motivation: the serial and parallel decode paths implement the same aggregation semantics
  twice; CR-20260913-02 had to be applied to both and the next durability fix will too.
- Target design: an internal `DecodeAggregator` in `YTAHD.Core/Core` owning the
  duplicate-run tracker, accumulator, packet list, and metrics; both paths feed it
  per-frame results (already uniform in the parallel path via the `DecodedFrame` record) and
  read final output/metrics from it. The `stopRequested` latch moves inside.
- Migration steps: (1) introduce the aggregator and route the parallel path through it;
  (2) route the serial path through it; (3) delete the now-dead inline logic.
- Test strategy: existing `DecoderStreamOrchestratorTests`, `DecodeSlotOrderingTests`,
  `DurabilityMatrixTests`, `IntegrityEndToEndTests` must stay green unchanged (they are the
  equivalence proof); add one serial-vs-parallel byte-identical fake-wrapper test per
  modulator if not already covered by `ParallelPipelineRealCodecTests` (it is, for real
  codecs).
- Rollback: revert the commit; no protocol or API change.
- ADR: `CR-20260913-03-decode-aggregation-unification.md` (check next ordinal at creation).

**R2. Wire or remove `--hwaccel` on decode (fixes F5).**

- Motivation: advertised-but-inert CLI surface contradicts the project's honest-diagnostics
  bar.
- Target design: `DecodeOptions.HardwareAcceleration` flows to `DecoderEngine.DecodeAsync`,
  which prepends `-hwaccel <value>` from `FFmpegEncoderArguments.HwaccelValue` before `-i`.
  Default `none` emits nothing — byte-identical arguments today.
- Migration steps: single step; argument builder change + option threading.
- Test strategy: extend the fake-wrapper argument assertions (pattern from
  `FFmpegEncoderArgumentsTests`) for `none` (no flag), `qsv`, `cuda`; one real-FFmpeg decode
  smoke test with `--hwaccel qsv` guarded by capability probe (pattern from
  `QsvRealCodecTests`).
- Rollback: revert; default path unchanged.
- ADR: `CR-20260913-04-decode-hwaccel-wiring.md`.

**R3. Remove unused dependencies (fixes F4).**

- Motivation: SkiaSharp ships native binaries nothing uses; MathNet is fully unused.
- Target design: `YTAHD.Core.csproj` references only what compiles.
- Migration steps: delete both PackageReferences + the dead `using SkiaSharp;`; rebuild.
- Test strategy: full build + full test suite green.
- Rollback: trivial revert.
- ADR: `TD-20260913-01-remove-unused-dependencies.md`.

**R4. Resolve `DecodeThresholds` (fixes F3).**

- Decide: wire as an advisory verdict in the CLI decode summary (recommended — the metrics
  already exist and the thresholds encode real operational experience) or delete with its
  test. Either is one small commit.
- ADR: `CR-20260913-05-decode-thresholds-verdict.md` (or a plain deletion note in the commit
  if deleted).

**R5. Align `PseudoQamModulator.GetPacketBufferLength` to bytes (fixes F9).**

- One-line change + cross-modulator buffer-length parity test. Verify
  `DecoderPacketCompatibilityTests` and `BinaryGridModulatorTests` stay green.
- ADR: `CR-20260913-06-packet-buffer-length-contract.md`.

**R6. Small cleanups batch (fixes F8, F10, F11, F12, F13).**

- Individually shippable micro-commits; F11 (decoder resolution) is the only one with design
  content and deserves its own ADR; the rest can share one `TD-` ADR.
- F6 (manifest copy ordinal) is deliberately **not** in this batch: it is a wire-format
  change and should ride with the next protocol revision, not a cleanup commit.

### Risks & unknowns

- **R1 equivalence at scale**: the serial/parallel equivalence is proven by
  `ParallelPipelineRealCodecTests` for real codecs, but only at the payload sizes in that
  matrix. A `YTAHD.Perf bench` run before/after R1 on a large payload (≥ 1 MB, durability
  on) would close the gap between "equivalent in tests" and "equivalent in practice".
- **R2 real-device behavior**: `--hwaccel qsv` decode was never exercised on this machine's
  driver stack; the capability probe pattern exists but decode-side probing
  (`-hwaccels` output) is not implemented. Budget for a small probe extension.
- **F7 streaming encode**: symbol extraction from a stream changes the durability encoder's
  dataFrameCount computation order (manifest needs the count before emission). Feasible
  (count = ceil(length / SymbolSize) from file length) but needs a design pass; do not
  bundle with R1.
- **Test-suite runtime**: the real-codec matrix is the slowest part of the suite; any
  refactor touching decode paths should run the full suite, not just fake-wrapper tests.
  Budget ~10–15 min per validation run based on prior ADR records.

### Explicit non-goals (deliberately not flagged)

- **The 3× physical repeat / canonical separator emission pattern** — protocol contract,
  validated by real-codec tests; not a refactor target.
- **`ParallelismPolicy.AutoWorkerCap = 4`** — measured decision (CR-20260912-02); no new
  evidence to relitigate.
- **`System.CommandLine` beta dependency** — known-beta, but replacing it is churn without a
  triggering problem; revisit only if the CLI surface grows significantly.
- **`DebugTrace` console-based logging** — adequate for the project's scale; a logging
  framework would add dependency weight for no current need.
- **Phase 2 capacity/robustness** (16-PAM levels under real codecs) — validated baseline per
  README/ADRs; capacity experiments belong to the backlog's Phase 4 research item, not this
  review.
- **`probe/` project** — does its one job (smoke test); no findings.

### Verdict

No critical findings. The five majors are maintainability debt (duplication, dead code,
unwired surface, unused deps), not correctness or protocol risks. R1–R3 are recommended
before the next protocol change (the Phase 4 follow-up research in `docs/BACKLOG.md` is the
natural next protocol work); R4–R6 can follow at leisure.

---
