# YouTube as a Hard Drive (YTAHD) - Binary to Video Pipeline

## Project Layout

- `YTAHD.Core` - reusable encoding/decoding library (engines, modulation, ffmpeg abstractions, audio helpers)
- `YTAHD.Cli` - command-line host application built on top of `YTAHD.Core`
- `YTAHD.Tests` - unit/integration tests for core functionality
- `YTAHD.Perf` - performance and overhead analysis tool for comparing algorithms

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

The same explicit override is available in decode mode:

```powershell
dotnet run --project YTAHD.Cli -- decode input.mp4 output.bin --ffmpeg-path "D:\!Tools\ffmpeg-20151019\bin\ffmpeg.exe"
```

The Phase 1 baseline currently targets the real H.264 path (`libx264`), which is the practical codec pair used by the smoke-validation workflow. This is now treated as the stable production contract for future modulation work until a new baseline is explicitly approved.

### Current implementation status

The real FFmpeg pipeline is working and the lossy decoder has been hardened to tolerate H.264 drift, duplicate frame runs, and empty/weak payloads without silently accepting corrupted output. The packet protocol and quality-scoring logic have been centralized into dedicated helpers, and the remaining backlog is focused on extractive refactors at the decode orchestration boundary (`REFACTOR-008`, `REFACTOR-009`, and `FEAT-041`).

A high-performance command-line utility implemented in C# that encodes any binary data (e.g., `.zip` files) into a 4K 60fps video stream optimized to survive YouTube's lossy compression algorithms (VP9/AV1), allowing files to be archived and retrieved directly from video hosting platforms.

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

- **Concept:** Instead of generating sharp square blocks (which create high-frequency noise that the codec hates), we inject data directly into the frequency domain using DCT, matching how the VP9 encoder perceives the image.
- **Modulation:** We mathematically synthesize 8×8 or 16×16 blocks using only low and mid-frequency cosine waves (the top-left section of the DCT matrix). The high-frequency zones (bottom-right) are left as zeros.
- **Advantage:** The resulting video frames appear perfectly smooth, soft, and organic to the YouTube encoder. The compressor preserves these frames with maximum priority and zero ringing artifacts, allowing the datagram lifespan to be safely reduced to **2 frames**.

### Phase 4: Motion Vector Abuse (Temporal Tracking)

- **Concept:** YouTube is highly efficient at tracking moving elements across frames. In this phase, data is not stored in the pixels themselves, but in the **direction and velocity** of repeating graphical patterns shifting between frames.
- **Modulation:** Kafelks/Tiles move by precise pixel offsets $(X, Y)$ relative to the previous frame. The coordinate offset acts as the data byte.
- **Advantage:** Optical flow tracking algorithms in the decoder can calculate movement vectors even if the underlying texture is blurred. This unlocks a datagram lifespan of **1 to 2 frames**, drastically elevating the data-per-minute ceiling.

---

## 🔊 Audio-Assisted Clock Synchronization

To prevent frame-dropping or frame-duplication errors from permanently desynchronizing the stream, an audio sub-carrier is implemented:

- **Sygnalization:** A continuous low-frequency Audio FSK (Frequency Shift Keying) tone loop utilizing resilient bands ($1000 \text{ Hz}$ and $1500 \text{ Hz}$).
- **Operation:** At the exact frame a new visual datagram triggers, the audio instantly shifts to $1500 \text{ Hz}$ for exactly 1 frame duration, dropping back to $1000 \text{ Hz}$ during hold frames.
- **Decoding:** The C# application runs a lightweight Fast Fourier Transform (FFT) on the audio channel. A frequency spike acts as a hardware-like clock pulse, commanding the video tracker precisely when to sample a stable frame.

---

## 🧮 Data Durability Matrix

To achieve total file recovery without a single bit failing, the pipeline wraps data payloads inside **Fountain Codes (RaptorQ / Luby Transform)** prior to visual rendering.

Instead of traditional linear block boundaries, the input file is transformed into an infinite mathematical stream of symbol packets. The receiver can completely rebuild 100% of the original `.zip` archive as soon as it intercepts any **arbitrary 85% of the video frames**, completely neutralizing random frame loss or local macroblock corruption introduced by YouTube's processing pipeline.

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
