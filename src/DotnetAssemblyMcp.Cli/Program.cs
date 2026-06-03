using DotnetAssemblyMcp.Cli;

// dotnet-assembly-cli: a human-driven terminal front-end over the SAME Core engine the MCP
// server uses. Every subcommand maps to one AssemblyOperations call and renders the resulting
// AssemblyResult<T> envelope: human-readable text by default, or the full JSON envelope under
// the global --json flag. The CLI is one-shot, so use the repeatable global --load <path> to
// bring assemblies into the index before a handle-based subcommand runs.
return CliApplication.Run(args);
