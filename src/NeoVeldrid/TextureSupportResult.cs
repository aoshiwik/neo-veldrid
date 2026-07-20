namespace NeoVeldrid;

/// <summary>
/// Identifies why a <see cref="TextureDescription"/> is or is not supported.
/// </summary>
public enum TextureSupportClassification : byte
{
    /// <summary>
    /// No query has initialized the result. This is the value of a default
    /// <see cref="TextureSupportResult"/> and never represents support.
    /// </summary>
    Uninitialized,
    /// <summary>
    /// The complete texture description is supported.
    /// </summary>
    Supported,
    /// <summary>
    /// The description is internally inconsistent or contains an invalid value.
    /// </summary>
    InvalidDescription,
    /// <summary>
    /// The description requests behavior that NeoVeldrid does not define.
    /// </summary>
    LibraryContract,
    /// <summary>
    /// The selected graphics backend does not implement the requested behavior.
    /// </summary>
    BackendContract,
    /// <summary>
    /// The selected graphics device does not expose the requested capability.
    /// </summary>
    DeviceCapability,
}

/// <summary>
/// Identifies the exact rule responsible for a texture support result.
/// </summary>
public enum TextureSupportReason : byte
{
    /// <summary>
    /// No support restriction applies.
    /// </summary>
    None,

    // Description invariants.
    UnknownPixelFormat,
    UnknownTextureType,
    UnknownTextureUsage,
    UnknownSampleCount,
    ZeroExtent,
    ZeroMipLevels,
    ZeroArrayLayers,
    Texture1DShape,
    Texture2DShape,
    Texture3DArrayLayers,
    CubemapTextureType,
    CubemapSquareFaces,
    CubemapMultisample,
    StagingUsageCombination,
    StagingMultisample,
    MultisampleTextureType,
    MultisampleMipLevels,
    LogicalMipChain,
    EffectiveArrayLayerOverflow,

    // NeoVeldrid-wide contracts.
    DepthStencilFormat,
    PackedDepthStencilUsage,
    PackedDepthStencilStaging,
    DepthStencilMipGeneration,
    StagingAddressLimit,

    // Backend and device capabilities.
    PixelFormat,
    TextureType,
    SampledUsage,
    StorageUsage,
    RenderTargetUsage,
    DepthStencilUsage,
    CubemapUsage,
    StagingUsage,
    NativeTextureImport,
    MipmapGeneration,
    WidthLimit,
    HeightLimit,
    DepthLimit,
    MipLevelLimit,
    ArrayLayerLimit,
    SampleCount,
    ResourceSizeLimit,
}

/// <summary>
/// Describes whether a complete <see cref="TextureDescription"/> is supported and, when it is not,
/// distinguishes invalid input from library, backend, and device capability boundaries.
/// </summary>
public readonly struct TextureSupportResult
{
    /// <summary>
    /// Gets whether the complete texture description is supported.
    /// </summary>
    public bool IsSupported => Classification == TextureSupportClassification.Supported;

    /// <summary>
    /// Gets the authority responsible for this result.
    /// </summary>
    public TextureSupportClassification Classification { get; }

    /// <summary>
    /// Gets the exact support rule that produced this result.
    /// </summary>
    public TextureSupportReason Reason { get; }

    /// <summary>
    /// Gets the device limits for this format, type, and usage combination when
    /// <see cref="IsSupported"/> is true.
    /// </summary>
    public PixelFormatProperties Properties { get; }

    internal TextureSupportResult(
        TextureSupportClassification classification,
        TextureSupportReason reason,
        PixelFormatProperties properties = default)
    {
        Classification = classification;
        Reason = reason;
        Properties = properties;
    }

    internal static TextureSupportResult Supported(
        PixelFormatProperties properties) =>
        new TextureSupportResult(
            TextureSupportClassification.Supported,
            TextureSupportReason.None,
            properties);

    internal static TextureSupportResult Unsupported(
        TextureSupportClassification classification,
        TextureSupportReason reason) =>
        new TextureSupportResult(classification, reason);

    /// <inheritdoc/>
    public override string ToString() => $"{Classification}/{Reason}";
}
