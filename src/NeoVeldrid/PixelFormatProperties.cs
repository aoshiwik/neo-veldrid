namespace NeoVeldrid;

/// <summary>
/// Describes the properties that are supported for a particular combination of <see cref="PixelFormat"/>,
/// <see cref="TextureType"/>, and <see cref="TextureUsage"/> by a <see cref="GraphicsDevice"/>.
/// See <see cref="GraphicsDevice.GetTextureSupport(in TextureDescription)"/>.
/// </summary>
public struct PixelFormatProperties
{
    /// <summary>
    /// The maximum supported width.
    /// </summary>
    public readonly uint MaxWidth;
    /// <summary>
    /// The maximum supported height.
    /// </summary>
    public readonly uint MaxHeight;
    /// <summary>
    /// The maximum supported depth.
    /// </summary>
    public readonly uint MaxDepth;
    /// <summary>
    /// The maximum supported number of mipmap levels.
    /// </summary>
    public readonly uint MaxMipLevels;
    /// <summary>
    /// The maximum supported value of <see cref="TextureDescription.ArrayLayers"/>. For cubemaps, this is the number of
    /// complete cube maps rather than the backend's physical face-layer count.
    /// </summary>
    public readonly uint MaxArrayLayers;
    /// <summary>
    /// The backend-reported upper bound on the total size of one texture resource, in bytes. A value of
    /// <see cref="System.UInt64.MaxValue"/> means that the backend does not expose a separate resource-size limit through this
    /// query. This limit is distinct from the independently reported dimension and layer limits.
    /// </summary>
    public readonly ulong MaxResourceSizeInBytes;

    private readonly uint _sampleCounts;

    /// <summary>
    /// Gets a value indicating whether or not the given <see cref="TextureSampleCount"/> is supported.
    /// </summary>
    /// <param name="count">The <see cref="TextureSampleCount"/> to query.</param>
    /// <returns>True if the sample count is supported; false otherwise.</returns>
    public readonly bool IsSampleCountSupported(TextureSampleCount count)
    {
        int bit = (int)count;
        return (_sampleCounts & (1 << bit)) != 0;
    }

    internal PixelFormatProperties(
        uint maxWidth,
        uint maxHeight,
        uint maxDepth,
        uint maxMipLevels,
        uint maxArrayLayers,
        uint sampleCounts,
        ulong maxResourceSizeInBytes = ulong.MaxValue)
    {
        MaxWidth = maxWidth;
        MaxHeight = maxHeight;
        MaxDepth = maxDepth;
        MaxMipLevels = maxMipLevels;
        MaxArrayLayers = maxArrayLayers;
        _sampleCounts = sampleCounts;
        MaxResourceSizeInBytes = maxResourceSizeInBytes;
    }
}
