// Chatty stdio fixture — writes far more than a pipe buffer to stderr BEFORE
// it serves its first request, the way a real extension dumps a stack trace, a
// Node warning or debug output at startup. `fs.writeSync(2, …)` is a blocking
// write(2) on both POSIX and Windows, so the child parks inside the write when
// nobody drains the pipe — and never reaches the request loop below.
import fs from "node:fs";
import readline from "node:readline";

const CHUNK = "x".repeat(4096);
const TOTAL = 200_000; // ≫ any OS pipe buffer (4–64 KB)
for (let written = 0; written < TOTAL; written += CHUNK.length) {
  fs.writeSync(2, CHUNK);
}
fs.writeSync(2, "\nCHATTY-STDERR-END\n");

const rl = readline.createInterface({ input: process.stdin, terminal: false });
rl.on("line", (line) => {
  try {
    const req = JSON.parse(line);
    const out = { jsonrpc: "2.0", id: req.id ?? null };
    out.result = JSON.stringify({ echo: req });
    process.stdout.write(JSON.stringify(out) + "\n");
  } catch {
    process.stdout.write(
      JSON.stringify({ jsonrpc: "2.0", id: null, error: { code: -32700, message: "parse" } }) + "\n"
    );
  }
});
process.stdin.on("end", () => process.exit(0));
