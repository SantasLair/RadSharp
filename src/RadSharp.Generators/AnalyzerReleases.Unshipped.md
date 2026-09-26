; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
RAD0002 | RadSharp | Error | Published property has an unsupported type or no public getter/setter
RAD1000 | RadSharp.Forms | Error | Form file isn't valid JSON or has no root object
RAD1001 | RadSharp.Forms | Error | Event handler named in the form was not found in the form class
RAD1002 | RadSharp.Forms | Warning | Unknown control class in the form file
RAD1003 | RadSharp.Forms | Warning | Unknown event in the form file
RAD1004 | RadSharp.Forms | Error | Control or handler name is not a valid C# identifier
RAD1005 | RadSharp.Forms | Error | Duplicate control name in the form file
