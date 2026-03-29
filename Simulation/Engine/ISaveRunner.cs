using Simulation.World;

namespace Simulation.Engine;

/// <summary>
/// Runs save work asynchronously. Tests can substitute a controllable runner to
/// observe and complete saves deterministically.
/// </summary>
internal interface ISaveRunner
{
    void RunSave(WorldImage image, int tick, Action<WorldImage, int> saveAction);
}
