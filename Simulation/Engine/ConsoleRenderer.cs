namespace Simulation.Engine;

/// <summary>
/// Temporary production renderer used until real rendering is implemented.
/// </summary>
internal readonly struct ConsoleRenderer : IRenderer
{
    public void Render(in RenderFrame frame)
    {
        // TODO: actual rendering and interpolation.
        Console.WriteLine($"[Render] tick={frame.Current.TickNumber} elapsed={frame.Interpolation.ElapsedNanoseconds}ns");
    }
}
