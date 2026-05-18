# Spike: metadata library

Throwaway comparison of `Mono.Cecil`, `AsmResolver`, and `System.Reflection.Metadata`.
See `Spike.sln`; run with:

```sh
dotnet build -c Release
dotnet run -c Release --project src/Spike -- fixtures/SampleLib/bin/Release/net9.0/SampleLib.dll
```

The runner targets `net10.0`; the fixture targets `net9.0` on purpose — proves the
chosen lib reads assemblies from an older TFM (the retrocompat scenario that matters).

**Outcome:** see [`docs/handoff-contract.md`](../docs/handoff-contract.md) §
"Implementation notes" on `main`. Branch `spike/metadata-lib` is throwaway; the
decision survives in the doc.
