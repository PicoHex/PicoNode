// NDJSON echo fixture — reads request frames on stdin, replies a response frame
// per line with the request id preserved and the request echoed inside
// `result` as a JSON STRING (the reader still yields it as raw payload text,
// and the request itself arrives with `params` inlined as a JSON value).
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