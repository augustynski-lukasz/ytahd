# YTAHD — Roadmap

Converted from the external review in `docs/SUGGESTIONS.md` (2026-09-16) into actionable
stories. This file is the planning view; `docs/BACKLOG.md` remains the open-items ledger —
when a story here starts, it gets a BACKLOG entry; when it lands, it gets an ADR and the
BACKLOG item is deleted.

Conventions:

- Story IDs: `RM-NN`. Each story names its epic, priority, and acceptance criteria.
- Priorities: **P1** = high impact now, **P2** = good next, **P3** = guardrail / principle.
- Status: `open` / `in-progress` / `done (ADR-id)`. Update in the same commit as the work.
- Code-level refactoring debt is **not** duplicated here — see `docs/REVIEW.md` (Review 1)
  and PLAN Workstream G for that track.

## Epic index

| Epic                          | Stories      | Theme                                    |
| ----------------------------- | ------------ | ---------------------------------------- |
| E1 — YouTube transit evidence | RM-01…RM-04  | Prove the core claim end-to-end          |
| E2 — Product framing          | RM-05, RM-06 | Set honest expectations                  |
| E3 — Mode hierarchy           | RM-07, RM-08 | Make the possibility space navigable     |
| E4 — Durability terminology   | RM-09        | Precise claims about recovery guarantees |
| E5 — Memory-bounded recovery  | RM-10        | Scale beyond 1 MB experiments            |
| E6 — Compatibility & tooling  | RM-11…RM-15  | Protect the wire-format asset            |
| E7 — Guardrails               | RM-16…RM-19  | What NOT to do                           |

---

## E1 — YouTube transit evidence (P1)

The central promise is not "encode/decode a locally generated FFmpeg file" — it is:
_encode → upload → YouTube processing → retrieve → decode, repeatably_. Nothing in the
repo currently validates that last mile.

### RM-01 — YouTube transit validation harness (P1, in-progress — harness scaffolded CR-20261007-03)

**Story:** As a maintainer, I want a canonical, repeatable YouTube transit validation
harness so that the core claim is backed by published evidence instead of local round-trips.

- Build a script/checklist that drives: encode → upload → wait for processing → retrieve
  (`yt-dlp` / browser download) → decode → SHA-256 verify.
- Store each run as a versioned experiment record under `docs/experiments/` (create the
  directory): date, video URL, rendition actually served, modulator, payload size,
  durability on/off, decode result, hardware.
- Vary deliberately, one axis at a time: upload source codec (H.264/HEVC/AV1), YouTube
  rendition (VP9 vs AV1, resolution, frame rate), retrieval method, modulator (Phase 1–4),
  payload size (tiny diagnostic → 1 MB → tens of MB), error model (missing frames,
  reordering, trimming, forced resolution changes).
- **Acceptance:** at least one published experiment record per modulator with a defined
  success criterion (byte-exact SHA-256 recovery, or documented failure mode).

### RM-02 — One recommended end-to-end command in the README (P1, open)

**Story:** As a new user, I want a single README command chain (encode → upload
assumptions → retrieve → decode → verify SHA-256) so that I can reproduce the supported
path without guessing options.

- One configuration only — the one actually endorsed by RM-01 evidence.
- State the upload/retrieval assumptions inline (rendition, `yt-dlp` invocation).
- **Acceptance:** a reader can copy-paste the chain and know exactly what is and is not
  guaranteed.

### RM-03 — Promote the long-stream plain-path caveat (P1, done — CR-20261007-01)

**Story:** As a user, I want the plain-path limitation and the role/cost of `--durability`
stated up front (README Quick Start + CLI output) so that I don't discover it via my first
failed recovery.

- Source material already exists: BACKLOG "Plain decode path is not guaranteed byte-exact
  for long streams" and ADR `F-20260914-03-long-stream-regression-coverage.md`
  (1 MB phase2 throws; 256 KB phase2 **silently truncates** with exit 0).
- README: elevate from the current "Reliability" section into Quick Start.
- CLI: implement the open follow-up from the same BACKLOG item — compare `payloadRecovered`
  against the input file size at decode time and **exit non-zero on mismatch**, turning
  silent truncation into a loud failure even on the plain path.
- **Acceptance:** README caveat visible in Quick Start; CLI exits non-zero on truncated
  plain-path recovery with a regression test.

### RM-04 — Published results table (P1, open)

