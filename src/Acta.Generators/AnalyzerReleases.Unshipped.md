; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
ACTA0101 | Acta | Error | Duplicate [Job] name within the manifest
ACTA0102 | Acta | Error | Invalid [Job] name (kebab-case, max 128, sys. prefix reserved for system)
ACTA0103 | Acta | Error | Invalid handler signature (per-variant message)
ACTA0104 | Acta | Warning | Duplicate input type; typed enqueue cannot route uniquely
ACTA0105 | Acta | Error | Invalid [Job] policy value (duration, bounds, undefined code)
ACTA0106 | Acta | Warning | Duplicate contract member name; colliding members are omitted
ACTA0121 | Acta | Error | Invalid [Schedule] declaration
ACTA0122 | Acta | Error | Invalid schedule expression (cron or positive ISO 8601 duration)
ACTA0123 | Acta | Error | Scheduled input has no accessible parameterless constructor
ACTA0124 | Acta | Error | Invalid [Schedule] policy value (MaxOutstanding below 1, undefined FullBehavior or MisfireStrategy)
ACTA0125 | Acta | Error | MaxOutstanding other than 1 on a Permanent definition's schedule
ACTA0126 | Acta | Error | Incomplete [Schedule] expiration (ExpireAfter without ExpireAction or the reverse, zero, undefined action)
ACTA0127 | Acta | Error | Incomplete [Schedule] source (expression and Calendar, neither, blank or invalid calendar name, a [Calendar] class bound in the wrong role)
ACTA0128 | Acta | Error | Invalid [Calendar] declaration (abstract, generic, or private class, neither calendar interface, invalid or duplicate name)
ACTA0131 | Acta | Error | Invalid [JobPayloadFormatDeclaration]
ACTA0132 | Acta | Error | Invalid [Job] payload-format usage (Format vs Input/OutputFormat, output on void, unknown name)
ACTA0142 | Acta | Error | Invalid Acta duration unit (uppercase or calendar unit)
ACTA0201 | ActaCodes | Error | Invalid code-family declaration
ACTA0202 | ActaCodes | Error | Invalid [Code] value (shape or id outside 0..254)
ACTA0203 | ActaCodes | Error | Duplicate [Code] value (code string or numeric value)
ACTA0204 | ActaCodes | Error | Retired or reserved code identity reuse and invalid reserve declarations
ACTA0401 | ActaSchema | Error | Incomplete schema declaration
ACTA0402 | ActaSchema | Error | Column mapping does not match the CLR type
ACTA0403 | ActaSchema | Error | Column DEFAULT incompatible with kind or allocation
ACTA0501 | ActaProjection | Error | Invalid [DbProjection] materializer shape
