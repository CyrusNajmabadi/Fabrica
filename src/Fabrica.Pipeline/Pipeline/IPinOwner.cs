namespace Fabrica.Pipeline;

/// <summary>
/// Marker interface for types that are allowed to pin queue positions via <see cref="PinnedVersions"/>. Only deferred (slow)
/// consumers need pinning because the fast consumer (renderer) completes within a single frame and the hold-back model keeps its
/// entries alive. See <see cref="PinnedVersions"/> for the full pinning protocol.
/// </summary>
public interface IPinOwner;