**Story:** As an evaluator, I want a results table (effective payload throughput, encoded
video expansion, decode success rate, upload/transcode conditions, hardware) so that I can
assess the project on numbers, not prose.

- Feeds off RM-01 experiment records; one row per validated configuration.
- Reuse `YTAHD.Perf` JSON output (pattern: `perf-g1-baseline.json` / `perf-g1-after.json`)
  as the machine-readable source.
- **Acceptance:** table exists in `docs/experiments/` (or linked from README) with at least
  the configurations endorsed in RM-02.

---

## E2 — Product framing (P1)

Status: **done (2026-10-03)** — RM-05 + RM-06 implemented by
`docs/decisions/CR-20261003-01-product-framing-repositioning.md`: README positioning block

- research-status caveat + "What this is NOT for" list; README "System Architecture
  Principles" no longer claims guaranteed 100% integrity; DESCRIPTION.md reframed. GitHub
  repo "About" text needs a manual owner update (suggested text: DESCRIPTION.md first
  paragraph).

### RM-05 — Reframe "hard drive" claims (P1, done — CR-20261003-01)

**Story:** As a maintainer, I want the project framed as a research transport experiment so
that the title's durability/privacy/cost expectations don't undermine credibility.

- Adopt framing along the lines of: _"A research-grade, self-verifying binary-over-video
  transport/archive experiment designed for lossy video-transcoding platforms."_
- Keep the memorable name, qualify the claim.

### RM-06 — Explicit unsuitability list (P1, done — CR-20261003-01)

**Story:** As a user, I want an explicit "do not use for" list so that I don't entrust the
wrong data to an experimental channel.

- README must state it is **not** suitable for: confidential data; the only copy of
  valuable data; data requiring guaranteed retention/retrieval; content that could violate
  platform rules or be removed; storage with predictable effective cost or retrieval
  latency.
- **Acceptance (RM-05 + RM-06):** README carries the reframed positioning and the
  unsuitability list; no doc claims "storage" guarantees unqualified.

---

## E3 — Mode hierarchy (P2)

Status: **done (2026-10-03)** — RM-07 + RM-08 implemented by
`docs/decisions/CR-20261003-02-configuration-mode-hierarchy.md`: `YTAHD.Cli/CliModeHierarchy.cs`
tier model (Reference / Validated / Experimental), `[tier: …]` labels on every CLI option,
`tier=` field in encode/decode summaries, stderr warnings for Tier-3 selections (nvenc/amf/
--hwaccel), encode `--modulator` default aligned to `phase1` (was phase3, contradicted docs),
`--ffpmeg-path` typo alias removed, README "Configuration tiers" section + ARCHITECTURE §7.6

- §12 checklist step, `CliModeHierarchyTests` (36 tests). The "Recommended YouTube mode" slot
  is deliberately **empty** pending RM-01 evidence. NOTE: the `--ffpmeg-path` alias removal is
  the only breaking change; ADR default-change (encode `--modulator` phase3→phase1) called out.

### RM-07 — Define the three-tier mode hierarchy (P2, done — CR-20261003-02)

**Story:** As a new user, I want an explicit hierarchy of supported configurations so that
I know whether `phase3 + AV1 + CUDA + audio clock + durability` is supported, experimental,
or untested.

1. **Reference mode** — slow, most observable, byte-exact locally, best documented.
2. **Recommended YouTube mode** — one configuration endorsed by RM-01 evidence.
3. **Experimental modes** — Phase 2–4 variants, GPU combinations, audio-clock variants,
   durability choices.

- Document the hierarchy in README/ARCHITECTURE; every CLI option maps to a tier.
- **Acceptance:** no combination is left ambiguous; the endorsed tier is exactly one
  configuration.

### RM-08 — Namespace experimental switches (P2, done — CR-20261003-02)

**Story:** As a maintainer, I want research switches separated from the stable CLI surface
so that the stable surface stays small and honest.

- Move research-only options under an `experimental` namespace (or config file), or hide
  them from default help output.
- **Acceptance:** default `--help` shows only stable-tier options; experimental options are
  visibly labeled.

---

## E4 — Durability terminology (P2)

