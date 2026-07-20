using System;

namespace NeoVeldrid;

/// <summary>
/// Selects how deferred Direct3D 11 texture uploads consume their source data.
/// </summary>
public enum D3D11DeferredTextureUploadMode
{
    /// <summary>
    /// Uses the capability reported by <c>D3D11_FEATURE_THREADING</c>.
    /// </summary>
    Automatic,

    /// <summary>
    /// Forces texture uploads through the software command-list source
    /// contract. Native-capable drivers translate the actual rebased source
    /// at the playback boundary so the same contract can be qualified safely.
    /// </summary>
    ForceSoftwareCommandListEmulation,
}

/// <summary>
/// Identifies the deferred Direct3D 11 texture-upload playback path selected
/// for a device.
/// </summary>
public enum D3D11DeferredTextureUploadPlaybackPath
{
    /// <summary>
    /// The driver consumes the logical source through its native command-list
    /// implementation.
    /// </summary>
    NativeCommandList,

    /// <summary>
    /// The Direct3D software command-list runtime consumes the rebased source.
    /// </summary>
    SoftwareCommandListRuntime,

    /// <summary>
    /// The explicit qualification mode consumes the rebased source contract.
    /// Native-capable drivers translate that source only at the final playback
    /// boundary.
    /// </summary>
    ForcedSoftwareCommandListRuntime,
}

/// <summary>
/// A structure describing Direct3D11-specific device creation options.
/// </summary>
public struct D3D11DeviceOptions
{
    /// <summary>
    /// Native pointer to an adapter.
    /// </summary>
    public IntPtr AdapterPtr;

    /// <summary>
    /// Set of device specific flags.
    /// See the Direct3D 11 DeviceCreationFlags documentation for details.
    /// </summary>
    public uint DeviceCreationFlags;

    /// <summary>
    /// Selects how deferred command-list texture uploads consume their source
    /// data.
    /// </summary>
    public D3D11DeferredTextureUploadMode DeferredTextureUploadMode;
}
