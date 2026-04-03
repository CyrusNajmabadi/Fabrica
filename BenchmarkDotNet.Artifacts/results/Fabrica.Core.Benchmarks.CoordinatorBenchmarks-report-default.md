
BenchmarkDotNet v0.15.8, macOS Tahoe 26.3.1 (a) (25D771280a) [Darwin 25.3.0]
Apple M4 Max, 1 CPU, 16 logical and 16 physical cores
.NET SDK 10.0.102
  [Host]   : .NET 10.0.2 (10.0.2, 10.0.225.61305), Arm64 RyuJIT armv8.0-a
  ShortRun : .NET 10.0.2 (10.0.2, 10.0.225.61305), Arm64 RyuJIT armv8.0-a

Job=ShortRun  InvocationCount=1  IterationCount=3  
LaunchCount=1  UnrollFactor=1  WarmupCount=3  

 Method                      | TreeDepth | N    | Mean       | Error      | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
---------------------------- |---------- |----- |-----------:|-----------:|----------:|------:|--------:|----------:|------------:|
 Coordinator_ForkThenRelease | 19        | 1000 | 2,622.3 μs | 6,115.1 μs | 335.19 μs |  1.01 |    0.16 | 439.47 KB |        1.00 |
 Coordinator_MergeOnly       | 19        | 1000 | 1,663.3 μs | 2,072.0 μs | 113.57 μs |  0.64 |    0.08 | 536.12 KB |        1.22 |
 BufferFill_Only             | 19        | 1000 |   313.1 μs |   308.3 μs |  16.90 μs |  0.12 |    0.01 | 344.05 KB |        0.78 |
