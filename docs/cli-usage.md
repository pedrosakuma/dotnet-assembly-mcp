# CLI usage — `dotnet-assembly-cli`

The same engine ships as a standalone terminal tool, **`dotnet-assembly-cli`**, for when *you*
(not an agent) want to navigate an assembly. It is a thin shell over the same orchestration the
MCP server uses — every MCP tool has a matching subcommand — but renders human-readable text by
default instead of an MCP envelope.

```bash
dotnet tool install -g dotnet-assembly-cli
dotnet-assembly-cli --help        # list every subcommand
dotnet-assembly-cli list-types --help
```

## A worked walkthrough

Start from a path, drill down to a method, then pivot through the call graph — the same
loop an agent runs, but readable in your terminal. Paths may be **relative or absolute** (and a
leading `~` is expanded) — the CLI resolves them against your current directory before handing an
absolute path to the engine.

```bash
DLL=./bin/Release/net10.0/MyLib.dll   # relative paths are fine

# 1. What's in here? (a path-taking command loads the module for you)
dotnet-assembly-cli list-types "$DLL"
#   25 type(s).
#   ...
#     FullName: SampleLib.OrderService
#     Handle:   t:b613bdf8-…:0x02000007

# 2. Find a method by name regex — gives you its (mvid, token) + handle
dotnet-assembly-cli find-method "$DLL" "Process"
#   3 match(es) for /Process/.
#     Handle:    m:b613bdf8-…:0x0600000D
#     Signature: int SampleLib.OrderService.Process(int)

# 3. Decompile it (--assembly loads the module before resolving the token)
dotnet-assembly-cli decompile-method b613bdf8-… 0x0600000D --assembly "$DLL"
#   SampleLib.OrderService.Process — 240 chars of C#.
#   Source:
#     public int Process(int orderId) { _counter++; … }

# 4. Who calls it? --load primes the index so the handle resolves. As a
#    recursive global option it can go before OR after the subcommand.
dotnet-assembly-cli find-callers b613bdf8-… 0x0600000D --load "$DLL"
#   1 caller(s) in 1 module (built).
#     Display: SampleLib.OrderService+<ProcessAsync>d__6.MoveNext

# Pipe any command through --json for the full MCP-shaped envelope
dotnet-assembly-cli find-callers b613bdf8-… 0x0600000D --load "$DLL" --json | jq '.Data.Callers'
```

> **The CLI is stateless per-invocation.** Each run builds a fresh index that is discarded on
> exit. There is therefore no standalone `load` or `list-assemblies` subcommand — they would have
> no meaning across processes — and everything must reach the index *within the same command line*:
> pass a path positional, a method command's `--assembly`, or one or more `--load <path>`.
> Cross-module queries (`find-callers`, `find-type-references`, `find-string-references`, …) only
> see the modules you loaded — their output reports the corpus size (`… in N module(s)`) so an
> empty result over `1 module` is your cue to `--load` the rest of the app.

## Shortcut: `explain-type` / `explain-method`

Steps 1–3 above chase a handle and a token by hand — fine for an agent, tedious for a human.
The two **composed** commands collapse that loop: give them an assembly plus a **type name**
(and optionally a **method name**) and they resolve everything internally.

```bash
# Whole-type overview in one shot: summary, attributes, members and methods grouped.
dotnet-assembly-cli explain-type "$DLL" SampleLib.OrderService

# A method by name — every overload, each with its source location (file:line via PDB).
dotnet-assembly-cli explain-method "$DLL" SampleLib.OrderService Process

# Add --decompile to print the C# body under each overload.
dotnet-assembly-cli explain-method "$DLL" SampleLib.OrderService Compute --decompile

# Who transitively calls a method? A recursive caller tree, resolved by name.
dotnet-assembly-cli callgraph "$DLL" SampleLib.OrderService Compute --depth 3

# What changed in the public surface between two builds of an assembly?
dotnet-assembly-cli diff-assemblies "$OLD_DLL" "$NEW_DLL"
```

`explain-method` matches the method name **exactly** by default (and lists near-misses if there
is none); pass `--contains` for substring matching. Both honour the global `--json` flag, which
emits the full `AssemblyResult` envelope instead of the human text view.

`callgraph` builds one tree per matched overload, drawing each method's (transitive) callers
across all loaded modules. Bound it with `--depth` (caller levels, default 3) and `--max-nodes`
(total nodes, default 200); nodes are marked `[cycle]` for recursion and `[more callers not
shown]` when the depth limit is reached. It is a MethodDef/IL call-path tree, so generic methods
appear once (not per closed instantiation).

`diff-assemblies` compares the **externally-visible public surface** of two assemblies (a type is
visible only when its whole declaring chain is public): types added / removed, and, for types in
both, public / protected members added / removed / signature-changed plus type-shape changes
(kind / base / interfaces). Member identity is name + generic arity + parameter list, so a
return-type, visibility or modifier (`static` / `virtual` / `abstract` / `sealed` / `readonly` /
`const`) change on the same signature is reported as a change rather than an add + remove.
Property / event accessors appear as their `get_` / `set_` / `add_` / `remove_` methods. Finding
differences still exits 0 (a diff is not an error); only an unreadable input assembly exits 1.
Type identity is compared by full name (signatures render type references by full name without
assembly identity), so a type that keeps its full name but moves to a different assembly — e.g. a
dependency version swap or type forward — is not flagged as a change.

## Subcommands

The CLI exposes 20 of the 22 MCP tools as 1:1 subcommands, plus four human-oriented composed
commands. The two stateful lifecycle tools (`load_assembly`, `list_assemblies`) are intentionally
omitted: in a one-shot CLI they have no standalone meaning (use the global `--load <path>` option
to prime the index instead).

| Group | Commands |
|---|---|
| **Lifecycle** | `import-manifest` |
| **Methods** | `get-method`, `decompile-method`, `decompile-type`, `get-method-il`, `list-methods`, `find-method`, `find-callers`, `get-method-source` |
| **Types** | `list-types`, `list-assembly-references`, `list-resources`, `list-attributes`, `get-type`, `list-derived-types`, `list-members` |
| **References** | `find-string-references`, `find-attribute-targets`, `find-member-references`, `find-type-references` |
| **Analysis (composed)** | `explain-type`, `explain-method`, `callgraph`, `diff-assemblies` |

Run `dotnet-assembly-cli <command> --help` for each command's arguments and options.

## Global options & exit codes

Two options are honoured by every subcommand:

| Option | Effect |
|---|---|
| `--json` | Emit the full `AssemblyResult` envelope as indented JSON (scriptable; identical to the MCP `data`). Without it, you get a human-readable rendering of the result. |
| `--load <path>` | Load an assembly (relative or absolute path) into this invocation's index before the command runs. Repeatable, and — being recursive — may appear before or after the subcommand. Because the CLI is one-shot, a handle (`m:<mvid>:0x…`) only resolves once its module is loaded, and cross-module queries only search loaded modules; `--load` (or a path-taking subcommand such as `find-method`, or a token command's `--assembly`) is how you prime the index. |

| Exit code | Meaning |
|---|---|
| `0` | Success. |
| `1` | The operation returned an error result (e.g. unknown MVID, absolute-path violation), or no command/an unknown command was given. The error message is printed to **stderr**. |
| `2` | Invalid argument value (e.g. an unparseable `--kind` / `--mode`). |

The architecture: a shared **`DotnetAssemblyMcp.Application`** project holds the tool orchestration;
the MCP `Server` and the `Cli` are both thin hosts over it, so the two never drift.
