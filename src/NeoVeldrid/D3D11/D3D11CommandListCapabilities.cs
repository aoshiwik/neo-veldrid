using System;
namespace NeoVeldrid.D3D11;

/// <summary>
/// Owns the distinction between the driver's native command-list capability
/// and the compatibility behavior selected for this device.
/// </summary>
internal sealed class D3D11CommandListCapabilities
{
    public bool DriverSupportsCommandLists { get; }

    public bool RequiresSoftwareRuntimeWorkarounds =>
        !DriverSupportsCommandLists;

    public D3D11DeferredTextureUploadMode DeferredTextureUploadMode { get; }

    public D3D11DeferredTextureUploadExecutor TextureUploadExecutor { get; }

    public D3D11CommandListCapabilities(
        bool driverSupportsCommandLists,
        D3D11DeferredTextureUploadMode deferredTextureUploadMode)
    {
        if (deferredTextureUploadMode is not D3D11DeferredTextureUploadMode.Automatic
            and not D3D11DeferredTextureUploadMode.ForceSoftwareCommandListEmulation)
        {
            throw new ArgumentOutOfRangeException(
                nameof(deferredTextureUploadMode),
                deferredTextureUploadMode,
                "Unknown Direct3D 11 deferred texture-upload mode.");
        }

        DriverSupportsCommandLists = driverSupportsCommandLists;
        DeferredTextureUploadMode = deferredTextureUploadMode;
        TextureUploadExecutor = D3D11DeferredTextureUploadExecutor.Create(
            driverSupportsCommandLists,
            deferredTextureUploadMode);
    }
}
