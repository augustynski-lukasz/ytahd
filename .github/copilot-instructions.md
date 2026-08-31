# Copilot instructions for YTAHD

## Project overview

- This repo is a .NET 8 video steganography / data-in-video project.
- Main solution: `YTAHD.sln`
- Core library: `YTAHD.Core`
- CLI app: `YTAHD.Cli`
- Tests: `YTAHD.Tests`
- Perf tool: `YTAHD.Perf`

## Architecture and conventions

- Prefer working through the app/service layer (`YTAHD.Core.Application`) instead of reaching directly into implementation details when possible.
- Keep modulator logic and geometry together. Shared geometry lives in `ModulatorGeometry`; shared app config lives in `VideoCodecOptions`.
- Do not reintroduce long parameter lists for modulator methods when a value object already exists.
- Prefer the strategy abstraction already in place: `IModulator`, `EncoderEngine`, `DecoderEngine`, `YtahdCodecService`, and the FFmpeg wrapper factory.
- Preserve the existing separation between video-container handling and data modulation. FFmpeg is the codec layer; modulation is the data layer.

## Real FFmpeg contract

- The current production baseline is a real H.264 / `libx264` encode/decode path.
- The project must use a real FFmpeg process for end-to-end validation; synthetic-only tests are not enough for final verification.
- Default behavior is to resolve `ffmpeg` from `PATH`.
- If an explicit path is needed, support the explicit override via the CLI and keep the default fallback behavior intact.
- Never assume bit-perfect decode from lossy H.264 output. Decode logic must be tolerant of drift, duplicated frames, weak payloads, and imperfect luminance separation.

## Quality bar for changes

- Add or update a regression test before or alongside a functional fix.
- Prefer small, root-cause-focused fixes over broad refactors.
- When changing decoding logic, validate against real FFmpeg round-trips, not only fake wrapper tests.
- When changing modulator APIs, keep compatibility patterns safe unless a cleaner migration is explicitly intended.

## Validation commands

Run the minimal relevant command before claiming a fix is ready.

### Full project validation

```powershell
dotnet test YTAHD.Tests/YTAHD.Tests.csproj --logger "console;verbosity=minimal"
```

### CLI smoke check

```powershell
dotnet run --project YTAHD.Cli -- encode sample.bin out.mp4 --modulator phase1 --ffmpeg-path "<path-to-ffmpeg>"
dotnet run --project YTAHD.Cli -- decode out.mp4 recovered.bin --modulator phase1 --ffmpeg-path "<path-to-ffmpeg>"
```

## Important project notes

- Lossy compression is the core constraint. Binary assumptions from perfect black/white frames are invalid under real H.264 output.
- The decoder must score several luminance candidates and tolerate a range of values instead of expecting a single threshold.
- Geometry assumptions (row stride, frame width/height, payload size, header size, border width) are part of the protocol contract and must stay aligned with the real decoded FFmpeg frames.
- The CLI and service layer should continue to make the “real ffmpeg + PATH fallback” workflow easy and predictable for future work.

## Preferred change patterns

- Keep data-encoding and decoding contract logic inside the relevant engine or modulator.
- Centralize duplicated config into shared types instead of spreading scalar settings through many methods.
- Prefer object-based configuration for encode/decode operations when multiple values are passed together.
- When refactoring, keep the public API stable unless a specific migration is already underway.

## Working style

- Be explicit about assumptions and validation evidence.
- Write clear, minimal code comments only when they capture a non-obvious protocol reason.
- Keep project documentation and TODO status aligned with the actual code state.
