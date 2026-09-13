# YouTube as a Hard Drive (YTAHD) - Binary to Video Pipeline

[Buy me a coffee](https://buymeacoffee.com/augustynskh)

## Project Layout

- `YTAHD.Core` - reusable encoding/decoding library (engines, modulation, ffmpeg abstractions, audio helpers)

- `YTAHD.Cli` - command-line host application built on top of `YTAHD.Core`
- `probe` - quick real-FFmpeg smoke test across the main modulator modes
- `YTAHD.Tests` - unit/integration tests for core functionality
- `YTAHD.Perf` - performance and overhead analysis tool for comparing algorithms

## CLI Usage

Run commands from the repository root. The CLI resolves `ffmpeg` from `PATH` by default.

```powershell
dotnet run --project YTAHD.Cli -- encode input.bin output.mp4 --modulator phase3
dotnet run --project YTAHD.Cli -- decode output.mp4 recovered.bin --modulator phase3
```

Use the same `--modulator` value for encoding and decoding. Available modes are `phase1`,
`phase2`, `phase3`, and `phase4`; the default is `phase1`. `phase4` (motion-vector
modulation) is implemented and validated against a real libx264 round trip (see
`docs/decisions/F-20260903-01-phase4-motion-vector-design.md`).

### Encode

```text
dotnet run --project YTAHD.Cli -- encode <input> <output> [options]
```

| Option                    | Default       | Description                                                 |
| ------------------------- | ------------- | ----------------------------------------------------------- |
| `--macroblock-size`, `-m` | `16`          | Macroblock size in pixels.                                  |
| `--width`, `-w`           | `3840`        | Output video width.                                         |
| `--height`, `-H`          | `2160`        | Output video height.                                        |
| `--fps`, `-r`             | `60`          | Output framerate.                                           |
| `--modulator`, `-M`       | `phase1`      | Modulation mode: `phase1`, `phase2`, `phase3`, or `phase4`. |
| `--ffmpeg-path`           | `PATH` lookup | Explicit path to `ffmpeg.exe` or its directory.             |
| `--video-encoder`, `-E` | `libx264` | Video encoder: `libx264` (CPU baseline), `h264_qsv` (Intel Quick Sync), `h264_nvenc`/`h264_amf` (experimental). |
| `--hwaccel` | `none` | Decode-side hardware acceleration for experiments: `none`, `qsv`, `cuda`, `d3d11va`. |
| `--audio-clock`           | `false`       | Mux an audio FSK datagram clock alongside the video.        |

### Decode

```text
dotnet run --project YTAHD.Cli -- decode <input> <output> [options]
```

| Option              | Default       | Description                                                               |
| ------------------- | ------------- | ------------------------------------------------------------------------- |
| `--modulator`, `-M` | `phase1`      | Modulation mode used to create the video.                                 |
| `--ffmpeg-path`     | `PATH` lookup | Explicit path to `ffmpeg.exe` or its directory.                           |
| `--audio-clock`     | `false`       | Cross-check the audio FSK datagram clock against the decoded frame count. |

### Reusable Service API

Host apps (CLI, GUI, Web) can call a stable app-layer API from `YTAHD.Core.Application`:

```csharp
using YTAHD.Core.Application;
using YTAHD.Core.Modulation;

var service = new YtahdCodecService(
	new BinaryGridModulator(),
	new DefaultFFmpegWrapperFactory());

await service.EncodeAsync(new EncodeOptions
{
	InputFile = "input.zip",
	OutputVideo = "out.mp4",
	Width = 3840,
	Height = 2160,
	MacroblockSize = 16,
	Fps = 60
});
```

### Real ffmpeg integration

The encoder and decoder use a real FFmpeg process for the video container layer. By default, the project resolves `ffmpeg` from the current `PATH`. If an explicit install path is required, set it via the CLI option or the wrapper factory:

```powershell
dotnet run --project YTAHD.Cli -- encode input.bin output.mp4 --ffmpeg-path "D:\!Tools\ffmpeg-20151019\bin\ffmpeg.exe" --fps 60
```

You can also pass the containing FFmpeg `bin` directory instead of the executable path. The
same explicit override is available in decode mode:

```powershell
dotnet run --project YTAHD.Cli -- decode input.mp4 output.bin --ffmpeg-path "D:\!Tools\ffmpeg-20151019\bin\ffmpeg.exe"
```

The Phase 1 baseline currently targets the real H.264 path (`libx264`), which is the practical codec pair used by the smoke-validation workflow. This is now treated as the stable production contract for future modulation work until a new baseline is explicitly approved.

### GPU acceleration (Intel Quick Sync)

Hardware encoding is opt-in. The default remains CPU `libx264`; selecting a GPU encoder
requires the FFmpeg build to ship the encoder and the machine to expose a working device
(see `docs/decisions/CR-20260912-06-gpu-acceleration-qsv-scoping.md`):

```powershell
dotnet run --project YTAHD.Cli -- encode input.bin output.mp4 --video-encoder h264_qsv
```

- **QSV (`h264_qsv`)** is the only hardware-validated profile: every modulator
  (`phase1`–`phase4`) round-trips a real QSV encode with byte-exact payload recovery and
  `integrity=passed`. On the reference machine (Intel UHD 630) a 30-frame 640x480 encode
  ran ~1.7x faster than `libx264`.
- **NVENC (`h264_nvenc`) and AMF (`h264_amf`)** are declared but experimental: they are
  validated only by capability probing, not by the durability bar, and may fail on your
  driver/build.
- The CLI probes `ffmpeg -encoders` / `ffmpeg -hwaccels` before a long encode and fails
  fast with an actionable message when the requested encoder is missing or the device is
  unavailable.

Troubleshooting common GPU failures:

| Symptom | Meaning | Fix |
| --------------------------------------------- | ---------------------------------------------------- | ---------------------------------------------- |
| `Unknown encoder 'h264_qsv'` | FFmpeg build lacks the encoder | Use a full/gpl-shared FFmpeg build |
| `OpenEncodeSessionEx failed: unsupported device` | Driver rejects the NVENC session (common on GeForce consumer GPUs for some builds) | Update drivers, or fall back to `libx264` |
| `No device available for encoder` / probe failure | No usable GPU device for the requested profile | Run without `--video-encoder` (CPU baseline) |
| `implied codec not found` / pixel-format errors | Encoder cannot accept the input pixel format | Keep the default `yuv420p` conversion path |

### Current implementation status

The real FFmpeg pipeline is working and the lossy decoder has been hardened to tolerate H.264 drift, duplicate frame runs, and empty/weak payloads without silently accepting corrupted output. The packet protocol and quality-scoring logic have been centralized into dedicated helpers. Hardware encoding via Intel Quick Sync (`h264_qsv`) is opt-in and validated against the same durability bar as the CPU baseline (see the GPU acceleration section above).

Phase 1 (monochrome binary grid) and Phase 2 (pseudo-QAM multi-channel) are the production-validated baselines. Phase 3 (DCT-domain carrier) is fully implemented: each 8×8 block is synthesised via IDCT from a DC term and 8 low-frequency AC carriers, and bits are recovered on decode by reading forward DCT coefficient signs. All three modulator paths pass the full test suite including real libx264 round-trip smoke checks. The Data Durability Matrix (parity-based symbol transport) is integrated into the service pipeline and verified under real FFmpeg output.

Phase 4 (motion-vector modulation, absolute-displacement scheme — see ADR
`F-20260903-01-phase4-motion-vector-design.md`) is implemented and wired into the encode/decode
pipeline: a deterministic 8×8 tile texture is shifted per payload byte on displaced frames,
alternating with canonical "no data" separator frames that mark datagram boundaries. Validated
against a real libx264 (CRF-23) round trip with 100% payload recovery across single- and
multi-frame payloads, in addition to the full unit-test suite (synthetic Gaussian noise, blur,
luma shift, fake-FFmpeg pipeline round-trips). A 4×4/16×16 tile-size sweep remains a deferred
tuning follow-up — see `docs/BACKLOG.md`.

The remaining backlog is maintenance, Phase 4 perf/docs polish (`docs/PLAN.md` stage A6), and
the audio FSK clock (workstream B).

A high-performance command-line utility implemented in C# that encodes any binary data (e.g., `.zip` files) into a 4K 60fps video stream optimized to survive YouTube's lossy compression algorithms (VP9/AV1), allowing files to be archived and retrieved directly from video hosting platforms.

## Lessons Learned

- Real H.264 output is lossy by design. A single hard threshold for black-vs-white decoding is not reliable under `libx264`; the decoder must score multiple luminance candidates and accept a range of values instead of assuming perfect binary separation.
- The actual stream contract is a decoded raw RGB24 frame from FFmpeg, not a raw RGBA buffer. The decoder must honor the real stride and pixel layout (`width * 3` per row for RGB24) rather than the internal in-memory RGBA layout used during render generation.
- Packet decode success depends on both the bit-decoder logic and the frame geometry assumptions. A fix that only changes thresholding can still fail if the row stride, payload sizing, or packet indexing are inconsistent with the actual decoded frame.
- End-to-end smoke tests with real FFmpeg remain the authoritative validation path. Synthetic tests are useful for debugging, but only a real codec round-trip proves the protocol still survives the production compression pipeline.

---

## ⚠️ The Core Challenge: YouTube Video Compression

YouTube processes all uploaded videos using aggressive lossy codecs (**VP9** and **AV1**). These codecs treat the video as an organic visual scene, applying two highly destructive processes:

1. **Spatial Quantization (DCT Discard):** High-frequency details (such as sharp pixel edges or random noise) are treated as irrelevant artifacts and dropped.
2. **Temporal Inter-frame Compression:** Codecs heavily compress the differences between consecutive frames (P-frames and B-frames). If every frame changes completely (high entropy), the bitrate collapses, resulting in catastrophic data corruption.

To bypass these limitations, this project implements layout patterns and modulation schemes that cooperate with, rather than fight against, video compression pipelines.

---

## 🛠️ System Architecture Principles

To guarantee 100% data integrity upon decoding, the pipeline enforces the following physical boundaries:

- **Resolution:** Locked to **4K Ultra HD (3840×2160)**. YouTube allocates its highest premium bitrate profiles exclusively to 4K videos, deploying the superior VP9/AV1 engines.
- **Framerate:** **60 FPS**. Higher framerates force YouTube to assign wider data streams compared to 24/30 FPS profiles.
- **Macropixel Mapping:** Pixels are grouped into distinct **16×16 blocks**. This aligns perfectly with the native macroblock size of video encoders, preventing edge blurring across block boundaries.
- **Luminance-First Encoding:** Chrominance subsampling (YUV 4:2:0) severely degrades color resolution. Data is primarily encoded within the **Y (Luminance)** channel or across predictable, widely separated RGB states to survive color smashing.

---

## 🗺️ Roadmap & Implementation Plan

The project is structured into 4 sequential evolutionary phases, moving from basic high-contrast layouts to advanced video-codec manipulation.

### Phase 1: High-Contrast Monochromatic Grid (The Baseline)

- **Concept:** Every 16×16 pixel block represents a single binary bit (Pure Black `0x00` = `0`, Pure White `0xFF` = `1`).
- **Datagram Lifespan:** 3 frames per data burst (at 60 FPS) to let the inter-frame compression stabilize.
- **Synchronization:** A high-contrast checkerboard frame wrapper (calibration border) used by the decoder via perspective transformation to rectify optical scaling.
- **Error Correction:** Basic 1D Reed-Solomon coding injected into each data row.

### Phase 2: Pseudo-QAM Multi-Channel Modulation

- **Concept:** Instead of 1 bit per macroblock, we utilize independent amplitude modulation across the **R, G, and B channels** simultaneously.
- **Modulation:** 16-PAM per channel (16 distinct intensity levels per color component: `0, 17, 34, ... 255`). This packs $4 \text{ bits} \times 3 \text{ channels} = 12 \text{ bits}$ per macroblock.
- **Calibration Pilots:** The outer border displays a static "reference palette" containing all 16 allowed states. The decoder measures how YouTube warped these exact states and dynamically recalculates the decision thresholds.

### Phase 3: Discrete Cosine Transform (DCT-Domain) Engineering

- **Concept:** Instead of generating sharp square blocks (which create high-frequency noise that the codec discards), data is injected directly into the frequency domain by synthesising each 8×8 pixel block from a DC term plus 8 low-frequency AC cosine carriers via the 2-D Inverse Discrete Cosine Transform (IDCT). The high-frequency coefficients (bottom-right of the 8×8 DCT matrix) are left at zero. The resulting frames appear as smooth, organic gradients — exactly the signal structure that lossy video codecs preserve with highest priority.

#### Carrier positions

Eight AC coefficient positions are used, chosen as the 8 lowest-frequency non-DC entries of the 8×8 DCT matrix (ordered by $u+v$ ascending):

$$
(u, v) \in \{(0,1),(1,0),(1,1),(0,2),(2,0),(0,3),(3,0),(1,2)\}
$$

Each position encodes 1 bit. Together they give **1 byte per 8×8 block**.

#### Encoding: IDCT synthesis

For each payload byte, 8 bits select the sign of each carrier coefficient. The DC coefficient is fixed at 1024 (producing a mean pixel value of 128). Each pixel $f(x,y)$ in the 8×8 block is computed as:

$$
f(x,y) = \underbrace{\frac{C(0)^2}{4} \cdot 1024}_{= 128\text{ (DC)}}
\;+\;
\sum_{i=0}^{7}
\frac{C(u_i)\,C(v_i)}{4}
\cdot
(\text{bit}_i = 1 \;?\; +128 : -128)
\cdot
\cos\!\frac{(2x{+}1)\,u_i\,\pi}{16}
\cdot
\cos\!\frac{(2y{+}1)\,v_i\,\pi}{16}
$$

where $C(k) = 1/\!\sqrt{2}$ for $k=0$ and $C(k)=1$ for $k>0$. Pixel values are clamped to $[0,255]$.

The carrier amplitude of 128 produces pixel-domain waves of roughly $\pm 18$–$32$ luma units above the DC mean — well above H.264 CRF-23 quantization noise for spectrally smooth blocks.

#### Decoding: forward DCT

On decode, the forward 2-D DCT is applied to each 8×8 block of luma values. The sign of each carrier coefficient recovers 1 bit:

$$
F(u,v) = \frac{C(u)\,C(v)}{4}
\sum_{x=0}^{7}\sum_{y=0}^{7}
f(x,y)
\cdot
\cos\!\frac{(2x{+}1)\,u\,\pi}{16}
\cdot
\cos\!\frac{(2y{+}1)\,v\,\pi}{16}
\qquad
\text{bit} = \bigl[F(u,v) > 0\bigr]
$$

**Noise robustness:** H.264 quantization errors on spectrally smooth blocks are approximately zero-mean and uncorrelated. Because $\sum_{x=0}^{7}\cos\frac{(2x+1)k\pi}{16} = 0$ for $k>0$, the error contribution to any AC coefficient averages to near zero across the 64-pixel block. Coefficient signs therefore survive lossy compression reliably, unlike per-pixel thresholding.

#### Capacity

$$
\text{bytes per frame} = \left\lfloor\frac{W - 2B}{8}\right\rfloor \times \left\lfloor\frac{H - 2B}{8}\right\rfloor \times 1
$$

where $W$, $H$ are frame dimensions and $B$ is the border width (default 32 px). For 3840×2160 with a 32 px border: $\lfloor 3776/8 \rfloor \times \lfloor 2096/8 \rfloor = 472 \times 262 = 123{,}664$ bytes per frame. The current v2 frame packet header stores payload length as a 32-bit value, so this capacity is usable rather than capped by the older v1 16-bit length field.

- **Datagram lifespan:** 2 frames at 60 FPS (smooth blocks are codec-friendly; fewer repeat frames are needed for stability).

### Phase 4: Motion Vector Abuse (Temporal Tracking)

- **Concept:** YouTube is highly efficient at tracking moving elements across frames. In this phase, data is not stored in the pixels themselves, but in the **direction and velocity** of repeating graphical patterns shifting between frames.
- **Modulation:** Kafelks/Tiles move by precise pixel offsets $(X, Y)$ relative to the previous frame. The coordinate offset acts as the data byte.
- **Advantage:** Optical flow tracking algorithms in the decoder can calculate movement vectors even if the underlying texture is blurred. This unlocks a datagram lifespan of **1 to 2 frames**, drastically elevating the data-per-minute ceiling.

---

## 🔊 Audio-Assisted Clock Synchronization

> **Status: implemented, optional (default off).** Enable with `--audio-clock` on both encode
> and decode (or `VideoCodecOptions.UseAudioClock`). See ADR
> `docs/decisions/F-20260903-02-audio-fsk-clock-design.md` for the corrected design and
> real-codec validation results.

To prevent frame-dropping or frame-duplication errors from permanently desynchronizing the stream, an audio sub-carrier is available:

- **Signalization:** A continuous Audio FSK (Frequency Shift Keying) tone loop utilizing resilient bands ($1000 \text{ Hz}$ hold and $1500 \text{ Hz}$ datagram-start pulse), phase-continuous and amplitude-ramped at segment edges to avoid clicks.
- **Operation:** At the start of each logical (data/parity) frame, the audio shifts to $1500 \text{ Hz}$ for 2 video frames (long enough to survive AAC's 1024-sample frame size), then drops back to $1000 \text{ Hz}$ for the remaining hold frames.
- **Decoding:** A two-bin Goertzel magnitude comparison (frequency ratio only, immune to loudness normalization) classifies each video-frame-aligned window as hold or pulse; rising edges mark datagram boundaries. The resulting count is exposed as `DecodeMetrics.AudioDatagramCount` for cross-checking against the video-decoded frame count — validated within a ±2 frame tolerance on a real libx264+AAC round trip.
- **Combined-clock arbitration:** `DecodeMetrics.HasAudioVideoDatagramMismatch()` compares the audio-derived datagram count against what the video pipeline actually reconstructed (data + parity frames after XOR recovery), catching a case plain XOR-parity cannot: a _whole_ parity group silently dropped (both its data and parity frames), which otherwise truncates the output with no error. Combined with the hole-tolerant durability matrix (see `docs/decisions/CR-20260913-02-combined-clock-multi-erasure-repair.md`), such a loss is now also _repaired_ as far as information allows: each parity group recovers independently, wholly-lost groups become zero-filled holes with the exact group ids surfaced in `DecodeMetrics.MissingDatagramIds`, and integrity still fails loudly (`integrity=failed`) instead of silently accepting corrupt output.
- **Known limitation:** Phase 4's fast 2-physical-frame-per-datagram cadence leaves no room for a hold gap between pulses, so only the first datagram boundary is currently audio-detectable in that mode (tracked in `docs/BACKLOG.md`).

---

## 🧮 Data Durability Matrix & Integrity Verification

The transport layer combines two complementary protections above the frame/packet protocol:

**Data Durability Matrix** (parity-based symbol transport, ADR
`F-20260830-04-data-durability-matrix.md`). Payload bytes are split into symbol blocks grouped
by durability window; each group carries an XOR parity symbol so a single missing frame per
group is rebuilt at decode. The matrix is integrated into the live service path — enable it
with `VideoCodecOptions.UseDurabilityMatrix` (service API) and it is exercised by the
real-FFmpeg regression tests. The runtime API stays replaceable for future fountain-style
expansion.

**Integrity verification** (ADR `CR-20260912-05-integrity-durability-combination.md`). Three
levels of proof, each closing a different silent-failure hole:

- **Per-frame SHA-256** — the v2 frame header hash is enforced strictly on decode: a frame
  whose payload does not match its header hash is rejected as corrupt, not accepted with a low
  quality score. Legacy v1 frames keep their hash-gated wrapped-length recovery.
- **Stream manifest** — a dedicated manifest frame (`FrameTypeManifest`) records protocol
  version, total payload bytes, the whole-payload SHA-256, modulator identity, geometry, data
  frame count, and parity settings. It is emitted redundantly at the stream start and end, so
  at least one copy survives typical head/tail loss. Multiple intact copies are reconciled;
  conflicting manifests fail the decode loudly.
- **Whole-payload verification** — after the matrix reconstructs the payload, its SHA-256 is
  compared against the manifest hash and its length against the manifest's declared byte
  count. The CLI decode summary reports `integrity=passed`, `integrity=failed`, or
  `integrity=frame-only` (legacy streams without a manifest).

Together this means the receiver can rebuild a payload from a valid subset **and prove** the
result equals the original: a corrupted length field can no longer silently truncate the
output, and a damaged frame is either repaired through parity or rejected loudly.

Evidence: 265/265 suite tests green including real libx264 round trips reporting
`integrity=passed`, corruption-rejection cases, and the legacy frame-only path.

---

## 📈 Performance Analysis Project

A dedicated project is available at `YTAHD.Perf` to compare encoding/redundancy algorithms and compute transmission statistics.

### What it reports

- Initial payload size
- Video total size
- Frame size
- Payload with overhead per frame
- Payload net data per frame
- Frame header size
- Total payload overhead
- Overhead percentages (header/parity/total)
- Averages per frame (logical and physical)
- Totals for whole video
- Video bandwidth
- Data bandwidth

### Run examples

```powershell
dotnet run --project YTAHD.Perf\YTAHD.Perf.csproj -- --payload-bytes 10485760 --algorithm xor-parity --repeat 3 --parity-group 4
```

```powershell
dotnet run --project YTAHD.Perf\YTAHD.Perf.csproj -- --payload-bytes 52428800 --compare true
```

### Options

- `--payload-bytes` input payload size (bytes)
- `--width` frame width
- `--height` frame height
- `--macroblock` macroblock size in pixels
- `--fps` frame rate
- `--header-bytes` frame header size
- `--repeat` physical repeats per logical frame
- `--parity-group` data frames per parity frame for xor-parity
- `--algorithm` `repeat` or `xor-parity`
- `--compare` compare `repeat(x1)`, `repeat(xN)`, and `xor-parity`

## License

Licensed under the [MIT License](LICENSE).
