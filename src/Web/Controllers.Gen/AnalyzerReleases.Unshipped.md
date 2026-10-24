; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
PCW001  | PicoNode.Web | Warning | ControllerMapsNoAction — controller discovered, no action matched an HTTP verb
PCW002  | PicoNode.Web | Error   | UnsupportedController — generic controller cannot be mapped
PCW003  | PicoNode.Web | Warning | DtoNotSerializable — serialised DTO without [PicoJsonSerializable]
