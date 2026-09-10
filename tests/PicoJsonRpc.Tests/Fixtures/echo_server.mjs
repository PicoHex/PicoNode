// NDJSON echo fixture — mirrors the A0 string-embedded wire contract: reads
// request frames on stdin, replies a response frame per line with the request
// id preserved and the request echoed as raw JSON text in `result`.
import readline from "node:readline";
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