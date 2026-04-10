# PicoWeb Static Showcase

This sample turns `PicoWeb.Samples` into a small static site backed by `PicoNode.Web` features that already exist in the repo.

The theme cookie demo is same-origin. The CORS configuration is exposed separately for local API clients such as a dev app running on `http://localhost:3000`.

## What it demonstrates

- Static file hosting from `wwwroot`
- Response compression via `CompressionMiddleware`
- CORS preflight and response headers via `CorsHandler`
- Cookie parsing and `Set-Cookie` generation
- `multipart/form-data` parsing for uploads

## Run

```powershell
dotnet run --project samples/PicoWeb.Samples/PicoWeb.Samples.csproj
```

Then open `http://127.0.0.1:7004/`.

## Useful endpoints

- `GET /` - landing page
- `GET /api/showcase` - summary payload for the page
- `GET /api/preferences` - current cookie-backed theme state
- `POST /api/preferences/dark` - store a dark theme cookie
- `GET /api/content` - larger text payload for compression
- `POST /api/uploads` - multipart form-data demo

## Quick checks

### CORS preflight

```powershell
curl -i -X OPTIONS "http://127.0.0.1:7004/api/uploads" `
  -H "Origin: http://localhost:3000" `
  -H "Access-Control-Request-Method: POST"
```

### Compression

```powershell
curl -i "http://127.0.0.1:7004/api/content" -H "Accept-Encoding: gzip"
```

### Cookie-backed theme

```powershell
curl -i -X POST "http://127.0.0.1:7004/api/preferences/dark"
curl -i "http://127.0.0.1:7004/api/preferences" -H "Cookie: pico-theme=dark"
```

### Multipart upload

```powershell
curl -i -X POST "http://127.0.0.1:7004/api/uploads" `
  -F "metadata=sample upload" `
  -F "file=@samples/PicoWeb.Samples/wwwroot/index.html;type=text/html"
```
