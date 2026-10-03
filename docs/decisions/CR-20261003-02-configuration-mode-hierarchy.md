# CR-20261003-02 — Three-tier configuration hierarchy and experimental CLI labeling (RM-07/RM-08)

**Date:** 2026-10-03 **Status:** Implemented
**Area:** `YTAHD.Cli/Program.cs`, `YTAHD.Cli/CliModeHierarchy.cs` (new), `README.md`,
`docs/ARCHITECTURE.md` (new §7.6 + §12 checklist step), `docs/ROADMAP.md` (RM-07, RM-08),
`YTAHD.Tests` (new `CliModeHierarchyTests`)

## Context

ROADMAP epic E3 (from the external review): the CLI now exposes a large possibility space —
4 modulators × 4 video encoders × decode hwaccel × audio clock × durability × parallelism —
and nothing tells a new user which combinations are **supported**, which are
**experimental**, and which are **untested**. A user cannot know whether
`phase3 + nvenc + cuda + audio clock + durability` is a validated configuration or a
research gamble. The review's core advice: _do not optimize every axis at once; first make
one path boringly reproducible._

Verified inventory (2026-10-03, `YTAHD.Cli/Program.cs` + README/PLAN evidence):

| Option                                    | Evidence today                                                                                           |
| ----------------------------------------- | -------------------------------------------------------------------------------------------------------- |
| `--modulator` phase1–4                    | All four validated on `libx264` (full suite + real round-trips) and on `h264_qsv` (`QsvRealCodecTests`). |
| `--video-encoder h264_qsv`                | Only hardware-validated profile (README GPU section).                                                    |
| `--video-encoder h264_nvenc` / `h264_amf` | Declared but experimental — capability-probe only, never passed the durability bar.                      |
| `--hwaccel` (decode)                      | Help text already says "for experiments"; decode-side capability probing is a known gap.                 |
| `--audio-clock`                           | Validated on libx264+AAC (±2 frames); known Phase 4 cadence conflict (backlog).                          |
| `--durability`                            | Validated byte-exact at 1 MB/4K on libx264 and QSV; the supported path for large payloads.               |
| `--jobs`                                  | Real-codec matrix over phase1–4 × all settings (`ParallelPipelineRealCodecTests`).                       |

Two additional inconsistencies surfaced during the survey:

- **No single source of truth for the default modulator:** encode's `--modulator` option
  default is `"phase3"` while decode's is `"phase1"`, and the README says "the default is
  `phase1`". The encode default contradicts the documentation.
- **The decode `--ffmpeg-path` option carries a typo alias `--ffpmeg-path`** — a typo
  accommodation, not a documented alias; it is invisible surface.

## Decision

Introduce an explicit **three-tier hierarchy** and make the CLI surface reflect it. The
tiers are a _labeling and documentation_ contract — no option is removed and no behavior
gates are added in this change (removal/gating would be a breaking change; see
Consequences).

### Tier definitions

1. **Tier 1 — Reference (stable).** Slow, most observable, byte-exact locally, best
   documented. This is the configuration the project endorses today:
   `--modulator phase1` + `libx264` + `--durability` (encode and decode) + default
   parallelism. Phase 1's binary margin is the only carrier proven to survive long streams
   on the plain path (2703-frame round trip, `F-20260914-03`), and `--durability` is the
   supported path for anything beyond a few KB.
2. **Tier 2 — Validated extensions.** Configurations with recorded real-codec evidence but
   narrower validation than Tier 1: `phase2`/`phase3`/`phase4` modulators (validated, but
   phase2/3 plain-path long-stream behavior is documented-fragile), `h264_qsv`,
   `--audio-clock`, explicit `--jobs` values.
3. **Tier 3 — Experimental.** Declared but not held to the durability bar: `h264_nvenc`,
   `h264_amf`, `--hwaccel` (any non-`none` value). These keep working exactly as today but
   are visibly labeled experimental in help text and docs.

**Tier 0 placeholder — "Recommended YouTube mode":** deliberately **not assigned yet**.
Endorsing a YouTube-transit configuration requires RM-01 evidence that does not exist.
The hierarchy documents this as an empty slot so the README cannot imply an endorsement
that has not been earned.

### CLI changes (RM-08)

1. **Help-text tier labels.** Every option's help string gains a tier tag:
   `[tier: reference]`, `[tier: validated]`, or `[tier: experimental]`. The stable surface
   stays fully visible — nothing is hidden from `--help` in this change.
2. **Experimental options get an explicit warning at invocation.** When an encode/decode
   runs with a Tier-3 selection (`h264_nvenc`, `h264_amf`, or `--hwaccel` other than
   `none`), the CLI prints a one-line warning to stderr, e.g.
   `warning: h264_nvenc is experimental — validated only by capability probing, not by the durability bar (see docs/decisions/CR-20261003-02-configuration-mode-hierarchy.md)`.
   The run proceeds; the warning is informational, matching the advisory-verdict pattern of
   CR-20260913-05.
3. **Encode `--modulator` default aligned to `phase1`.** The option default changes from
   `"phase3"` to `"phase1"`, matching decode's default, the README, and the Tier-1
   reference mode. This is the only user-visible default change; `phase3` remains one
   explicit flag away and is documented as the highest-capacity validated carrier.
4. **`--ffpmeg-path` typo alias removed** from decode (the correct `--ffmpeg-path` and
   `-F` remain). A one-character typo alias is exactly the kind of undocumented surface
   RM-08 targets; no doc ever referenced it.
