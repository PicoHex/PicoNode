# PicoNode.Docs.Tests

Keeps the README code samples honest: a sample is code that compiles, or it is not a sample.

- `samples/readme/*.cs` holds the README's TCP echo and metrics blocks **verbatim**, between
  `// #region readme:echo` / `// #region readme:metrics` markers. That project is referenced by
  this one, so a sample that stops compiling fails the docs test build - not only a solution build.
- `ReadmeSamples.cs` holds the rate-limiting samples the same way (`readme:policy`,
  `readme:bucket`), inside methods that compile against `PicoNode.Web`.
- `ReadmeSamplesTests` compares each marked region with the matching `csharp` block in all ten
  `README*.md` files (they share one block per sample - only the prose is translated) and fails
  naming the READMEs that drifted.

So: edit a sample in a README, mirror it in the sample source (and the other nine READMEs), or
this project fails. A sample that stops compiling fails the build.
