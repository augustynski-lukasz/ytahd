# YouTube as a Hard Drive (YTAHD)

Research-grade CLI tool (C# / .NET 8) that encodes arbitrary binary data into 4K@60fps video
frames as a self-verifying experiment in transporting bytes through lossy video-transcoding
platforms. Includes four modulators (monochrome macroblock grid, pseudo-QAM, DCT-domain
carriers, motion-vector tiles), an FFmpeg pipe transport, and a durability matrix with
SHA-256 integrity verification. Byte-exact recovery is validated over local real-FFmpeg
round-trips; YouTube transit itself is not yet validated (see docs/ROADMAP.md, Epic E1).

See README.md for design, suitability notes, and roadmap.
