// Dying stdio fixture (T7.6c) — the T7.1 failure path, made permanent.
//
// A real extension that dies on start-up (bad config, missing binary, an
// uncaught throw) usually has nothing to say on stdout: it writes its last
// words to stderr and exits non-zero. Nothing can be answered on that channel,
// so the caller's only channel-crash evidence IS the stderr tail — this fixture
// is the smallest child that produces exactly that: a recognisable marker, then
// exit 3 before any request is read.
//
// `fs.writeSync(2, …)` is a blocking write(2) on both POSIX and Windows, so the
// bytes are IN the pipe before process.exit — the tail cannot be missed by a
// timing race between the drain and the exit. Marker text is asserted verbatim
// by LifecycleTests.Dead_child_stderr_tail_reaches_the_caller.
import fs from "node:fs";

fs.writeSync(2, "DEAD-CHILD-STDERR-MARKER-7c41e0\n");
fs.writeSync(2, "Error: the fixture failed on purpose (exit 3)\n");
process.exit(3);
