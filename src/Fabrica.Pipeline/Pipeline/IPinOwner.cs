namespace Fabrica.Pipeline;

/// <summary>
/// Marker interface for types that are allowed to pin chain-node sequence numbers via <see cref="PinnedVersions"/>. Only deferred
/// (slow) consumers need pinning because the fast consumer (renderer) completes within a single frame and the epoch protects its
/// nodes. See <see cref="PinnedVersions"/> for the full pinning protocol.
/// </summary>
public interface IPinOwner;
