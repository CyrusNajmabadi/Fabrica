```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.3.1 (a) (25D771280a) [Darwin 25.3.0]
Apple M4 Max, 1 CPU, 16 logical and 16 physical cores
.NET SDK 10.0.102
  [Host]   : .NET 10.0.2 (10.0.2, 10.0.225.61305), Arm64 RyuJIT armv8.0-a
  ShortRun : .NET 10.0.2 (10.0.2, 10.0.225.61305), Arm64 RyuJIT armv8.0-a

Job=ShortRun  InvocationCount=1  IterationCount=3  
LaunchCount=1  UnrollFactor=1  WarmupCount=3  

```
| Method                      | TreeDepth | N    | Mean       | Error      | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|---------------------------- |---------- |----- |-----------:|-----------:|----------:|------:|--------:|----------:|------------:|
| Coordinator_ForkThenRelease | 19        | 1000 | 2,138.4 μs | 2,982.7 μs | 163.49 μs |  1.00 |    0.09 |   65712 B |        1.00 |
| Coordinator_MergeOnly       | 19        | 1000 | 1,393.0 μs | 1,199.8 μs |  65.77 μs |  0.65 |    0.05 |  196680 B |        2.99 |
| BufferFill_Only             | 19        | 1000 |   221.3 μs | 1,109.6 μs |  60.82 μs |  0.10 |    0.03 |         - |        0.00 |
