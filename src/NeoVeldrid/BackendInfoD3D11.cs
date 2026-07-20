#if !EXCLUDE_D3D11_BACKEND
using System;
using NeoVeldrid.D3D11;

namespace NeoVeldrid;

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
