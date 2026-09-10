using System.Runtime.CompilerServices;

// B2/B3: the crash/backoff lifecycle hooks (Churn/MarkHealthy) and the
// channel shape are exercised by both PicoJsonRpc.Tests and the binding
// adapter tests (PicoAgent.Binding.Tests) — same kernel, two test surfaces.
[assembly: InternalsVisibleTo("PicoJsonRpc.Tests")]
[assembly: InternalsVisibleTo("PicoAgent.Binding.Tests")]
