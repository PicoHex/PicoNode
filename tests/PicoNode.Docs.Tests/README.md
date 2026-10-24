# PicoNode.Docs.Tests

Keeps the README code honest: a sample is code that compiles, or it says in writing that it is not.

- `samples/readme/*.cs` holds the README's samples **verbatim**, between `// #region readme:<name>`
  markers — the TCP echo, metrics, transport options, config reload, logging, compression, static
  files, CORS and cookies/multipart blocks. That project is referenced by this one, so a sample that
  stops compiling fails the docs test build, not only a solution build.
- `ReadmeSamples.cs` holds the rate-limiting samples the same way (`readme:policy`,
  `readme:bucket`), inside methods that compile against `PicoNode.Web`.
- `ReadmeSamplesTests` compares each marked region with the matching `csharp` block in all ten
  `README*.md` files and fails naming the READMEs that drifted.
- `ReadmeCodeBlockTests` measures the block inventory instead of trusting it:
  - every fence must pair with a bare closing fence (a stray fence used to turn the rest of the
    English README into literal code);
  - all ten READMEs must carry the same blocks, byte for byte — only the prose is translated;
  - every block of `README.md` is either a compiled region or carries
    `<!-- readme-fragment: <reason> -->` on the line above its fence, with a real reason; and
  - every region must be quoted by a README block exactly once (a sample nobody shows is dead
    weight that still has to compile).

So: edit a sample in a README and mirror it in the sample source **and the other nine READMEs**, or
this project fails. A new snippet that cannot be compiled as a whole (it declares a class, opens with
`using` directives, or is a comment-only note) gets a `readme-fragment` marker saying why — that
marker is the only supported way to ship code the build does not prove.
