#if !EXCLUDE_D3D11_BACKEND
using System;
using NeoVeldrid.D3D11;

namespace NeoVeldrid;

/// <summary>
/// Immutable evidence describing one deferred texture upload recorded through
/// the forced software command-list source contract.
/// </summary>
public readonly struct D3D11DeferredTextureUploadPlaybackTrace
{
    /// <summary>
    /// Gets the playback path which consumed this upload.
    /// </summary>
    public D3D11DeferredTextureUploadPlaybackPath PlaybackPath { get; }

    /// <summary>
    /// Gets the unsigned byte delta from the rebased source address to the
    /// logical source address supplied by the caller.
    /// </summary>
    public ulong LogicalSourceMinusRebasedSource { get; }

    /// <summary>
    /// Gets the destination X coordinate used to construct the rebased source.
    /// </summary>
    public uint DestinationX { get; }

    /// <summary>
    /// Gets the destination Y coordinate used to construct the rebased source.
    /// </summary>
    public uint DestinationY { get; }

    /// <summary>
    /// Gets the destination Z coordinate used to construct the rebased source.
    /// </summary>
    public uint DestinationZ { get; }

    /// <summary>
    /// Gets whether the native call used an explicit destination box.
    /// </summary>
    public bool HasDestinationBox { get; }

    /// <summary>
    /// Gets the exclusive right edge of the explicit destination box, or zero
    /// when the whole subresource was selected.
    /// </summary>
    public uint DestinationBoxRight { get; }

    /// <summary>
    /// Gets the exclusive bottom edge of the explicit destination box, or zero
    /// when the whole subresource was selected.
    /// </summary>
    public uint DestinationBoxBottom { get; }

    /// <summary>
    /// Gets the exclusive back edge of the explicit destination box, or zero
    /// when the whole subresource was selected.
    /// </summary>
    public uint DestinationBoxBack { get; }

    /// <summary>
    /// Gets the source row pitch supplied to Direct3D.
    /// </summary>
    public uint RowPitch { get; }

    /// <summary>
    /// Gets the source depth pitch supplied to Direct3D.
    /// </summary>
    public uint DepthPitch { get; }

    /// <summary>
    /// Gets the upload's pixel format.
    /// </summary>
    public PixelFormat Format { get; }

    /// <summary>
    /// Gets whether the final playback adapter translated the rebased source
    /// for a native command-list-capable driver.
    /// </summary>
    public bool NativePlaybackTranslationApplied { get; }

    internal D3D11DeferredTextureUploadPlaybackTrace(
        D3D11DeferredTextureUploadPlaybackPath playbackPath,
        nuint logicalSourceMinusRebasedSource,
        uint destinationX,
        uint destinationY,
        uint destinationZ,
        bool hasDestinationBox,
        uint destinationBoxRight,
        uint destinationBoxBottom,
        uint destinationBoxBack,
        uint rowPitch,
        uint depthPitch,
        PixelFormat format,
        bool nativePlaybackTranslationApplied)
    {
        PlaybackPath = playbackPath;
        LogicalSourceMinusRebasedSource = checked(
            (ulong)logicalSourceMinusRebasedSource);
        DestinationX = destinationX;
        DestinationY = destinationY;
        DestinationZ = destinationZ;
        HasDestinationBox = hasDestinationBox;
        DestinationBoxRight = destinationBoxRight;
        DestinationBoxBottom = destinationBoxBottom;
        DestinationBoxBack = destinationBoxBack;
        RowPitch = rowPitch;
        DepthPitch = depthPitch;
        Format = format;
        NativePlaybackTranslationApplied = nativePlaybackTranslationApplied;
    }
}

/// <summary>
/// Exposes Direct3D 11-specific functionality,
/// useful for interoperating with native components which interface directly with Direct3D 11.
/// Can only be used on <see cref="GraphicsBackend.Direct3D11"/>.
/// </summary>
public unsafe class BackendInfoD3D11
{
    private readonly D3D11GraphicsDevice _gd;

    internal BackendInfoD3D11(D3D11GraphicsDevice gd)
    {
        _gd = gd;
    }

    /// <summary>
    /// Gets a pointer to the ID3D11Device controlled by the GraphicsDevice.
    /// </summary>
    public IntPtr Device => (nint)_gd.Device;

    /// <summary>
    /// Gets a pointer to the IAdapter used to create the GraphicsDevice.
    /// </summary>
    public IntPtr Adapter => (nint)_gd.Adapter;

    /// <summary>
    /// Gets the PCI ID of the hardware device.
    /// </summary>
    public int DeviceId => _gd.DeviceId;

    /// <summary>
    /// Gets whether the native Direct3D 11 driver reported command-list
    /// support through <c>D3D11_FEATURE_THREADING</c>.
    /// </summary>
    public bool DriverCommandListsSupported =>
        _gd.CommandListCapabilities.DriverSupportsCommandLists;

    /// <summary>
    /// Gets the deferred texture-upload mode selected when this device was
    /// created.
    /// </summary>
    public D3D11DeferredTextureUploadMode DeferredTextureUploadMode =>
        _gd.CommandListCapabilities.DeferredTextureUploadMode;

