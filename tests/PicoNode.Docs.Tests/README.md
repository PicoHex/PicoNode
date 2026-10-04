# PicoNode.Docs.Tests

Keeps the README code samples honest.

- `ReadmeSamples.cs` holds the README's rate-limiting samples **verbatim**, between
  `// #region readme:policy` / `// #region readme:bucket` markers, inside methods that
  compile against `PicoNode.Web`.
- `ReadmeSamplesTests` compares each marked region with the matching `csharp` block in all
  ten `README*.md` files (they share one block per sample - only the prose is translated)
  and fails when they drift.

So: edit a sample in a README, mirror it in `ReadmeSamples.cs` (and the other READMEs), or
this project fails. A sample that stops compiling fails the build.
