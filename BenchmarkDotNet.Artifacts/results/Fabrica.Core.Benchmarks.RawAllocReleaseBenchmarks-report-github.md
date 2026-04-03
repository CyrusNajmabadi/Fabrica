```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.3.1 (a) (25D771280a) [Darwin 25.3.0]
Apple M4 Max, 1 CPU, 16 logical and 16 physical cores
.NET SDK 10.0.102
  [Host]   : .NET 10.0.2 (10.0.2, 10.0.225.61305), Arm64 RyuJIT armv8.0-a
  ShortRun : .NET 10.0.2 (10.0.2, 10.0.225.61305), Arm64 RyuJIT armv8.0-a

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                         | N       | Mean      | Error     | StdDev    | Ratio | RatioSD | Gen0      | Gen1      | Gen2      | Allocated | Alloc Ratio |
|------------------------------- |-------- |----------:|----------:|----------:|------:|--------:|----------:|----------:|----------:|----------:|------------:|
| **ArenaOnly_AllocThenFree**        | **100000**  |  **1.110 ms** | **0.1809 ms** | **0.0099 ms** |  **0.58** |    **0.01** |  **998.0469** |  **570.3125** |  **287.1094** |   **7.63 MB** |        **0.89** |
| ArenaAndRefCount_AllocThenFree | 100000  |  1.644 ms | 0.8244 ms | 0.0452 ms |  0.86 |    0.02 | 1097.6563 |  630.8594 |  427.7344 |   8.57 MB |        1.00 |
| ArenaAndRefCount_SteadyState   | 100000  |  1.916 ms | 0.6304 ms | 0.0346 ms |  1.00 |    0.02 | 1097.6563 |  632.8125 |  460.9375 |   8.57 MB |        1.00 |
|                                |         |           |           |           |       |         |           |           |           |           |             |
| **ArenaOnly_AllocThenFree**        | **1000000** |  **7.186 ms** | **1.8185 ms** | **0.0997 ms** |  **0.48** |    **0.01** | **8718.7500** | **4890.6250** | **1453.1250** |  **69.59 MB** |        **0.94** |
| ArenaAndRefCount_AllocThenFree | 1000000 | 12.315 ms | 6.7621 ms | 0.3707 ms |  0.82 |    0.02 | 9156.2500 | 5093.7500 | 1328.1250 |  73.96 MB |        1.00 |
| ArenaAndRefCount_SteadyState   | 1000000 | 14.940 ms | 0.7288 ms | 0.0400 ms |  1.00 |    0.00 | 9187.5000 | 5125.0000 | 1343.7500 |  73.96 MB |        1.00 |
