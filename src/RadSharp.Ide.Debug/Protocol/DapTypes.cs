using System.Text.Json;
using System.Text.Json.Serialization;

namespace RadSharp.Ide.Debug.Protocol;

// Typed subset of the Debug Adapter Protocol. Property names follow the wire format (camelCase, except where the
// spec says otherwise). Every type here is registered in DapJson so (de)serialization is source-generated.

public sealed record DapSource
{
    public string? Name { get; init; }
    public string? Path { get; init; }
    public int? SourceReference { get; init; }
}

public sealed record DapCapabilities
{
    public bool SupportsConfigurationDoneRequest { get; init; }
    public bool SupportsConditionalBreakpoints { get; init; }
    public bool SupportsHitConditionalBreakpoints { get; init; }
    public bool SupportsEvaluateForHovers { get; init; }
    public bool SupportsTerminateRequest { get; init; }
    public bool SupportsExceptionInfoRequest { get; init; }
    public bool SupportsSetVariable { get; init; }
    public bool SupportsFunctionBreakpoints { get; init; }
    public List<ExceptionBreakpointFilter>? ExceptionBreakpointFilters { get; init; }
}

public sealed record ExceptionBreakpointFilter
{
    public string Filter { get; init; } = "";
    public string Label { get; init; } = "";
    public bool Default { get; init; }
}

public sealed record InitializeArguments
{
    [JsonPropertyName("clientID")] public string ClientId { get; init; } = "radsharp";
    public string ClientName { get; init; } = "RADSharp IDE";
    [JsonPropertyName("adapterID")] public string AdapterId { get; init; } = "coreclr";
    public bool LinesStartAt1 { get; init; } = true;
    public bool ColumnsStartAt1 { get; init; } = true;
    public string PathFormat { get; init; } = "path";
    public bool SupportsVariableType { get; init; } = true;
    public bool SupportsRunInTerminalRequest { get; init; }
}

/// <summary>Arguments of the launch request. Program, Args, Cwd, Env and StopAtEntry are what netcoredbg reads.</summary>
public sealed record LaunchArguments
{
    public string Program { get; init; } = "";
    public List<string>? Args { get; init; }
    public string? Cwd { get; init; }
    public Dictionary<string, string?>? Env { get; init; }
    public bool StopAtEntry { get; init; }
    public bool? JustMyCode { get; init; }
}

public sealed record DapSourceBreakpoint
{
    public int Line { get; init; }
    public int? Column { get; init; }
    public string? Condition { get; init; }
    public string? HitCondition { get; init; }
    public string? LogMessage { get; init; }
}

public sealed record SetBreakpointsArguments
{
    public DapSource Source { get; init; } = new();
    public List<DapSourceBreakpoint> Breakpoints { get; init; } = new();
}

public sealed record DapBreakpoint
{
    public int? Id { get; init; }
    public bool Verified { get; init; }
    public string? Message { get; init; }
    public DapSource? Source { get; init; }
    public int? Line { get; init; }
    public int? Column { get; init; }
}

public sealed record SetBreakpointsResponse
{
    public List<DapBreakpoint> Breakpoints { get; init; } = new();
}

public sealed record SetExceptionBreakpointsArguments
{
    public List<string> Filters { get; init; } = new();
}

public sealed record ContinueArguments
{
    public int ThreadId { get; init; }
    public bool? SingleThread { get; init; }
}

public sealed record ContinueResponse
{
    public bool AllThreadsContinued { get; init; } = true;
}

/// <summary>Arguments shared by next, stepIn, stepOut and pause (they all take just a thread id).</summary>
public sealed record ThreadArguments
{
    public int ThreadId { get; init; }
}

public sealed record DebugThread
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
}

public sealed record ThreadsResponse
{
    public List<DebugThread> Threads { get; init; } = new();
}

public sealed record StackTraceArguments
{
    public int ThreadId { get; init; }
    public int? StartFrame { get; init; }
    public int? Levels { get; init; }
}

public sealed record StackFrame
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public DapSource? Source { get; init; }
    public int Line { get; init; }
    public int Column { get; init; }
    public int? EndLine { get; init; }
    public int? EndColumn { get; init; }
}

public sealed record StackTraceResponse
{
    public List<StackFrame> StackFrames { get; init; } = new();
    public int? TotalFrames { get; init; }
}

public sealed record ScopesArguments
{
    public int FrameId { get; init; }
}

public sealed record Scope
{
    public string Name { get; init; } = "";
    public int VariablesReference { get; init; }
    public bool Expensive { get; init; }
}

public sealed record ScopesResponse
{
    public List<Scope> Scopes { get; init; } = new();
}

public sealed record VariablesArguments
{
    public int VariablesReference { get; init; }
    public int? Start { get; init; }
    public int? Count { get; init; }
}

public sealed record Variable
{
    public string Name { get; init; } = "";
    public string Value { get; init; } = "";
    public string? Type { get; init; }
    public int VariablesReference { get; init; }
    public string? EvaluateName { get; init; }
}