Status: **done (2026-10-06)** — RM-09 implemented by
`docs/decisions/CR-20261006-01-durability-guarantee-claims.md`: the placeholder
`DurabilityRedundancyMode` enum (Repeat/XorParity/Fountain) and the write-nowhere/read-nowhere
`DurabilityMatrixOptions.RedundancyMode` property were **removed** (nothing in-repo or CLI
ever set them; no wire-format impact); XOR parity's actual guarantee (1 erasure per group,
manifest SHA-256 proves correctness) documented in README + ARCHITECTURE §8.2 "Guarantee
contract"; §12.3 reworded (new schemes land with implementation + tests + ADR, no mode
selector). Regression test `Options_Expose_Only_The_Implemented_XorParity_Contract`.

### RM-09 — Make recovery-guarantee claims precise (P2, done — CR-20261006-01)

**Story:** As a user, I want documentation to distinguish implemented-and-proven vs
implemented-but-experimental vs placeholder vs planned so that I don't infer guarantees the
transport cannot provide.

- Verified code fact (2026-09-16): `DurabilityRedundancyMode.Fountain`
  (`YTAHD.Core/Core/DurabilityRedundancyMode.cs`) exists but is **never selected anywhere**
  — `DurabilityMatrixOptions.RedundancyMode` defaults to `XorParity`, no code path or CLI
  option ever sets `Fountain`, and XOR parity is not a fountain code or general erasure
  coding.
- Decide per the Review-1 pattern: either remove `Fountain` from the enum until a real
  implementation exists, or keep it and make any selection fail explicitly with a useful
  explanation + `experimental` label. Do not leave it silently selectable-looking.
- Document XOR parity's actual guarantee (1 parity symbol per group ⇒ tolerates 1 erasure
  per group; see `DurabilityMatrixOptions.ComputeRecoveryThreshold`).
- **Acceptance:** docs state exactly what is proven; `Fountain` is either gone or
  explicitly non-functional; ADR records the decision.

---

## E5 — Memory-bounded recovery (P1)

### RM-10 — Streaming, memory-bounded decode (P1, done — CR-20261007-04)

**Story:** As a user archiving large payloads, I want decode memory to be bounded
independent of payload size so that archival-scale recovery is feasible at all.

- Design was decided in ADR `F-20260914-02-streaming-decode-output.md` (Status: Accepted);
  implementation ADR `CR-20261007-04-streaming-decode-output-implementation.md`
  (Status: Implemented) records what landed.
- Landed: `StreamingDurabilityDecoder` (per-group incremental recovery, contiguous-prefix
  flush to a consumer sink, streaming SHA-256), `DecodeStreamOrchestrator.ProcessToSinkAsync`,
  and an opt-in `DecodeOptions.StreamingOutput` flag wired through `DecoderEngine` for the
  durability decode path.
- Follow-up (not scheduled): a `YTAHD.Perf` peak-RSS measurement on a ≥ 100 MB payload to
  formally verify the acceptance clause; parallel orchestrator path stays batch.
- **Acceptance:** hole/integrity semantics match the batch hole-tolerant decode
  (CR-20260913-02); decode memory is O(one parity group) by construction. (RSS measurement
  follow-up remains open.)

---

## E6 — Compatibility & tooling (P2)

### RM-11 — Golden-vector fixtures per wire-format version (P2, done — CR-20261007-02)

**Story:** As a maintainer, I want small known payload → encoded frame/packet → expected
decoded bytes fixtures so that wire-format regressions are caught byte-exactly.

- One fixture set per protocol version (v1/v2 packet formats, manifest chunking).
- **Acceptance:** fixtures committed; a test decodes them without running the encoder.

### RM-12 — Cross-release compatibility tests (P2, done — CR-20261008-01)

**Story:** As a maintainer, I want tests that decode fixtures produced by prior tagged
releases so that compatibility is proven against history, not just the current pair.

- Fixtures generated by each tag's own encoder (`scripts/generate-release-fixtures.ps1`)
  and committed under `YTAHD.Tests/ReleaseFixtures/`; covers v0.1.1 (v1 packets),
  v0.1.3 (v2, no manifest), v0.2.0 (v2 + manifest, incl. chunked).
- **Acceptance:** at least the previous tagged release's fixtures decode correctly
  (exceeded: all three eras decode byte-exactly, incl. a v1 hole-tolerance case).

### RM-13 — Machine-readable `ytahd inspect` (P2, done — CR-20261007-02)

**Story:** As a user/tooling author, I want an `inspect` command emitting JSON (protocol
version, modulator, frame geometry, payload size, integrity state, decoder confidence,
loss/recovery statistics, manifest hash) so that streams can be triaged programmatically.

