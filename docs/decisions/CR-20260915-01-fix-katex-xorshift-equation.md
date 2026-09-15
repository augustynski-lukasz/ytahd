# CR-20260915-01 — Fix math render failures in ARCHITECTURE.md (KaTeX `\^`, GitHub `&` escaping)

**Date:** 2026-09-15 **Status:** Implemented
**Area:** `docs/ARCHITECTURE.md` (§7.2, §7.4 math sections)

## Context

The user reported that some Mermaid diagrams and mathematical equations in
`docs/ARCHITECTURE.md` failed to render — first in the local Markdown preview
(Markdown Preview Enhanced 0.8.35, bundling Mermaid 11.17.2 and KaTeX), then on
github.com (MathJax) for the §7.2 and §7.4 nibble-split equations.

## Observed failure

- The xorshift32 PRNG equation in §7.4 rendered as a raw KaTeX parse error:
  `KaTeX parse error: Expected group as argument to '\^' at position 14`.
- On github.com, the §7.2 equation `$\text{lo} = v \mathbin{\&} \text{0x0F}$` and
  the §7.4 equations `$\text{dx}_c = (v \gg 4) \mathbin{\&} \text{0xF}$` failed to
  render (MathJax "Misplaced &"-class error).
- All 10 Mermaid diagrams were suspected but verified fine (see Root cause).

## Root cause

- **Defect 1 (the real one):** the equation
  `$s \mathrel{\^}= s \ll 13; \dots$` used `\^` inside `\mathrel{}`. In KaTeX,
  `\^` is the circumflex-accent command and **requires a group argument**
  (`\^{o}`); a bare `\^` followed by `}` is a parse error, so the whole inline
  equation failed to render.
- **Defect 2:** GitHub's Markdown pipeline HTML-escapes `&` inside `.md` files
  **before** MathJax parses the math, so `\mathbin{\&}` arrives as `\mathbin{&amp;}`
  and MathJax errors out. GitHub's own docs acknowledge this class of problem — the
  `$`…`$` backtick-delimited math syntax exists for "characters that overlap with
  markdown syntax". Any raw `&` (even backslash-escaped) inside `$…$`/`$$…$$` is
  unreliable on GitHub.
- **Red herring:** the diagrams and much of the document *looked* mojibake-corrupted
  (`Ôćĺ`, `├Ś`, `ÔÇö`) during investigation. A byte-level audit showed the file is
  clean UTF-8 — the artifacts were produced by the terminal/tool display pipeline
  decoding UTF-8 as CP1252, not by the file itself. No encoding fix was needed.

## Resolution

- **Defect 1:** replaced `\mathrel{\^}` with `\mathrel{\oplus}` — valid KaTeX and
  semantically more accurate, since xorshift32's update is XOR (`⊕`), not a caret
  operator. Verified: all 71 math snippets in the document now pass
  `katex.renderToString` with `throwOnError: true`.
- **Defect 2:** rewrote both nibble-split equations **without any ampersand**, using
  mathematically identical forms: `$\text{lo} = v \bmod 16$` /
  `$\text{hi} = \lfloor v / 16 \rfloor$` (§7.2) and
  `$\text{dx}_c = \lfloor v / 16 \rfloor$` /
  `$\text{dy}_c = v \bmod 16$` (§7.4). Verified against the source:
  `PseudoQamModulator.cs` (`value & 0x0F`, `(value >> 4) & 0x0F`) and
  `MotionTileBasis.cs` (`(value >> 4) & 0xF`, `value & 0xF`) — `mod 16` and
  `⌊v/16⌋` are exactly the low/high nibble extractions. The rewritten TeX passes
  KaTeX validation and contains no GitHub-hostile characters.

## Lessons

- **Verify the artifact, not the terminal.** Mojibake seen in tool output was a
  display-pipeline artifact; a code-point audit (`U+2014`, `U+00D7`, `U+2192` are
  legitimate characters) disproved the corruption hypothesis before any
  "fix" was attempted.
- **Test with the exact bundled renderer.** The preview extension bundles its own
  Mermaid (11.17.2); validating against npm's latest can give false pass/fail
  signals. The bundled copy was extracted and used for the render test.
- **`\^` is an accent command in KaTeX/LaTeX**, not a caret operator. Use
  `\oplus`, `\wedge`, or `\text{\^{}}` depending on intent.
- **GitHub escapes `&` (and `<`, `>`) before MathJax runs.** Never put a raw or
  backslash-escaped `&` inside `$…$`/`$$…$$` in repo Markdown. Prefer
  ampersand-free rewrites (`\bmod`, `\lfloor…\rfloor`) or GitHub's `$`…`$`
  backtick syntax. `<`/`>` inside math are also risky (`\lt`, `\gt` are safer).
- **Validate math against every target renderer**, not just one: a snippet can be
  valid KaTeX yet still fail on GitHub's MathJax pipeline (and vice versa).

## Consequences

- §7.4's xorshift32 equation renders correctly in KaTeX-based previews.
- §7.2 and §7.4 nibble equations render on github.com (MathJax), local KaTeX
  preview, and are semantically identical to the C# nibble extraction.
- Validation method (extract blocks → parse/render with the bundled Mermaid and
  KaTeX versions) is repeatable and caught exactly the defects reported.
