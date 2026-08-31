# Probe Project

This project is a lightweight smoke test for the real FFmpeg encode/decode pipeline used by the steganography codec.

## Purpose

The probe exists to quickly validate that the application can:

- encode a payload into a video stream using the configured modulator
- decode that stream back into bytes
- confirm that the recovered payload matches the original input

It exercises the real service layer and real libx264/FFmpeg pipeline rather than mocked or synthetic frame data.

## Included checks

The current probe runs a short round-trip for each supported modulator:

- `phase1`
- `phase2`
- `phase3`

For each mode it:

1. generates a temporary test payload
2. encodes it to an MP4 file
3. decodes it back to a file
4. compares the recovered bytes with the original payload
5. prints a concise success/failure summary

## How to run

From the repo root:

```powershell
dotnet run --project probe
```

## Notes

This is intentionally a quick validation tool, not the primary regression harness.

The formal verification should remain in the test project, where deterministic and structured assertions are maintained.
The probe is useful for ad hoc sanity checks when we want to see whether the live FFmpeg path still behaves correctly across the major modulation modes.

## Relationship to the main tests

- Use the normal xUnit project for permanent regression coverage.
- Use this probe for rapid real-pipeline smoke checks.
- Keep the real FFmpeg path as the final source of truth for production validation.
