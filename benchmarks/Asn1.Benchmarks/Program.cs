using BenchmarkDotNet.Running;

// dotnet run -c Release --project benchmarks/Asn1.Benchmarks -- --filter '*'   (see docs/benchmarks.md)
var summaries = BenchmarkSwitcher.FromAssembly(typeof(Asn1.Benchmarks.CodecBenchmarks).Assembly).Run(args);

// A failing [GlobalSetup] check only marks its benchmark as failed; fail the process so that CI notices.
return summaries.Any(s => s.HasCriticalValidationErrors || s.Reports.Any(r => !r.Success)) ? 1 : 0;
