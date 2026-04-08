using System.Runtime.InteropServices;
using Fabrica.Core.Memory;

namespace Fabrica.SampleGame.Benchmarks.Scale;

[StructLayout(LayoutKind.Sequential)]
internal struct BenchNode
{
    public Handle<BenchNode> Child0;
    public Handle<BenchNode> Child1;
    public Handle<BenchNode> Child2;
    public Handle<BenchNode> Child3;
    public Handle<BenchNode> Child4;
    public Handle<BenchNode> Child5;
    public Handle<BenchNode> Child6;
    public Handle<BenchNode> Child7;
    public Handle<BenchNode> Next;
    public int Value;
}
