```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.3.1 (a) (25D771280a) [Darwin 25.3.0]
Apple M4 Max, 1 CPU, 16 logical and 16 physical cores
.NET SDK 10.0.102
  [Host]   : .NET 10.0.2 (10.0.2, 10.0.225.61305), Arm64 RyuJIT armv8.0-a
  ShortRun : .NET 10.0.2 (10.0.2, 10.0.225.61305), Arm64 RyuJIT armv8.0-a

Job=ShortRun  InvocationCount=1  IterationCount=3  
LaunchCount=1  UnrollFactor=1  WarmupCount=3  

```
| Method                    | N      | Mean        | Error      | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|-------------------------- |------- |------------:|-----------:|----------:|------:|--------:|----------:|------------:|
| **IncrementChildren_Direct**  | **1000**   |    **19.86 μs** |   **9.501 μs** |  **0.521 μs** |  **1.00** |    **0.03** |         **-** |          **NA** |
| IncrementChildren_Visitor | 1000   |    24.14 μs |  22.315 μs |  1.223 μs |  1.22 |    0.06 |         - |          NA |
| CascadeDecrement_Direct   | 1000   |   104.00 μs |  24.781 μs |  1.358 μs |  5.24 |    0.13 |   16416 B |          NA |
| CascadeDecrement_Visitor  | 1000   |   110.39 μs |  92.257 μs |  5.057 μs |  5.56 |    0.25 |   16416 B |          NA |
|                           |        |             |            |           |       |         |           |             |
| **IncrementChildren_Direct**  | **10000**  |    **47.92 μs** |  **29.366 μs** |  **1.610 μs** |  **1.00** |    **0.04** |         **-** |          **NA** |
| IncrementChildren_Visitor | 10000  |    48.11 μs |  47.276 μs |  2.591 μs |  1.00 |    0.06 |         - |          NA |
| CascadeDecrement_Direct   | 10000  |   147.07 μs |  99.965 μs |  5.479 μs |  3.07 |    0.13 |  262368 B |          NA |
| CascadeDecrement_Visitor  | 10000  |   135.32 μs |  52.883 μs |  2.899 μs |  2.83 |    0.10 |  262368 B |          NA |
|                           |        |             |            |           |       |         |           |             |
| **IncrementChildren_Direct**  | **100000** |   **216.25 μs** | **134.310 μs** |  **7.362 μs** |  **1.00** |    **0.04** |         **-** |          **NA** |
| IncrementChildren_Visitor | 100000 |   204.69 μs |   8.332 μs |  0.457 μs |  0.95 |    0.03 |         - |          NA |
| CascadeDecrement_Direct   | 100000 | 1,078.57 μs | 506.548 μs | 27.766 μs |  4.99 |    0.18 | 2097520 B |          NA |
| CascadeDecrement_Visitor  | 100000 | 1,035.10 μs | 521.374 μs | 28.578 μs |  4.79 |    0.18 | 2097520 B |          NA |
