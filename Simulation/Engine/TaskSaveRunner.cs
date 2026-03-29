using Simulation.World;

namespace Simulation.Engine;

/// <summary>
/// Production save runner that dispatches save work to the thread pool.
/// </summary>
internal readonly struct TaskSaveRunner : ISaveRunner
{
    public void RunSave(WorldImage image, int tick, Action<WorldImage, int> saveAction)
    {
        Task.Run(() => saveAction(image, tick));
    }
}