    /// <summary>
    /// Gets the authoritative deferred texture-upload playback path selected
    /// for this device.
    /// </summary>
    public D3D11DeferredTextureUploadPlaybackPath
        DeferredTextureUploadPlaybackPath =>
            _gd.CommandListCapabilities.TextureUploadExecutor.PlaybackPath;

    /// <summary>
    /// Gets whether deferred texture uploads use the source-rebasing contract
    /// required by software-emulated Direct3D 11 command lists.
    /// </summary>
    public bool UsesSoftwareCommandListTextureUploadPath =>
        _gd.CommandListCapabilities.TextureUploadExecutor
            .UsesSoftwareCommandListSourceContract;

    /// <summary>
    /// Gets whether the software command-list texture-upload contract was
    /// selected explicitly rather than by the native driver capability.
    /// </summary>
    public bool IsSoftwareCommandListTextureUploadPathForced =>
        _gd.CommandListCapabilities.TextureUploadExecutor
            .IsForcedSoftwareCommandListEmulation;

    /// <summary>
    /// Gets whether a native-capable driver translates the production
    /// software command-list source contract at the final playback boundary.
    /// </summary>
    public bool UsesNativeSoftwareRuntimePlaybackTranslation =>
        _gd.CommandListCapabilities.TextureUploadExecutor
            .UsesNativeSoftwareRuntimePlaybackTranslation;

    /// <summary>
    /// Gets the number of deferred texture uploads observed while the explicit
    /// forced qualification mode is active. Automatic devices keep this
    /// diagnostic disabled to avoid atomic work in the upload hot path.
    /// </summary>
    public ulong DeferredTextureUploadExecutionCount =>
        _gd.CommandListCapabilities.TextureUploadExecutor.ExecutionCount;

    /// <summary>
    /// Gets the number of forced-qualification texture uploads for which a
    /// nonzero software command-list source rebase was executed.
    /// </summary>
    public ulong DeferredTextureUploadRebaseCount =>
        _gd.CommandListCapabilities.TextureUploadExecutor.RebasedExecutionCount;

    /// <summary>
    /// Gets the number of forced-qualification texture uploads whose rebased
    /// sources were translated at the native playback boundary.
    /// </summary>
    public ulong NativeSoftwareRuntimePlaybackTranslationCount =>
        _gd.CommandListCapabilities.TextureUploadExecutor
            .NativePlaybackTranslationCount;

    /// <summary>
    /// Returns an immutable snapshot of every upload consumed through the
    /// explicit forced software command-list source contract.
    /// </summary>
    public D3D11DeferredTextureUploadPlaybackTrace[]
        GetDeferredTextureUploadPlaybackTraces() =>
            _gd.CommandListCapabilities.TextureUploadExecutor
                .GetForcedPlaybackTraces();

    /// <summary>
    /// Gets whether NeoVeldrid probed for the D3D11 SDK debug layers while
    /// creating this device.
    /// </summary>
    public bool DebugLayerWasProbed => _gd.DebugLayerProbeHResult.HasValue;

    /// <summary>
    /// Gets whether the D3D11 SDK-layer probe succeeded. This is false when the
    /// layer was unavailable or when no probe was needed.
    /// </summary>
    public bool DebugLayerProbeSucceeded =>
        _gd.DebugLayerProbeHResult is int result && result >= 0;

    /// <summary>
    /// Gets the native result of the D3D11 SDK-layer probe, or null when the
    /// debug layer was not requested and no debug-build probe was needed.
    /// </summary>
    public int? DebugLayerProbeHResult => _gd.DebugLayerProbeHResult;

    /// <summary>
    /// Gets whether this device was actually created with the D3D11 debug flag.
    /// </summary>
    public bool DebugDeviceWasCreated => _gd.DebugDeviceCreated;

    /// <summary>
    /// Gets whether an ID3D11InfoQueue was acquired and activated as the
    /// device's native validation-message transport.
    /// </summary>
    public bool ValidationInfoQueueWasActivated => _gd.ValidationInfoQueueActivated;

    /// <summary>
    /// Gets the latest backend-neutral validation status for this device.
    /// </summary>
    public GraphicsDeviceValidationStatus ValidationStatus => _gd.Validation.Status;

    /// <summary>
    /// Gets the number of native D3D11 validation messages copied into the
    /// device-owned validation history.
    /// </summary>
    public ulong CollectedValidationMessageCount => _gd.CollectedValidationMessageCount;

    /// <summary>
    /// Gets the cumulative number of native messages discarded because the
    /// ID3D11InfoQueue reached its capacity. Any nonzero value is reported as a
    /// validation error.
    /// </summary>
    public ulong DiscardedValidationMessageCount => _gd.DiscardedValidationMessageCount;

    /// <summary>
    /// Gets a pointer to the native texture wrapped by the given NeoVeldrid Texture. Depending on the instance's TextureType,
    /// this will be a pointer to an ID3D11Texture1D, an ID3D11Texture2D, or an ID3D11Texture3D.
    /// </summary>
    /// <returns>A pointer to the NeoVeldrid Texture's underlying ID3D11Texture1D, ID3D11Texture2D, or ID3D11Texture3D. The type
    /// of this object depends on the parameter's TextureType.</returns>
    public IntPtr GetTexturePointer(Texture texture)
        => (nint)Util.AssertSubtype<Texture, D3D11Texture>(texture).DeviceTexture;
}
#endif