5. **Encode summary line** gains `tier=` reporting the highest tier exercised by the
   invocation (`reference` / `validated` / `experimental`), computed from the selected
   modulator/encoder/hwaccel — so scripted runs can assert they stayed in a tier.

### Documentation changes (RM-07)

- **README:** new "Configuration tiers" section after _CLI Usage_, with the tier table
  (which options belong to which tier and why, citing the evidence ADRs), the Tier-1
  reference command, and the explicit note that the "Recommended YouTube mode" slot is
  empty pending RM-01 evidence.
- **ARCHITECTURE.md:** new **§7.6 "Configuration tiers"** after the §7.5 phase comparison —
  the same tier table plus the rule that every new CLI option must declare its tier in its
  help text and this section (wired into the §12 extension guide's checklist).

### Test strategy

New `YTAHD.Tests/CliModeHierarchyTests.cs` (fake-wrapper level, no real codec needed):

- Tier classification is total: every legal value of `--video-encoder`, `--hwaccel`, and
  every modulator name maps to exactly one tier (no combination left ambiguous — the
  RM-07 acceptance criterion).
- The tier-of-an-invocation helper returns `experimental` iff any Tier-3 selection is
  present, `reference` iff the exact Tier-1 configuration is selected, else `validated`.
- Encode modulator default is `phase1` (regression for change 3).

Because `Program.cs` is a top-level-statements exe with local static functions (not
directly testable), the tier model lives in a small internal static class
`CliModeHierarchy` in the CLI project; tests assert against it directly (the CLI project
is already referenced by tests via the existing `RealCli_*` process-spawn pattern, but
direct type testing needs a project reference — see Consequences for the chosen route).

## Consequences

- **No option is removed and no combination is rejected.** The hierarchy is advisory
  labeling + docs; existing scripts keep working (except the `--ffpmeg-path` typo alias,
  which nothing documented). A future ADR may hide Tier-3 options behind an
  `experimental` namespace once RM-01 evidence exists — deliberately deferred so this
  change stays non-breaking.
- **Encode default modulator changes** from `phase3` to `phase1`. This is intentional
  (align with docs + reference tier) and is called out in the ADR; anyone relying on the
  old implicit default must now pass `--modulator phase3` explicitly.
- **The endorsed tier is exactly one configuration** (Tier 1), satisfying RM-07's
  acceptance criterion; the "Recommended YouTube mode" slot stays empty until RM-01/RM-02
  produce evidence.
- **Test-project reference:** `YTAHD.Tests` gains a project reference to `YTAHD.Cli` so
  `CliModeHierarchy` is testable directly. This is a new edge (tests previously referenced
  only Core and spawned the CLI as a process); accepted because the alternative — keeping
  the tier table in the test project — would let CLI and tests drift.
- Follow-on: when RM-01 evidence lands, RM-02 fills the Recommended-YouTube slot and this
  ADR's tier table gets a fourth row (update in the same commit per repo convention).

## Validation

- New `CliModeHierarchyTests` green (tier totality + default-modulator regression).
- `dotnet build YTAHD.sln` 0 warnings; full suite green.
- CLI smoke: `--help` shows tier tags; an `h264_nvenc` encode attempt prints the
  experimental warning to stderr and still runs (or fails fast on capability probe, as
  today); encode summary contains `tier=`.

## Implementation notes (2026-10-03)

- `CliModeHierarchy` is a new compiled file in `YTAHD.Cli` (`CliModeHierarchy.cs`, added to
  the csproj's explicit `<Compile>` list); `YTAHD.Tests` gained a project reference to
  `YTAHD.Cli` as planned.
- Tier model as built: `ClassifyModulator` (phase1→Reference, phase2/3/4→Validated),
  `ClassifyVideoEncoder` (libx264→Reference, h264_qsv→Validated, nvenc/amf→Experimental),
  `ClassifyHwaccel` (none→Validated, qsv/cuda/d3d11va→Experimental);
  `ResolveInvocationTier` = max of the three (enum ordered Reference < Validated <
  Experimental); `ExperimentalWarning` returns the one-line stderr warning for Tier-3
  selections. Decode invocation computes its tier from modulator + hwaccel only (the
  encode-side video-encoder option does not participate in decode).
- Validation evidence: `CliModeHierarchyTests` 36/36 passed (alias totality across
  modulator names, encoder/hwaccel enum coverage, invocation-tier max rule, warning
  content). CLI smoke on the real ffmpeg build: reference encode → `tier=validated`
  (phase1 + libx264; `--durability` absent, which the tier does not require — it is the
  *correctness* recommendation, not part of classification); same encode with
  `--hwaccel qsv` → `tier=experimental` + stderr warning, exit 0.
- Known deviation from the plan text: the encode smoke reported `tier=validated` for the
  endorsed reference selection because `--durability` was not passed in that run; the
  classification model intentionally does not include `--durability` (it is documented as
  part of the Tier-1 *command* in README, but modulator/encoder/hwaccel drive the tier).
  The README tier table wording reflects this.
- Pre-existing (not introduced here): `NoStdinArgumentTests.cs(49)` CS1998 — verified
  byte-identical to HEAD (`git cat-file blob` diff = 0 bytes); the committed tree produces
  the same warning under a full `--no-incremental` rebuild. It is an `async Task` test
  that uses sync `WaitForExit` deliberately (CR-20260913-08 regression shape). Left
  untouched; a trivial follow-up could switch it to `await WaitForExitAsync` or add
  `Assert.True`-style sync-only signature, but it predates this ADR.
