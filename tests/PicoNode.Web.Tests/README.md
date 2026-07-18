# PicoNode.Web.Tests

PicoNode.Web 中间件框架测试。

## 测试覆盖

- WebRouter: 精确路由 + RadixTree 参数化路由
- CorsMiddleware: 跨域策略
- CompressionMiddleware: gzip/deflate/brotli
- CacheMiddleware: 缓存控制
- SecurityHeadersMiddleware: HSTS, CSP, X-Frame-Options
- SseConnection: Server-Sent Events
- MultipartFormDataParser: multipart/form-data 解析
- SessionMiddleware: 会话生命周期
- AuthMiddleware: 认证流程
- StaticFileMiddleware: 静态文件服务
