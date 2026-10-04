# PicoNode.Web.Tests

PicoNode.Web middleware framework tests.

## Coverage

- WebRouter: exact routing + RadixTree parameterized routing
- CorsMiddleware: cross-origin policies
- CompressionMiddleware: gzip / deflate / brotli
- CacheMiddleware: cache control
- SecurityHeadersMiddleware: HSTS, CSP, X-Frame-Options
- SseConnection: Server-Sent Events
- MultipartFormDataParser: multipart/form-data parsing
- SessionMiddleware: session lifecycle
- AuthMiddleware: authentication flow
- RateLimitMiddleware: single bucket, `X-RateLimit-*` headers, 429 + Retry-After
- RateLimitPolicy: ordered tiers, budgets, classifiers, chained policies, exemption/applies semantics
- InMemoryRateLimitStore: refill, eviction and concurrent consumption
- RemoteAddress: peer plumbing (`HttpRequest.RemoteEndPoint`), IPv4-mapped IPv6 normalization
- StaticFileMiddleware: static file serving
