using Simulation.World;

namespace Simulation.Engine;

/// <summary>
/// Production save runner that dispatches save work to the thread pool.
/// </summary>
internal readonly struct TaskSaveRunner : ISaveRunner<WorldSnapshot>
{
    public void RunSave(WorldSnapshot node, int sequenceNumber, Action<WorldSnapshot, int> saveAction) =>
        Task.Run(() => saveAction(node, sequenceNumber));
}
