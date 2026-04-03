```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.3.1 (a) (25D771280a) [Darwin 25.3.0]
Apple M4 Max, 1 CPU, 16 logical and 16 physical cores
.NET SDK 10.0.102
  [Host]   : .NET 10.0.2 (10.0.2, 10.0.225.61305), Arm64 RyuJIT armv8.0-a
  ShortRun : .NET 10.0.2 (10.0.2, 10.0.225.61305), Arm64 RyuJIT armv8.0-a

Job=ShortRun  InvocationCount=1  IterationCount=3  
LaunchCount=1  UnrollFactor=1  WarmupCount=3  

```
| Method          | TreeDepth | N    | Mean       | Error      | StdDev   | Ratio | RatioSD | Allocated | Alloc Ratio |
|---------------- |---------- |----- |-----------:|-----------:|---------:|------:|--------:|----------:|------------:|
| Fork_Only       | 19        | 1000 |   355.8 μs | 1,043.6 μs | 57.20 μs |  1.02 |    0.21 | 192.37 KB |        1.00 |
| Release_Only    | 19        | 1000 |   999.0 μs |   376.5 μs | 20.63 μs |  2.86 |    0.44 | 256.13 KB |        1.33 |
| ForkThenRelease | 19        | 1000 | 1,133.0 μs |   624.3 μs | 34.22 μs |  3.25 |    0.50 |  64.47 KB |        0.34 |
