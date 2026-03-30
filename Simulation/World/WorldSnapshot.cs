namespace Simulation.World;

/// <summary>
/// A simulation-specific chain node.  Wraps one <see cref="WorldImage"/> and
/// inherits all chain mechanics (forward pointer, ref-counting, sequence number,
/// publish timestamp, bounded iterator) from <see cref="ChainNode{TSelf}"/>.
///
/// <see cref="TickNumber"/> is a domain alias for <see cref="ChainNode{TSelf}.SequenceNumber"/>.
/// </summary>
internal sealed class WorldSnapshot : ChainNode<WorldSnapshot>
{
    /// <summary>
    /// The underlying world state for this snapshot.  Contains simulation data
    /// (belt state, machine state, etc.) that the renderer reads during
    /// interpolation.
    /// </summary>
    public WorldImage Image { get; private set; } = null!;

    /// <summary>
    /// Domain alias for <see cref="ChainNode{TSelf}.SequenceNumber"/>.
    /// Tick 0 is the initial state created by Bootstrap.  Each call to
    /// <c>SimulationLoop.Tick</c> increments by 1.
    /// </summary>
    public int TickNumber => this.SequenceNumber;

    /// <summary>
    /// Prepares this snapshot for use after being rented from the pool.
    /// Delegates chain initialisation to <see cref="ChainNode{TSelf}.InitializeBase"/>
    /// and sets the domain-specific image.
    /// </summary>
    internal void Initialize(WorldImage image, int tickNumber)
    {
        this.InitializeBase(tickNumber);
        this.Image = image;
    }

    /// <inheritdoc />
    protected override void OnReleased() => this.Image = null!;
}
