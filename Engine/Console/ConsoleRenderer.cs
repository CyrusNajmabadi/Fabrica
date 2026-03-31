using Engine.Rendering;

namespace Engine.Console;

/// <summary>
/// Temporary production renderer used until real rendering is implemented.
/// </summary>
internal readonly struct ConsoleRenderer : IRenderer
{
    public void Render(in RenderFrame frame) =>
        global::System.Console.WriteLine($"[Render] tick={frame.Latest.SequenceNumber} elapsed={frame.Interpolation.ElapsedNanoseconds}ns");
}