public sealed record VariablesResponse
{
    public List<Variable> Variables { get; init; } = new();
}

public sealed record EvaluateArguments
{
    public string Expression { get; init; } = "";
    public int? FrameId { get; init; }
    public string? Context { get; init; }
}

public sealed record EvaluateResponse
{
    public string Result { get; init; } = "";
    public string? Type { get; init; }
    public int VariablesReference { get; init; }
}

public sealed record ExceptionInfoArguments
{
    public int ThreadId { get; init; }
}

public sealed record ExceptionDetails
{
    public string? Message { get; init; }
    public string? TypeName { get; init; }
    public string? FullTypeName { get; init; }
    public string? StackTrace { get; init; }
}

public sealed record ExceptionInfoResponse
{
    public string ExceptionId { get; init; } = "";
    public string? Description { get; init; }
    public string? BreakMode { get; init; }
    public ExceptionDetails? Details { get; init; }
}

public sealed record DisconnectArguments
{
    public bool? Restart { get; init; }
    public bool? TerminateDebuggee { get; init; }
}

// ---- events (also the .NET event argument types) ----

public sealed class StoppedEventArgs : EventArgs
{
    /// <summary>"step", "breakpoint", "exception", "pause", "entry", "goto", "function breakpoint", ...</summary>
    public string Reason { get; init; } = "";
    public string? Description { get; init; }
    public int? ThreadId { get; init; }
    public string? Text { get; init; }
    public bool AllThreadsStopped { get; init; }
    public List<int>? HitBreakpointIds { get; init; }
}

public sealed class ContinuedEventArgs : EventArgs
{
    public int ThreadId { get; init; }
    public bool AllThreadsContinued { get; init; } = true;
}

public sealed class ExitedEventArgs : EventArgs
{
    public int ExitCode { get; init; }
}

public sealed class TerminatedEventArgs : EventArgs
{
}

public sealed class OutputEventArgs : EventArgs
{
    /// <summary>"console", "stdout", "stderr", "important", "telemetry"; null means console.</summary>
    public string? Category { get; init; }
    public string Output { get; init; } = "";
    public DapSource? Source { get; init; }
    public int? Line { get; init; }
}

public sealed class BreakpointEventArgs : EventArgs
{
    /// <summary>"changed", "new" or "removed".</summary>
    public string Reason { get; init; } = "";
    public DapBreakpoint Breakpoint { get; init; } = new();
}

public sealed record DebugModule
{
    /// <summary>A number or a string on the wire, so kept raw.</summary>
    public JsonElement? Id { get; init; }
    public string Name { get; init; } = "";
    public string? Path { get; init; }
    public string? SymbolStatus { get; init; }
    public string? SymbolFilePath { get; init; }
    public bool? IsUserCode { get; init; }
}

public sealed class ModuleEventArgs : EventArgs
{
    /// <summary>"new", "changed" or "removed".</summary>
    public string Reason { get; init; } = "";
    public DebugModule Module { get; init; } = new();
}

public sealed class ThreadEventArgs : EventArgs
{
    /// <summary>"started" or "exited".</summary>
    public string Reason { get; init; } = "";
    public int ThreadId { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(DapCapabilities))]
[JsonSerializable(typeof(InitializeArguments))]
[JsonSerializable(typeof(LaunchArguments))]
[JsonSerializable(typeof(SetBreakpointsArguments))]
[JsonSerializable(typeof(SetBreakpointsResponse))]
[JsonSerializable(typeof(SetExceptionBreakpointsArguments))]
[JsonSerializable(typeof(ContinueArguments))]
[JsonSerializable(typeof(ContinueResponse))]
[JsonSerializable(typeof(ThreadArguments))]
[JsonSerializable(typeof(ThreadsResponse))]
[JsonSerializable(typeof(StackTraceArguments))]
[JsonSerializable(typeof(StackTraceResponse))]
[JsonSerializable(typeof(ScopesArguments))]
[JsonSerializable(typeof(ScopesResponse))]
[JsonSerializable(typeof(VariablesArguments))]
[JsonSerializable(typeof(VariablesResponse))]
[JsonSerializable(typeof(EvaluateArguments))]
[JsonSerializable(typeof(EvaluateResponse))]
[JsonSerializable(typeof(ExceptionInfoArguments))]
[JsonSerializable(typeof(ExceptionInfoResponse))]
[JsonSerializable(typeof(DisconnectArguments))]
[JsonSerializable(typeof(StoppedEventArgs))]
[JsonSerializable(typeof(ContinuedEventArgs))]
[JsonSerializable(typeof(ExitedEventArgs))]
[JsonSerializable(typeof(TerminatedEventArgs))]
[JsonSerializable(typeof(OutputEventArgs))]
[JsonSerializable(typeof(BreakpointEventArgs))]
[JsonSerializable(typeof(ModuleEventArgs))]
[JsonSerializable(typeof(ThreadEventArgs))]
public sealed partial class DapJson : JsonSerializerContext
{
}
