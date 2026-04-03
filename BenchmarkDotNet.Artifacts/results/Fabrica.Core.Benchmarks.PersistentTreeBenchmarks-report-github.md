```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.3.1 (a) (25D771280a) [Darwin 25.3.0]
Apple M4 Max, 1 CPU, 16 logical and 16 physical cores
.NET SDK 10.0.102
  [Host]   : .NET 10.0.2 (10.0.2, 10.0.225.61305), Arm64 RyuJIT armv8.0-a
  ShortRun : .NET 10.0.2 (10.0.2, 10.0.225.61305), Arm64 RyuJIT armv8.0-a

Job=ShortRun  InvocationCount=1  IterationCount=3  
LaunchCount=1  UnrollFactor=1  WarmupCount=3  

```
| Method                      | TreeDepth | ChangeCount | Mean      | Error      | StdDev    | Ratio | RatioSD | Allocated   | Alloc Ratio |
|---------------------------- |---------- |------------ |----------:|-----------:|----------:|------:|--------:|------------:|------------:|
| **Interleaved_RandomLeaf**      | **19**        | **1000**        |  **1.281 ms** |  **0.7820 ms** | **0.0429 ms** |  **1.00** |    **0.04** |    **64.47 KB** |        **1.00** |
| Burst_AllChangesThenRelease | 19        | 1000        |  1.474 ms |  2.0380 ms | 0.1117 ms |  1.15 |    0.08 |    448.5 KB |        6.96 |
| Windowed_ProducerAheadBy100 | 19        | 1000        |  1.454 ms |  2.5150 ms | 0.1379 ms |  1.14 |    0.10 |    96.38 KB |        1.50 |
|                             |           |             |           |            |           |       |         |             |             |
| **Interleaved_RandomLeaf**      | **19**        | **10000**       |  **9.527 ms** |  **4.8276 ms** | **0.2646 ms** |  **1.00** |    **0.03** |    **64.47 KB** |        **1.00** |
| Burst_AllChangesThenRelease | 19        | 10000       | 11.161 ms | 20.0406 ms | 1.0985 ms |  1.17 |    0.10 |  3649.09 KB |       56.60 |
| Windowed_ProducerAheadBy100 | 19        | 10000       | 10.046 ms |  9.5084 ms | 0.5212 ms |  1.06 |    0.05 |    96.38 KB |        1.50 |
|                             |           |             |           |            |           |       |         |             |             |
| **Interleaved_RandomLeaf**      | **19**        | **50000**       | **43.131 ms** |  **7.0584 ms** | **0.3869 ms** |  **1.00** |    **0.01** |    **64.47 KB** |        **1.00** |
| Burst_AllChangesThenRelease | 19        | 50000       | 48.756 ms |  5.0008 ms | 0.2741 ms |  1.13 |    0.01 | 16067.43 KB |      249.23 |
| Windowed_ProducerAheadBy100 | 19        | 50000       | 46.429 ms | 35.0268 ms | 1.9199 ms |  1.08 |    0.04 |    96.38 KB |        1.50 |