- **Acceptance:** JSON schema documented; CLI emits it for any YTAHD-encoded video.

### RM-14 — Containerized reproducible FFmpeg test environment (P3, done — CR-20261009-02)

**Story:** As a contributor, I want a container with pinned FFmpeg for FFmpeg-dependent
scenarios so that results are reproducible across machines.

- Keep native performance tests separate (container timing is not comparable).
- **Acceptance:** documented container run of the real-codec test subset.
- Landed: `Dockerfile.test` (`.NET 8 SDK` + FFmpeg 5.1.9-0+deb12u1 pinned with
  `apt-mark hold`, native deps matching CI), `.dockerignore` (source-only image inputs;
  perf artifacts stay host-side). Full suite via `docker run --rm -t ytahd-test`;
  per-class via `--filter`. GPU/hwaccel tests self-skip by their existing encoder probes;
  `YTAHD.Perf` stays native per the story. CI wiring deliberately deferred (in the ADR).
- **Verified end-to-end 2026-10-10 (Docker Engine 29.9.0, WSL Ubuntu 24.04): full suite
  390/390 in the container** with FFmpeg 5.1.9. The first real build surfaced and fixed
  two Windows-only-invisible defects: a `.dockerignore` root-anchored pattern leak
  (nested `obj/` shipped Windows `project.assets.json` → MSB4018 NuGet fallback-folder
  error) and a C# 13-only `Span` local in `Phase4DurabilityDiscriminatorTests` that the
  pinned .NET 8 SDK (C# 12) rejects (CS8652). Both detailed in the ADR's debugging
  sections.

### RM-15 — Benchmark regression guardrails (P3, done — CR-20261009-01)

**Story:** As a maintainer, I want CI guardrails on benchmark results that detect material
regressions without fragile fixed-time assertions.

- Build on the existing `YTAHD.Perf --json` output format.
- **Acceptance:** a defined threshold policy (e.g. % regression vs stored baseline) and a
  check that can run locally/CI.
- Landed: `scripts/check-perf-regression.ps1` — compares a fresh `YTAHD.Perf bench
--json` result against a stored baseline; fails on >20% serial / >35% parallel
  throughput loss per run key (modulator+payload+jobs, repeats summed) or >10% aggregate
  mean loss; rejects cross-environment comparisons (core-count mismatch) to force
  re-baselining. Thresholds calibrated on the measured G1 noise floor. Local/CI-runnable
  via exit codes; CI wiring itself is deferred (shared runners aren't throughput-
  comparable to committed baselines — recorded in the ADR).

---

## E7 — Guardrails (P3 — standing principles, not tasks)

### RM-16 — No new modulation scheme without an evidence-based niche (P3, standing)

Phase 1–4 already cover distinct robustness/capacity regions (3,997 / 30,863 / 123,611 /
4,835 bytes/frame at 4K). Do not add Phase 5 until RM-01 evidence shows a gap the existing
carriers cannot fill.

### RM-17 — GPU speed ≠ end-to-end usefulness (P3, standing)

A GPU profile is valuable only if it preserves the carrier after target transcoding and
improves total wall-clock throughput. Evaluate GPU options through the RM-01/RM-04
evidence path, not encode benchmarks alone.

### RM-18 — Keep the stable CLI surface small (P3, standing)

Every explored idea does not become a permanent public option (see RM-08). Research
switches live under `experimental` or config files.

### RM-19 — Protocol cleverness must not cost independent implementability (P3, standing)

The byte-exact specification in `docs/ARCHITECTURE.md` (validated: 71 math expressions,
10 Mermaid diagrams, commit 00fb97c) is a major asset. Any wire-format change (e.g. the
manifest copy-ordinal item in BACKLOG) must update the spec and ship golden vectors
(RM-11) in the same change.

---

## Sequencing summary

1. **Now (P1):** RM-03 (caveat + CLI exit code — smallest, unblocks trust), RM-05/06
   (framing — docs only), RM-01/02/04 (evidence harness — the core-claim track).
2. **Next (P2):** RM-07/08 (mode hierarchy), RM-09 (Fountain decision), RM-11–13
   (compatibility/tooling).
3. **Ongoing (P3):** RM-16–19 (principles).

Code-level refactoring (Workstream G, Review 1) runs on its own track per
`docs/REVIEW.md`; wire-format work (manifest copy ordinal) should ride with the next
protocol revision and pick up RM-11/RM-19 obligations when it does.
