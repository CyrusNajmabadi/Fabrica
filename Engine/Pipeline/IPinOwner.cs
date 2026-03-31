namespace Engine.Pipeline;

/// <summary>
/// Marker interface for types that are allowed to pin chain-node sequence
/// numbers via <see cref="PinnedVersions"/>.  Only deferred (slow) consumers
/// need pinning because the fast consumer (renderer) completes within a
/// single frame and the epoch protects its nodes.
///
/// Constraining <see cref="PinnedVersions.Pin"/> and
/// <see cref="PinnedVersions.Unpin"/> to this interface ensures at the type
/// level that ad-hoc pinning is impossible — only registered deferred
/// consumers can participate.
/// </summary>
internal interface IPinOwner;
