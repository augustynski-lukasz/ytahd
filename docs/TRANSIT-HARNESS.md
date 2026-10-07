# YTAHD — YouTube Transit Validation Harness (RM-01)

Purpose: turn the core claim — _encode → upload → YouTube processing → retrieve → decode,
repeatably_ — into published, versioned evidence. Nothing in the repo validates the last
mile until a record in `docs/experiments/` says so.

## The loop

```
1. encode    payload.bin -> transit.mp4        (CLI encode, options recorded below)
2. upload    transit.mp4 -> YouTube            (unlisted; record the URL)
3. wait      until YouTube finishes processing (record the wait time)
4. retrieve  yt-dlp / browser download         (record the rendition actually served)
5. decode    retrieved.mp4 -> recovered.bin    (CLI decode, same --modulator)
6. verify    SHA-256(recovered.bin) == SHA-256(payload.bin)
```

## How to run a session

1. **Prepare the payload.** Use a deterministic payload so runs are comparable:
   ```powershell
   $bytes = [byte[]](1..262144 | ForEach-Object { ($_ * 11 + 3) % 251 })
   [IO.File]::WriteAllBytes("$env:TEMP\ytahd-transit-payload.bin", $bytes)
   ```
   Sizes to cover, one axis at a time: tiny diagnostic (256 B) → 64 KB → 256 KB → 1 MB →
   tens of MB.

2. **Encode** (record every option in the record):
   ```powershell
   dotnet run --project YTAHD.Cli -- encode payload.bin transit.mp4 `
     --modulator phase1 --durability --width 3840 --height 2160 --fps 60
   ```
   Note the `tier=` field of the encode summary in the record.

3. **Upload** to YouTube as **unlisted**. Record: upload timestamp, source codec/container
   (the CLI emits H.264/MP4), and the video URL.

4. **Wait for processing.** Record when YouTube reports processing complete, and which
   renditions are offered (yt-dlp `-F` lists them).

5. **Retrieve.** Prefer `yt-dlp` for reproducibility:
   ```powershell
   yt-dlp -F "<url>"                       # list renditions; record what is offered
   yt-dlp -f <format-id> -o retrieved.mp4 "<url>"
   ```
   Record the format id and the rendition actually served (codec, resolution, fps).
   Also try a plain browser download at least once per modulator — the served rendition
   can differ.

6. **Decode**:
   ```powershell
   dotnet run --project YTAHD.Cli -- decode retrieved.mp4 recovered.bin `
     --modulator phase1 --durability --expected-bytes 262144
   ```
   The exit code is authoritative (RM-03): 0 means trustworthy recovery.

7. **Verify**:
   ```powershell
   (Get-FileHash recovered.bin -Algorithm SHA256).Hash
   (Get-FileHash payload.bin  -Algorithm SHA256).Hash
   ```

8. **Record the run** as `docs/experiments/E-YYYYMMDD-NN-<slug>.md` using the template
   below. Commit the record even when the run FAILED — a documented failure mode is
   evidence too (that is the acceptance criterion).

## Experiment record template

```markdown
# E-YYYYMMDD-NN — <short title>

**Date:** YYYY-MM-DD
**Modulator:** phase1 | phase2 | phase3 | phase4
**Payload:** <size> bytes, deterministic (byte[i] = (i*11+3) % 251)
**Durability:** on | off
**Encode options:** --width N --height N --fps N [--video-encoder …] [--audio-clock]
**Encode tier:** <tier= field>
**Video URL:** <url> (unlisted)
**Upload → processing-complete:** <duration>
**Retrieval:** yt-dlp -f <id> | browser download
**Rendition served:** <codec, resolution, fps> (from yt-dlp -F)
**Decode options:** --modulator <same> [--durability] --expected-bytes <size>
**Decode exit code:** <n>
**Decode summary:** <paste the Decode summary line>
**SHA-256 match:** yes | no
**Result:** PASS | FAIL | PARTIAL
**Failure mode (if any):** <missing frames / reordering / trimming / resolution change /
transcode artifacts — describe what the decode metrics showed>
**Hardware:** <CPU/GPU, ffmpeg build>
**Notes:** <one-axis-at-a-time: what varied vs the previous record>
```

## Axis plan (vary ONE axis per run)

| Axis | Values to cover |
| --- | --- |
| Modulator | phase1 → phase2 → phase3 → phase4 |
| Payload size | 256 B → 64 KB → 256 KB → 1 MB → 10 MB |
| Durability | off → on (on is expected for ≥ 64 KB) |
| Upload source codec | H.264 (CLI default) → HEVC/AV1 re-wrap experiments |
| Rendition | VP9 vs AV1, 1080p vs 4K, 30 vs 60 fps |
| Retrieval | yt-dlp best → yt-dlp specific format → browser download |
| Error model | head/tail trim, forced resolution change, missing frames |

## Acceptance (RM-01)

At least one published experiment record **per modulator** with a defined success
criterion: byte-exact SHA-256 recovery, or a documented failure mode. RM-02 (the README
command chain) and RM-04 (the results table) are written from these records.
