// A0 probe (2026-09-06): the [PicoSerializable] attribute lives in the
// PicoSerDe.Core namespace (NOT PicoJetson), and PicoJetson.JsonSerializer
// is the stack-mandated serializer — both globals mirror the PicoAgent.Domain
// convention (all JsonSerializer calls resolve via this global, zero STJ).
global using System.Runtime.CompilerServices;
global using System.Text;
global using PicoJetson;
global using PicoSerDe.Core;
