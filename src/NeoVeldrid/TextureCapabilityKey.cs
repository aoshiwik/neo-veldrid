using System;

namespace NeoVeldrid;

/// <summary>
/// A dimension-independent key for immutable backend texture capabilities.
/// Exact extents and counts are checked against the returned limits after this
/// capability has been queried. The two booleans preserve every qualitative
/// branch used by the backends while preventing authoring-time texture sizes
/// from growing the device cache without bound.
/// </summary>
internal readonly struct TextureCapabilityKey : IEquatable<TextureCapabilityKey>
{
    private readonly PixelFormat _format;
    private readonly TextureUsage _usage;
    private readonly TextureType _type;
    private readonly TextureSampleCount _sampleCount;
    private readonly bool _hasMultipleMipLevels;
    private readonly bool _hasMultipleArrayLayers;

    internal TextureCapabilityKey(in TextureDescription description)
    {
        _format = description.Format;
        _usage = description.Usage;
        _type = description.Type;
        _sampleCount = description.SampleCount;
        _hasMultipleMipLevels = description.MipLevels > 1;
        _hasMultipleArrayLayers = description.ArrayLayers > 1;
    }

    internal TextureDescription CreateCanonicalDescription()
    {
        uint dimension = _hasMultipleMipLevels ? 2u : 1u;
        uint height = _type == TextureType.Texture1D ? 1u : dimension;
        uint depth = _type == TextureType.Texture3D ? dimension : 1u;
        uint arrayLayers = _type == TextureType.Texture3D
            ? 1u
            : _hasMultipleArrayLayers ? 2u : 1u;
        return new TextureDescription(
            dimension,
            height,
            depth,
            _hasMultipleMipLevels ? 2u : 1u,
            arrayLayers,
            _format,
            _usage,
            _type,
            _sampleCount);
    }

    public bool Equals(TextureCapabilityKey other) =>
        _format == other._format
        && _usage == other._usage
        && _type == other._type
        && _sampleCount == other._sampleCount
        && _hasMultipleMipLevels == other._hasMultipleMipLevels
        && _hasMultipleArrayLayers == other._hasMultipleArrayLayers;

    public override bool Equals(object obj) =>
        obj is TextureCapabilityKey other && Equals(other);

    public override int GetHashCode() => HashHelper.Combine(
        (int)_format,
        (int)_usage,
        (int)_type,
        (int)_sampleCount,
        _hasMultipleMipLevels.GetHashCode(),
        _hasMultipleArrayLayers.GetHashCode());
}
