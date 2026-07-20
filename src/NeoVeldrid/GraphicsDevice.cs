using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace NeoVeldrid;

/// <summary>
/// Represents an abstract graphics device, capable of creating device resources and executing commands.
/// </summary>
public abstract class GraphicsDevice : IDisposable
{
    private static readonly Action<CommandSubmissionBoundary> s_submitCommandsBoundary =
        static state => state.Device.SubmitCommandsAndCompleteDiagnostics(
            state.CommandList,
            state.Fence);
    private static readonly Action<SwapchainBoundary> s_swapBuffersBoundary =
        static state => state.Device.SwapBuffersCore(state.Swapchain);
    private static readonly Action<GraphicsDevice> s_waitForIdleBoundary =
        static device => device.WaitForIdleAndFlushDeferredDisposals();

    private readonly object _deferredDisposalLock = new object();
    private readonly Queue<IDisposable> _disposables = new Queue<IDisposable>();
    private DeferredDisposalQueueState _deferredDisposalState;
    private int _deferredDisposalDrainOwnerThreadId;
    private Sampler _aniso4xSampler;
    private int _disposeSignaled;
    private int _disposeOwnerThreadId;
    private bool _disposeCompleted;
    private ExceptionDispatchInfo _disposeFailure;
    private bool _deviceCreationComplete;
    private GraphicsDeviceValidation _validation;
    private Action _collectValidationMessages;

    internal GraphicsDevice() { }

    /// <summary>
    /// Gets the device-owned validation status and native-message history.
    /// </summary>
    public GraphicsDeviceValidation Validation => _validation
        ?? throw new InvalidOperationException("The graphics backend has not initialized validation diagnostics.");

    /// <summary>
    /// Initializes the single validation authority for this device.
    /// </summary>
    protected void InitializeValidation(GraphicsBackend backend, bool requested)
    {
        if (_validation != null)
        {
            throw new InvalidOperationException("Validation diagnostics have already been initialized.");
        }

        _validation = new GraphicsDeviceValidation(backend, requested);
        _collectValidationMessages = CollectValidationMessages;
    }

    /// <summary>
    /// Gets the name of the device.
    /// </summary>
    public abstract string DeviceName { get; }

    /// <summary>
    /// Gets the name of the device vendor.
    /// </summary>
    public abstract string VendorName { get; }

    /// <summary>
    /// Gets the API version of the graphics backend.
    /// </summary>
    public abstract GraphicsApiVersion ApiVersion { get; }

    /// <summary>
    /// Gets a value identifying the specific graphics API used by this instance.
    /// </summary>
    public abstract GraphicsBackend BackendType { get; }

    /// <summary>
    /// Gets a value identifying whether texture coordinates begin in the top left corner of a Texture.
    /// If true, (0, 0) refers to the top-left texel of a Texture. If false, (0, 0) refers to the bottom-left
    /// texel of a Texture. This property is useful for determining how the output of a Framebuffer should be sampled.
    /// </summary>
    public abstract bool IsUvOriginTopLeft { get; }

    /// <summary>
    /// Gets a value indicating whether this device's depth values range from 0 to 1.
    /// If false, depth values instead range from -1 to 1.
    /// </summary>
    public abstract bool IsDepthRangeZeroToOne { get; }

    /// <summary>
    /// Gets a value indicating whether this device's clip space Y values increase from top (-1) to bottom (1).
    /// If false, clip space Y values instead increase from bottom (-1) to top (1).
    /// </summary>
    public abstract bool IsClipSpaceYInverted { get; }

    /// <summary>
    /// Gets a value indicating whether debug mode was requested when this device was created, as specified by
    /// <see cref="GraphicsDeviceOptions.Debug"/>. A true value does not guarantee that debugging is actually
    /// active; see <see cref="IsDebugActive"/> for that.
    /// </summary>
    public bool IsDebugRequested => _validation?.Status.Requested ?? false;

    /// <summary>
    /// Gets a value indicating whether the graphics API's debug or validation facilities are actually active for
    /// this device. Unlike <see cref="IsDebugRequested"/>, this requires the backend, driver, and any needed SDK or
    /// validation layers to support and enable debugging, so it can be false even when debug mode was requested.
    /// </summary>
    public bool IsDebugActive => _validation?.Status.IsActive ?? false;

    internal bool RequiresValidationBoundary =>
        _validation?.RequiresBoundaryChecks ?? false;

    /// <summary>
    /// Gets the number of backend work items admitted ahead of the current
    /// execution point. Thread-affine backends override this for lifecycle
    /// diagnostics and deterministic concurrency tests.
    /// </summary>
    internal virtual int PendingBackendWorkItemCount => 0;

    /// <summary>
    /// Collects any backend-polled messages and fails if new native validation errors have been observed.
    /// </summary>
    /// <param name="boundary">A stable description of the lifecycle boundary being checked.</param>
    public IReadOnlyList<GraphicsDeviceValidationMessage> CheckValidation(string boundary)
    {
        if (IsDisposed)
        {
            throw new ObjectDisposedException(nameof(GraphicsDevice));
        }

        return CheckValidationCore(boundary);
    }

    private protected virtual IReadOnlyList<GraphicsDeviceValidationMessage> CheckValidationCore(
        string boundary) =>
        Validation.Checkpoint(boundary, _collectValidationMessages);

    /// <summary>
    /// Runs one native lifecycle operation and its validation checkpoint under
    /// the device-owned boundary lock. Operation and validation failures are
    /// preserved together instead of deferring evidence to another caller.
    /// </summary>
    internal virtual void ExecuteValidationBoundary<TState>(
        string boundary,
        TState state,
        Action<TState> operation) =>
        Validation.ExecuteBoundary(boundary, state, operation, _collectValidationMessages);

    /// <summary>
    /// Gives polling backends an opportunity to copy native diagnostics into <see cref="Validation"/>.
    /// Callback-based backends do not need to override this method.
    /// </summary>
    protected virtual void CollectValidationMessages()
    {
    }

    /// <summary>
    /// Gets the <see cref="ResourceFactory"/> controlled by this instance.
    /// </summary>
    public abstract ResourceFactory ResourceFactory { get; }

    /// <summary>
    /// Retrieves the main Swapchain for this device. This property is only valid if the device was created with a main
    /// Swapchain, and will return null otherwise.
    /// </summary>
    public abstract Swapchain MainSwapchain { get; }

    /// <summary>
    /// Gets a <see cref="GraphicsDeviceFeatures"/> which enumerates the optional features supported by this instance.
    /// </summary>
    public abstract GraphicsDeviceFeatures Features { get; }

    /// <summary>
    /// Gets or sets whether the main Swapchain's <see cref="SwapBuffers()"/> should be synchronized to the window system's
    /// vertical refresh rate.
    /// This is equivalent to <see cref="MainSwapchain"/>.<see cref="Swapchain.SyncToVerticalBlank"/>.
    /// This property cannot be set if this GraphicsDevice was created without a main Swapchain.
    /// </summary>
    public virtual bool SyncToVerticalBlank
    {
        get => MainSwapchain?.SyncToVerticalBlank ?? false;
        set
        {
            if (MainSwapchain == null)
            {
                throw new NeoVeldridException($"This GraphicsDevice was created without a main Swapchain. This property cannot be set.");
            }

            MainSwapchain.SyncToVerticalBlank = value;
        }
    }

    /// <summary>
    /// The required alignment, in bytes, for uniform buffer offsets. <see cref="DeviceBufferRange.Offset"/> must be a
    /// multiple of this value. When binding a <see cref="ResourceSet"/> to a <see cref="CommandList"/> with an overload
    /// accepting dynamic offsets, each offset must be a multiple of this value.
    /// </summary>
    public uint UniformBufferMinOffsetAlignment => GetUniformBufferMinOffsetAlignmentCore();

    /// <summary>
    /// The required alignment, in bytes, for structured buffer offsets. <see cref="DeviceBufferRange.Offset"/> must be a
    /// multiple of this value. When binding a <see cref="ResourceSet"/> to a <see cref="CommandList"/> with an overload
    /// accepting dynamic offsets, each offset must be a multiple of this value.
    /// </summary>
    public uint StructuredBufferMinOffsetAlignment => GetStructuredBufferMinOffsetAlignmentCore();

    internal abstract uint GetUniformBufferMinOffsetAlignmentCore();
    internal abstract uint GetStructuredBufferMinOffsetAlignmentCore();

    /// <summary>
    /// Submits the given <see cref="CommandList"/> for execution by this device.
    /// Commands submitted in this way may not be completed when this method returns.
    /// Use <see cref="WaitForIdle"/> to wait for all submitted commands to complete.
    /// <see cref="CommandList.End"/> must have been called on <paramref name="commandList"/> for this method to succeed.
    /// </summary>
    /// <param name="commandList">The completed <see cref="CommandList"/> to execute. <see cref="CommandList.End"/> must have
    /// been previously called on this object.</param>
    public void SubmitCommands(CommandList commandList)
    {
        if (RequiresValidationBoundary)
        {
            ExecuteValidationBoundary(
                "command submission",
                new CommandSubmissionBoundary(this, commandList, null),
                s_submitCommandsBoundary);
        }
        else
        {
            SubmitCommandsAndCompleteDiagnostics(commandList, null);
        }
    }

    /// <summary>
    /// Submits the given <see cref="CommandList"/> for execution by this device.
    /// Commands submitted in this way may not be completed when this method returns.
    /// Use <see cref="WaitForIdle"/> to wait for all submitted commands to complete.
    /// <see cref="CommandList.End"/> must have been called on <paramref name="commandList"/> for this method to succeed.
    /// </summary>
    /// <param name="commandList">The completed <see cref="CommandList"/> to execute. <see cref="CommandList.End"/> must have
    /// been previously called on this object.</param>
    /// <param name="fence">A <see cref="Fence"/> which will become signaled after this submission fully completes
    /// execution.</param>
    public void SubmitCommands(CommandList commandList, Fence fence)
    {
        if (RequiresValidationBoundary)
        {
            ExecuteValidationBoundary(
                "command submission",
                new CommandSubmissionBoundary(this, commandList, fence),
                s_submitCommandsBoundary);
        }
        else
        {
            SubmitCommandsAndCompleteDiagnostics(commandList, fence);
        }
    }

    private void SubmitCommandsAndCompleteDiagnostics(CommandList commandList, Fence fence)
    {
        SubmitCommandsCore(commandList, fence);
        // Native submission has committed at this point. Finalize managed
        // submission ownership even if the following validation checkpoint
        // reports an API error to the caller.
        commandList.CompleteSuccessfulSubmissionDiagnostics();
    }

    private protected abstract void SubmitCommandsCore(
        CommandList commandList,
        Fence fence);

    /// <summary>
    /// Blocks the calling thread until the given <see cref="Fence"/> becomes signaled.
    /// </summary>
    /// <param name="fence">The <see cref="Fence"/> instance to wait on.</param>
    public void WaitForFence(Fence fence)
    {
        if (!WaitForFence(fence, ulong.MaxValue))
        {
            throw new NeoVeldridException("The operation timed out before the Fence was signaled.");
        }
    }

    /// <summary>
    /// Blocks the calling thread until the given <see cref="Fence"/> becomes signaled, or until a time greater than the
    /// given TimeSpan has elapsed.
    /// </summary>
    /// <param name="fence">The <see cref="Fence"/> instance to wait on.</param>
    /// <param name="timeout">A TimeSpan indicating the maximum time to wait on the Fence.</param>
    /// <returns>True if the Fence was signaled. False if the timeout was reached instead.</returns>
    public bool WaitForFence(Fence fence, TimeSpan timeout)
        => WaitForFence(fence, (ulong)timeout.TotalMilliseconds * 1_000_000);
    /// <summary>
    /// Blocks the calling thread until the given <see cref="Fence"/> becomes signaled, or until a time greater than the
    /// given TimeSpan has elapsed.
    /// </summary>
    /// <param name="fence">The <see cref="Fence"/> instance to wait on.</param>
    /// <param name="nanosecondTimeout">A value in nanoseconds, indicating the maximum time to wait on the Fence.</param>
    /// <returns>True if the Fence was signaled. False if the timeout was reached instead.</returns>
    public abstract bool WaitForFence(Fence fence, ulong nanosecondTimeout);

    /// <summary>
    /// Blocks the calling thread until one or all of the given <see cref="Fence"/> instances have become signaled.
    /// </summary>
    /// <param name="fences">An array of <see cref="Fence"/> objects to wait on.</param>
    /// <param name="waitAll">If true, then this method blocks until all of the given Fences become signaled.
    /// If false, then this method only waits until one of the Fences become signaled.</param>
    public void WaitForFences(Fence[] fences, bool waitAll)
    {
        if (!WaitForFences(fences, waitAll, ulong.MaxValue))
        {
            throw new NeoVeldridException("The operation timed out before the Fence(s) were signaled.");
        }
    }

    /// <summary>
    /// Blocks the calling thread until one or all of the given <see cref="Fence"/> instances have become signaled,
    /// or until the given timeout has been reached.
    /// </summary>
    /// <param name="fences">An array of <see cref="Fence"/> objects to wait on.</param>
    /// <param name="waitAll">If true, then this method blocks until all of the given Fences become signaled.
    /// If false, then this method only waits until one of the Fences become signaled.</param>
    /// <param name="timeout">A TimeSpan indicating the maximum time to wait on the Fences.</param>
    /// <returns>True if the Fence was signaled. False if the timeout was reached instead.</returns>
    public bool WaitForFences(Fence[] fences, bool waitAll, TimeSpan timeout)
        => WaitForFences(fences, waitAll, (ulong)timeout.TotalMilliseconds * 1_000_000);

    /// <summary>
    /// Blocks the calling thread until one or all of the given <see cref="Fence"/> instances have become signaled,
    /// or until the given timeout has been reached.
    /// </summary>
    /// <param name="fences">An array of <see cref="Fence"/> objects to wait on.</param>
    /// <param name="waitAll">If true, then this method blocks until all of the given Fences become signaled.
    /// If false, then this method only waits until one of the Fences become signaled.</param>
    /// <param name="nanosecondTimeout">A value in nanoseconds, indicating the maximum time to wait on the Fence.  Pass ulong.MaxValue to wait indefinitely.</param>
    /// <returns>True if the Fence was signaled. False if the timeout was reached instead.</returns>
    public abstract bool WaitForFences(Fence[] fences, bool waitAll, ulong nanosecondTimeout);

    /// <summary>
    /// Resets the given <see cref="Fence"/> to the unsignaled state.
    /// </summary>
    /// <param name="fence">The <see cref="Fence"/> instance to reset.</param>
    public abstract void ResetFence(Fence fence);

    /// <summary>
    /// Swaps the buffers of the main swapchain and presents the rendered image to the screen.
    /// This is equivalent to passing <see cref="MainSwapchain"/> to <see cref="SwapBuffers(Swapchain)"/>.
    /// This method can only be called if this GraphicsDevice was created with a main Swapchain.
    /// </summary>
    public void SwapBuffers()
    {
        if (MainSwapchain == null)
        {
            throw new NeoVeldridException("This GraphicsDevice was created without a main Swapchain, so the requested operation cannot be performed.");
        }

        SwapBuffers(MainSwapchain);
    }

    /// <summary>
    /// Swaps the buffers of the given swapchain.
    /// </summary>
    /// <param name="swapchain">The <see cref="Swapchain"/> to swap and present.</param>
    public void SwapBuffers(Swapchain swapchain)
    {
        if (RequiresValidationBoundary)
        {
            ExecuteValidationBoundary(
                "presentation",
                new SwapchainBoundary(this, swapchain),
                s_swapBuffersBoundary);
        }
        else
        {
            SwapBuffersCore(swapchain);
        }
    }

    private protected abstract void SwapBuffersCore(Swapchain swapchain);

    /// <summary>
    /// Gets a <see cref="Framebuffer"/> object representing the render targets of the main swapchain.
    /// This is equivalent to <see cref="MainSwapchain"/>.<see cref="Swapchain.Framebuffer"/>.
    /// If this GraphicsDevice was created without a main Swapchain, then this returns null.
    /// </summary>
    public Framebuffer SwapchainFramebuffer => MainSwapchain?.Framebuffer;

    /// <summary>
    /// Notifies this instance that the main window has been resized. This causes the <see cref="SwapchainFramebuffer"/> to
    /// be appropriately resized and recreated.
    /// This is equivalent to calling <see cref="MainSwapchain"/>.<see cref="Swapchain.Resize(uint, uint)"/>.
    /// This method can only be called if this GraphicsDevice was created with a main Swapchain.
    /// </summary>
    /// <param name="width">The new width of the main window.</param>
    /// <param name="height">The new height of the main window.</param>
    public void ResizeMainWindow(uint width, uint height)
    {
        if (MainSwapchain == null)
        {
            throw new NeoVeldridException("This GraphicsDevice was created without a main Swapchain, so the requested operation cannot be performed.");
        }

        MainSwapchain.Resize(width, height);
    }

    /// <summary>
    /// A blocking method that returns when all submitted <see cref="CommandList"/> objects have fully completed.
    /// </summary>
    public void WaitForIdle() => ExecuteWaitForIdle(WaitForIdleTransaction);

    /// <summary>
    /// Gives a thread-affine backend ownership of the complete idle transaction,
    /// including deferred resource disposal. The default backend executes inline.
    /// </summary>
    private protected virtual void ExecuteWaitForIdle(Action waitForIdle) =>
        waitForIdle();

    private void WaitForIdleTransaction()
    {
        if (RequiresValidationBoundary)
        {
            ExecuteValidationBoundary("device idle", this, s_waitForIdleBoundary);
        }
        else
        {
            WaitForIdleAndFlushDeferredDisposals();
        }
    }

    private void WaitForIdleAndFlushDeferredDisposals()
    {
        WaitForIdleCore();
        FlushDeferredDisposals();
    }

    private protected abstract void WaitForIdleCore();

    /// <summary>
    /// Gets the maximum sample count supported by the given <see cref="PixelFormat"/>.
    /// </summary>
    /// <param name="format">The format to query.</param>
    /// <param name="depthFormat">Whether the format will be used in a depth texture.</param>
    /// <returns>A <see cref="TextureSampleCount"/> value representing the maximum count that a <see cref="Texture"/> of that
    /// format can be created with.</returns>
    public abstract TextureSampleCount GetSampleCountLimit(PixelFormat format, bool depthFormat);

    /// <summary>
    /// Maps a <see cref="DeviceBuffer"/> or <see cref="Texture"/> into a CPU-accessible data region. For Texture resources, this
    /// overload maps the first subresource.
    /// </summary>
    /// <param name="resource">The <see cref="DeviceBuffer"/> or <see cref="Texture"/> resource to map.</param>
    /// <param name="mode">The <see cref="MapMode"/> to use.</param>
    /// <returns>A <see cref="MappedResource"/> structure describing the mapped data region.</returns>
    public MappedResource Map(MappableResource resource, MapMode mode) => Map(resource, mode, 0);
    /// <summary>
    /// Maps a <see cref="DeviceBuffer"/> or <see cref="Texture"/> into a CPU-accessible data region.
    /// </summary>
    /// <param name="resource">The <see cref="DeviceBuffer"/> or <see cref="Texture"/> resource to map.</param>
    /// <param name="mode">The <see cref="MapMode"/> to use.</param>
    /// <param name="subresource">The subresource to map. Subresources are indexed first by mip slice, then by array layer.
    /// For <see cref="DeviceBuffer"/> resources, this parameter must be 0.</param>
    /// <returns>A <see cref="MappedResource"/> structure describing the mapped data region.</returns>
    public MappedResource Map(MappableResource resource, MapMode mode, uint subresource)
    {
#if VALIDATE_USAGE
        if (resource is DeviceBuffer buffer)
        {
            if ((buffer.Usage & BufferUsage.Dynamic) != BufferUsage.Dynamic
                && (buffer.Usage & BufferUsage.Staging) != BufferUsage.Staging)
            {
                throw new NeoVeldridException("Buffers must have the Staging or Dynamic usage flag to be mapped.");
            }
            if (subresource != 0)
            {
                throw new NeoVeldridException("Subresource must be 0 for Buffer resources.");
            }
            if ((mode == MapMode.Read || mode == MapMode.ReadWrite) && (buffer.Usage & BufferUsage.Staging) == 0)
            {
                throw new NeoVeldridException(
                    $"{nameof(MapMode)}.{nameof(MapMode.Read)} and {nameof(MapMode)}.{nameof(MapMode.ReadWrite)} can only be used on buffers created with {nameof(BufferUsage)}.{nameof(BufferUsage.Staging)}.");
            }
        }
        else if (resource is Texture tex)
        {
            if ((tex.Usage & TextureUsage.Staging) == 0)
            {
                throw new NeoVeldridException("Texture must have the Staging usage flag to be mapped.");
            }
            if (subresource >= tex.ArrayLayers * tex.MipLevels)
            {
                throw new NeoVeldridException(
                    "Subresource must be less than the number of subresources in the Texture being mapped.");
            }
        }
#endif

        return MapCore(resource, mode, subresource);
    }

    /// <summary>
    /// </summary>
    /// <param name="resource"></param>
    /// <param name="mode"></param>
    /// <param name="subresource"></param>
    /// <returns></returns>
    protected abstract MappedResource MapCore(MappableResource resource, MapMode mode, uint subresource);

    /// <summary>
    /// Maps a <see cref="DeviceBuffer"/> or <see cref="Texture"/> into a CPU-accessible data region, and returns a structured
    /// view over that region. For Texture resources, this overload maps the first subresource.
    /// </summary>
    /// <param name="resource">The <see cref="DeviceBuffer"/> or <see cref="Texture"/> resource to map.</param>
    /// <param name="mode">The <see cref="MapMode"/> to use.</param>
    /// <typeparam name="T">The blittable value type which mapped data is viewed as.</typeparam>
    /// <returns>A <see cref="MappedResource"/> structure describing the mapped data region.</returns>
    public MappedResourceView<T> Map<T>(MappableResource resource, MapMode mode) where T : unmanaged
        => Map<T>(resource, mode, 0);
    /// <summary>
    /// Maps a <see cref="DeviceBuffer"/> or <see cref="Texture"/> into a CPU-accessible data region, and returns a structured
    /// view over that region.
    /// </summary>
    /// <param name="resource">The <see cref="DeviceBuffer"/> or <see cref="Texture"/> resource to map.</param>
    /// <param name="mode">The <see cref="MapMode"/> to use.</param>
    /// <param name="subresource">The subresource to map. Subresources are indexed first by mip slice, then by array layer.</param>
    /// <typeparam name="T">The blittable value type which mapped data is viewed as.</typeparam>
    /// <returns>A <see cref="MappedResource"/> structure describing the mapped data region.</returns>
    public MappedResourceView<T> Map<T>(MappableResource resource, MapMode mode, uint subresource) where T : unmanaged
    {
        MappedResource mappedResource = Map(resource, mode, subresource);
        return new MappedResourceView<T>(mappedResource);
    }

    /// <summary>
    /// Invalidates a previously-mapped data region for the given <see cref="DeviceBuffer"/> or <see cref="Texture"/>.
    /// For <see cref="Texture"/> resources, this unmaps the first subresource.
    /// </summary>
    /// <param name="resource">The resource to unmap.</param>
    public void Unmap(MappableResource resource) => Unmap(resource, 0);
    /// <summary>
    /// Invalidates a previously-mapped data region for the given <see cref="DeviceBuffer"/> or <see cref="Texture"/>.
    /// </summary>
    /// <param name="resource">The resource to unmap.</param>
    /// <param name="subresource">The subresource to unmap. Subresources are indexed first by mip slice, then by array layer.
    /// For <see cref="DeviceBuffer"/> resources, this parameter must be 0.</param>
    public void Unmap(MappableResource resource, uint subresource)
    {
        UnmapCore(resource, subresource);
    }

    /// <summary>
    /// </summary>
    /// <param name="resource"></param>
    /// <param name="subresource"></param>
    protected abstract void UnmapCore(MappableResource resource, uint subresource);

    /// <summary>
    /// Updates a portion of a <see cref="Texture"/> resource with new data.
    /// </summary>
    /// <param name="texture">The resource to update.</param>
    /// <param name="source">A pointer to the start of the data to upload. This must point to tightly-packed pixel data for
    /// the region specified.</param>
    /// <param name="sizeInBytes">The number of bytes to upload. This value must match the total size of the texture region
    /// specified.</param>
    /// <param name="x">The minimum X value of the updated region.</param>
    /// <param name="y">The minimum Y value of the updated region.</param>
    /// <param name="z">The minimum Z value of the updated region.</param>
    /// <param name="width">The width of the updated region, in texels.</param>
    /// <param name="height">The height of the updated region, in texels.</param>
    /// <param name="depth">The depth of the updated region, in texels.</param>
    /// <param name="mipLevel">The mipmap level to update. Must be less than the total number of mipmaps contained in the
    /// <see cref="Texture"/>.</param>
    /// <param name="arrayLayer">The array layer to update. Must be less than the total array layer count contained in the
    /// <see cref="Texture"/>.</param>
    public void UpdateTexture(
        Texture texture,
        IntPtr source,
        uint sizeInBytes,
        uint x, uint y, uint z,
        uint width, uint height, uint depth,
        uint mipLevel, uint arrayLayer)
    {
#if VALIDATE_USAGE
        ValidateUpdateTextureParameters(texture, sizeInBytes, x, y, z, width, height, depth, mipLevel, arrayLayer);
#endif
        UpdateTextureCore(texture, source, sizeInBytes, x, y, z, width, height, depth, mipLevel, arrayLayer);
    }

    /// <summary>
    /// Updates a portion of a <see cref="Texture"/> resource with new data contained in an array
    /// </summary>
    /// <param name="texture">The resource to update.</param>
    /// <param name="source">An array containing the data to upload. This must contain tightly-packed pixel data for the
    /// region specified.</param>
    /// <param name="x">The minimum X value of the updated region.</param>
    /// <param name="y">The minimum Y value of the updated region.</param>
    /// <param name="z">The minimum Z value of the updated region.</param>
    /// <param name="width">The width of the updated region, in texels.</param>
    /// <param name="height">The height of the updated region, in texels.</param>
    /// <param name="depth">The depth of the updated region, in texels.</param>
    /// <param name="mipLevel">The mipmap level to update. Must be less than the total number of mipmaps contained in the
    /// <see cref="Texture"/>.</param>
    /// <param name="arrayLayer">The array layer to update. Must be less than the total array layer count contained in the
    /// <see cref="Texture"/>.</param>
    public void UpdateTexture<T>(
        Texture texture,
        T[] source,
        uint x, uint y, uint z,
        uint width, uint height, uint depth,
        uint mipLevel, uint arrayLayer) where T : unmanaged
    {
        UpdateTexture(texture, (ReadOnlySpan<T>)source, x, y, z, width, height, depth, mipLevel, arrayLayer);
    }

    /// <summary>
    /// Updates a portion of a <see cref="Texture"/> resource with new data contained in an array
    /// </summary>
    /// <param name="texture">The resource to update.</param>
    /// <param name="source">A readonly span containing the data to upload. This must contain tightly-packed pixel data for the
    /// region specified.</param>
    /// <param name="x">The minimum X value of the updated region.</param>
    /// <param name="y">The minimum Y value of the updated region.</param>
    /// <param name="z">The minimum Z value of the updated region.</param>
    /// <param name="width">The width of the updated region, in texels.</param>
    /// <param name="height">The height of the updated region, in texels.</param>
    /// <param name="depth">The depth of the updated region, in texels.</param>
    /// <param name="mipLevel">The mipmap level to update. Must be less than the total number of mipmaps contained in the
    /// <see cref="Texture"/>.</param>
    /// <param name="arrayLayer">The array layer to update. Must be less than the total array layer count contained in the
    /// <see cref="Texture"/>.</param>
    public unsafe void UpdateTexture<T>(
        Texture texture,
        ReadOnlySpan<T> source,
        uint x, uint y, uint z,
        uint width, uint height, uint depth,
        uint mipLevel, uint arrayLayer) where T : unmanaged
    {
        uint sizeInBytes = (uint)(sizeof(T) * source.Length);
#if VALIDATE_USAGE
        ValidateUpdateTextureParameters(texture, sizeInBytes, x, y, z, width, height, depth, mipLevel, arrayLayer);
#endif

        fixed (void* pin = &MemoryMarshal.GetReference(source))
        {
            UpdateTextureCore(
            texture,
            (IntPtr)pin,
            sizeInBytes,
            x, y, z,
            width, height, depth,
            mipLevel, arrayLayer);
        }
    }

    /// <summary>
    /// Updates a portion of a <see cref="Texture"/> resource with new data contained in an array
    /// </summary>
    /// <param name="texture">The resource to update.</param>
    /// <param name="source">A readonly span containing the data to upload. This must contain tightly-packed pixel data for the
    /// region specified.</param>
    /// <param name="x">The minimum X value of the updated region.</param>
    /// <param name="y">The minimum Y value of the updated region.</param>
    /// <param name="z">The minimum Z value of the updated region.</param>
    /// <param name="width">The width of the updated region, in texels.</param>
    /// <param name="height">The height of the updated region, in texels.</param>
    /// <param name="depth">The depth of the updated region, in texels.</param>
    /// <param name="mipLevel">The mipmap level to update. Must be less than the total number of mipmaps contained in the
    /// <see cref="Texture"/>.</param>
    /// <param name="arrayLayer">The array layer to update. Must be less than the total array layer count contained in the
    /// <see cref="Texture"/>.</param>
    public void UpdateTexture<T>(
        Texture texture,
        Span<T> source,
        uint x, uint y, uint z,
        uint width, uint height, uint depth,
        uint mipLevel, uint arrayLayer) where T : unmanaged
    {
        UpdateTexture(texture, (ReadOnlySpan<T>)source, x, y, z, width, height, depth, mipLevel, arrayLayer);
    }

    private protected abstract void UpdateTextureCore(
        Texture texture,
        IntPtr source,
        uint sizeInBytes,
        uint x, uint y, uint z,
        uint width, uint height, uint depth,
        uint mipLevel, uint arrayLayer);

    internal enum TextureUpdateValidationMode
    {
        /// <summary>
        /// The immediate device API consumes the leading bytes required by
        /// the region. Existing callers may supply a larger backing array or
        /// a block-padded compressed edge region.
        /// </summary>
        GraphicsDeviceCompatible,

        /// <summary>
        /// A command-list upload copies and retains the supplied payload, so
        /// it requires one exact logical region with no unused trailing data.
        /// </summary>
        CommandListExact,
    }

    internal static void ValidateUpdateTextureParameters(
        Texture texture,
        uint sizeInBytes,
        uint x, uint y, uint z,
        uint width, uint height, uint depth,
        uint mipLevel, uint arrayLayer,
        TextureUpdateValidationMode validationMode =
            TextureUpdateValidationMode.GraphicsDeviceCompatible)
    {
        ArgumentNullException.ThrowIfNull(texture);
        if (texture.IsDisposed)
            throw new ObjectDisposedException(nameof(texture));
        if (width == 0u || height == 0u || depth == 0u)
        {
            throw new NeoVeldridException(
                "Texture update dimensions must all be greater than zero.");
        }
        if (mipLevel >= texture.MipLevels)
        {
            throw new NeoVeldridException(
                $"{nameof(mipLevel)} ({mipLevel}) must be less than the Texture's mip level count ({texture.MipLevels}).");
        }

        uint effectiveArrayLayers;
        try
        {
            effectiveArrayLayers =
                (texture.Usage & TextureUsage.Cubemap) != 0
                    ? checked(texture.ArrayLayers * 6u)
                    : texture.ArrayLayers;
        }
        catch (OverflowException exception)
        {
            throw new NeoVeldridException(
                "The Texture's effective array layer count exceeds UInt32.MaxValue.",
                exception);
        }
        if (arrayLayer >= effectiveArrayLayers)
        {
            throw new NeoVeldridException(
                $"{nameof(arrayLayer)} ({arrayLayer}) must be less than the Texture's effective array layer count ({effectiveArrayLayers}).");
        }

        Util.GetMipDimensions(
            texture,
            mipLevel,
            out uint mipWidth,
            out uint mipHeight,
            out uint mipDepth);
        bool isCompressed =
            FormatHelpers.IsCompressedFormat(texture.Format);
        ulong storageWidth = mipWidth;
        ulong storageHeight = mipHeight;
        if (isCompressed &&
            validationMode ==
                TextureUpdateValidationMode.GraphicsDeviceCompatible)
        {
            // Preserve the immediate API's established block-storage
            // vocabulary for tiny compressed mips. The command-list API uses
            // logical extents because those extents are recorded directly in
            // a backend copy command.
            storageWidth = RoundUpToBlockExtent(mipWidth);
            storageHeight = RoundUpToBlockExtent(mipHeight);
        }

        if ((ulong)x + width > storageWidth ||
            (ulong)y + height > storageHeight ||
            (ulong)z + depth > mipDepth)
        {
            throw new NeoVeldridException(
                "The given region does not fit into the selected Texture mip level.");
        }

        if (isCompressed)
        {
            const uint blockExtent = 4u;
            bool widthReachesMipEdge = x + width == mipWidth;
            bool heightReachesMipEdge = y + height == mipHeight;
            if (x % blockExtent != 0u ||
                y % blockExtent != 0u ||
                (width % blockExtent != 0u && !widthReachesMipEdge) ||
                (height % blockExtent != 0u && !heightReachesMipEdge))
            {
                throw new NeoVeldridException(
                    "Updates to block-compressed textures must use block-aligned offsets and block-sized extents except at the selected mip level's edge.");
            }
        }

        uint expectedSize = CalculateTextureUpdateSize(
            width,
            height,
            depth,
            texture.Format);
        if (validationMode ==
                TextureUpdateValidationMode.CommandListExact &&
            sizeInBytes != expectedSize)
        {
            throw new NeoVeldridException(
                $"The data size must exactly match the given update region. Expected {expectedSize} bytes, but {sizeInBytes} were provided.");
        }
        if (validationMode ==
                TextureUpdateValidationMode.GraphicsDeviceCompatible &&
            sizeInBytes < expectedSize)
        {
            throw new NeoVeldridException(
                $"The data size is less than expected for the given update region. At least {expectedSize} bytes must be provided, but only {sizeInBytes} were.");
        }
    }

    private static ulong RoundUpToBlockExtent(uint value)
    {
        const ulong blockExtent = 4u;
        return ((ulong)value + blockExtent - 1u) /
            blockExtent * blockExtent;
    }

    private static uint CalculateTextureUpdateSize(
        uint width,
        uint height,
        uint depth,
        PixelFormat format)
    {
        ulong byteCount;
        try
        {
            if (FormatHelpers.IsCompressedFormat(format))
            {
                const ulong blockExtent = 4u;
                ulong blockColumns = ((ulong)width + blockExtent - 1u) / blockExtent;
                ulong blockRows = ((ulong)height + blockExtent - 1u) / blockExtent;
                byteCount = checked(
                    blockColumns *
                    blockRows *
                    depth *
                    FormatHelpers.GetBlockSizeInBytes(format));
            }
            else
            {
                byteCount = checked(
                    (ulong)width *
                    height *
                    depth *
                    FormatSizeHelpers.GetSizeInBytes(format));
            }
        }
        catch (OverflowException exception)
        {
            throw new NeoVeldridException(
                "The texture update byte count exceeds UInt64.MaxValue.",
                exception);
        }

        if (byteCount > uint.MaxValue)
        {
            throw new NeoVeldridException(
                "A single texture update cannot exceed UInt32.MaxValue bytes.");
        }

        return (uint)byteCount;
    }

    /// <summary>
    /// Updates a <see cref="DeviceBuffer"/> region with new data.
    /// This function must be used with a blittable value type <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The type of data to upload.</typeparam>
    /// <param name="buffer">The resource to update.</param>
    /// <param name="bufferOffsetInBytes">An offset, in bytes, from the beginning of the <see cref="DeviceBuffer"/> storage, at
    /// which new data will be uploaded.</param>
    /// <param name="source">The value to upload.</param>
    public unsafe void UpdateBuffer<T>(
        DeviceBuffer buffer,
        uint bufferOffsetInBytes,
        T source) where T : unmanaged
    {
        ref byte sourceByteRef = ref Unsafe.AsRef<byte>(Unsafe.AsPointer(ref source));
        fixed (byte* ptr = &sourceByteRef)
        {
            UpdateBuffer(buffer, bufferOffsetInBytes, (IntPtr)ptr, (uint)sizeof(T));
        }
    }

    /// <summary>
    /// Updates a <see cref="DeviceBuffer"/> region with new data.
    /// This function must be used with a blittable value type <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The type of data to upload.</typeparam>
    /// <param name="buffer">The resource to update.</param>
    /// <param name="bufferOffsetInBytes">An offset, in bytes, from the beginning of the <see cref="DeviceBuffer"/>'s storage, at
    /// which new data will be uploaded.</param>
    /// <param name="source">A reference to the single value to upload.</param>
    public unsafe void UpdateBuffer<T>(
        DeviceBuffer buffer,
        uint bufferOffsetInBytes,
        ref T source) where T : unmanaged
    {
        ref byte sourceByteRef = ref Unsafe.AsRef<byte>(Unsafe.AsPointer(ref source));
        fixed (byte* ptr = &sourceByteRef)
        {
            UpdateBuffer(buffer, bufferOffsetInBytes, (IntPtr)ptr, (uint)sizeof(T));
        }
    }

    /// <summary>
    /// Updates a <see cref="DeviceBuffer"/> region with new data.
    /// This function must be used with a blittable value type <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The type of data to upload.</typeparam>
    /// <param name="buffer">The resource to update.</param>
    /// <param name="bufferOffsetInBytes">An offset, in bytes, from the beginning of the <see cref="DeviceBuffer"/>'s storage, at
    /// which new data will be uploaded.</param>
    /// <param name="source">A reference to the first of a series of values to upload.</param>
    /// <param name="sizeInBytes">The total size of the uploaded data, in bytes.</param>
    public unsafe void UpdateBuffer<T>(
        DeviceBuffer buffer,
        uint bufferOffsetInBytes,
        ref T source,
        uint sizeInBytes) where T : unmanaged
    {
        ref byte sourceByteRef = ref Unsafe.AsRef<byte>(Unsafe.AsPointer(ref source));
        fixed (byte* ptr = &sourceByteRef)
        {
            UpdateBuffer(buffer, bufferOffsetInBytes, (IntPtr)ptr, sizeInBytes);
        }
    }

    /// <summary>
    /// Updates a <see cref="DeviceBuffer"/> region with new data.
    /// This function must be used with a blittable value type <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The type of data to upload.</typeparam>
    /// <param name="buffer">The resource to update.</param>
    /// <param name="bufferOffsetInBytes">An offset, in bytes, from the beginning of the <see cref="DeviceBuffer"/>'s storage, at
    /// which new data will be uploaded.</param>
    /// <param name="source">An array containing the data to upload.</param>
    public void UpdateBuffer<T>(
        DeviceBuffer buffer,
        uint bufferOffsetInBytes,
        T[] source) where T : unmanaged
    {
        UpdateBuffer(buffer, bufferOffsetInBytes, (ReadOnlySpan<T>)source);
    }

    /// <summary>
    /// Updates a <see cref="DeviceBuffer"/> region with new data.
    /// This function must be used with a blittable value type <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The type of data to upload.</typeparam>
    /// <param name="buffer">The resource to update.</param>
    /// <param name="bufferOffsetInBytes">An offset, in bytes, from the beginning of the <see cref="DeviceBuffer"/>'s storage, at
    /// which new data will be uploaded.</param>
    /// <param name="source">A readonly span containing the data to upload.</param>
    public unsafe void UpdateBuffer<T>(
        DeviceBuffer buffer,
        uint bufferOffsetInBytes,
        ReadOnlySpan<T> source) where T : unmanaged
    {
        fixed (void* pin = &MemoryMarshal.GetReference(source))
        {
            UpdateBuffer(buffer, bufferOffsetInBytes, (IntPtr)pin, (uint)(sizeof(T) * source.Length));
        }
    }

    /// <summary>
    /// Updates a <see cref="DeviceBuffer"/> region with new data.
    /// This function must be used with a blittable value type <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The type of data to upload.</typeparam>
    /// <param name="buffer">The resource to update.</param>
    /// <param name="bufferOffsetInBytes">An offset, in bytes, from the beginning of the <see cref="DeviceBuffer"/>'s storage, at
    /// which new data will be uploaded.</param>
    /// <param name="source">A span containing the data to upload.</param>
    public void UpdateBuffer<T>(
        DeviceBuffer buffer,
        uint bufferOffsetInBytes,
        Span<T> source) where T : unmanaged
    {
        UpdateBuffer(buffer, bufferOffsetInBytes, (ReadOnlySpan<T>)source);
    }

    /// <summary>
    /// Updates a <see cref="DeviceBuffer"/> region with new data.
    /// </summary>
    /// <param name="buffer">The resource to update.</param>
    /// <param name="bufferOffsetInBytes">An offset, in bytes, from the beginning of the <see cref="DeviceBuffer"/>'s storage, at
    /// which new data will be uploaded.</param>
    /// <param name="source">A pointer to the start of the data to upload.</param>
    /// <param name="sizeInBytes">The total size of the uploaded data, in bytes.</param>
    public void UpdateBuffer(
        DeviceBuffer buffer,
        uint bufferOffsetInBytes,
        IntPtr source,
        uint sizeInBytes)
    {
        if (bufferOffsetInBytes + sizeInBytes > buffer.SizeInBytes)
        {
            throw new NeoVeldridException(
                $"The data size given to UpdateBuffer is too large. The given buffer can only hold {buffer.SizeInBytes} total bytes. The requested update would require {bufferOffsetInBytes + sizeInBytes} bytes.");
        }
        if (sizeInBytes == 0)
        {
            return;
        }
        UpdateBufferCore(buffer, bufferOffsetInBytes, source, sizeInBytes);
    }

    private protected abstract void UpdateBufferCore(DeviceBuffer buffer, uint bufferOffsetInBytes, IntPtr source, uint sizeInBytes);

    /// <summary>
    /// Gets whether or not the given <see cref="PixelFormat"/>, <see cref="TextureType"/>, and <see cref="TextureUsage"/>
    /// combination is supported by this instance.
    /// </summary>
    /// <param name="format">The PixelFormat to query.</param>
    /// <param name="type">The TextureType to query.</param>
    /// <param name="usage">The TextureUsage to query.</param>
    /// <returns>True if the given combination is supported; false otherwise.</returns>
    public bool GetPixelFormatSupport(
        PixelFormat format,
        TextureType type,
        TextureUsage usage)
    {
        return GetPixelFormatSupportCore(format, type, usage, out _);
    }

    /// <summary>
    /// Gets whether or not the given <see cref="PixelFormat"/>, <see cref="TextureType"/>, and <see cref="TextureUsage"/>
    /// combination is supported by this instance, and also gets the device-specific properties supported by this instance.
    /// </summary>
    /// <param name="format">The PixelFormat to query.</param>
    /// <param name="type">The TextureType to query.</param>
    /// <param name="usage">The TextureUsage to query.</param>
    /// <param name="properties">If the combination is supported, then this parameter describes the limits of a Texture
    /// created using the given combination of attributes.</param>
    /// <returns>True if the given combination is supported; false otherwise. If the combination is supported,
    /// then <paramref name="properties"/> contains the limits supported by this instance.</returns>
    public bool GetPixelFormatSupport(
        PixelFormat format,
        TextureType type,
        TextureUsage usage,
        out PixelFormatProperties properties)
    {
        return GetPixelFormatSupportCore(format, type, usage, out properties);
    }

    private protected abstract bool GetPixelFormatSupportCore(
        PixelFormat format,
        TextureType type,
        TextureUsage usage,
        out PixelFormatProperties properties);

    /// <summary>
    /// Adds the given object to a deferred disposal list, which will be processed when this GraphicsDevice becomes idle.
    /// This method can be used to safely dispose a device resource which may be in use at the time this method is called,
    /// but which will no longer be in use when the device is idle.
    /// </summary>
    /// <param name="disposable">An object to dispose when this instance becomes idle.</param>
    public void DisposeWhenIdle(IDisposable disposable)
    {
        ArgumentNullException.ThrowIfNull(disposable);

        lock (_deferredDisposalLock)
        {
            bool drainReentry =
                _deferredDisposalDrainOwnerThreadId == Environment.CurrentManagedThreadId;
            bool finalDrainReentry =
                _deferredDisposalState == DeferredDisposalQueueState.Closing
                && drainReentry;
            if (_deferredDisposalState == DeferredDisposalQueueState.Closed
                || (_deferredDisposalState == DeferredDisposalQueueState.Closing
                    && !finalDrainReentry)
                || (_disposeSignaled != 0 && !drainReentry))
            {
                throw new ObjectDisposedException(
                    nameof(GraphicsDevice),
                    "Deferred disposal is closed because the graphics device is being torn down.");
            }

            _disposables.Enqueue(disposable);
        }
    }

    private void FlushDeferredDisposals() =>
        DrainDeferredDisposals(closeQueue: false);

    private void CloseAndDrainDeferredDisposals() =>
        DrainDeferredDisposals(closeQueue: true);

    private void DrainDeferredDisposals(bool closeQueue)
    {
        int currentThreadId = Environment.CurrentManagedThreadId;
        int remainingInIdleBatch = 0;

        lock (_deferredDisposalLock)
        {
            while (_deferredDisposalDrainOwnerThreadId != 0)
            {
                if (_deferredDisposalDrainOwnerThreadId == currentThreadId)
                {
                    if (closeQueue)
                    {
                        throw new InvalidOperationException(
                            "The final deferred-disposal drain cannot reenter itself.");
                    }

                    // The outer drain owns this idle-boundary batch. Let it
                    // finish without recursively consuming later additions.
                    return;
                }

                Monitor.Wait(_deferredDisposalLock);
            }

            if (closeQueue)
            {
                if (_deferredDisposalState == DeferredDisposalQueueState.Closed)
                {
                    return;
                }

                _deferredDisposalState = DeferredDisposalQueueState.Closing;
            }
            else
            {
                if (_deferredDisposalState != DeferredDisposalQueueState.Open)
                {
                    return;
                }

                remainingInIdleBatch = _disposables.Count;
                if (remainingInIdleBatch == 0)
                {
                    return;
                }
            }

            // Exactly one thread owns dequeueing. Ordinary idle-boundary
            // drains consume their captured batch; the final owner consumes
            // through a locked empty-to-closed transition.
            _deferredDisposalDrainOwnerThreadId = currentThreadId;
        }

        List<Exception> failures = null;
        try
        {
            while (true)
            {
                IDisposable disposable;
                lock (_deferredDisposalLock)
                {
                    if (closeQueue)
                    {
                        if (_disposables.Count == 0)
                        {
                            // The empty check and closed transition share the
                            // enqueue lock, so no producer can slip an item past
                            // the final drain.
                            _deferredDisposalState = DeferredDisposalQueueState.Closed;
                            Monitor.PulseAll(_deferredDisposalLock);
                            break;
                        }
                    }
                    else
                    {
                        if (remainingInIdleBatch == 0 || _disposables.Count == 0)
                        {
                            break;
                        }

                        remainingInIdleBatch--;
                    }

                    disposable = _disposables.Dequeue();
                }

                // User-controlled disposal is never invoked under the queue
                // lock. During final closing, reentrant additions are accepted
                // and consumed by a later iteration of this same drain.
                AttemptCleanup(disposable, ref failures);
            }
        }
        finally
        {
            lock (_deferredDisposalLock)
            {
                if (closeQueue)
                {
                    // All ordinary disposal failures are captured above. This
                    // fallback closes admission if an exceptional runtime
                    // failure interrupts the drain itself.
                    if (_deferredDisposalState == DeferredDisposalQueueState.Closing)
                    {
                        _deferredDisposalState = DeferredDisposalQueueState.Closed;
                    }
                }
                else
                {
                    Debug.Assert(
                        _deferredDisposalState == DeferredDisposalQueueState.Open,
                        "An ordinary deferred-disposal drain cannot own a closing queue.");
                }

                _deferredDisposalDrainOwnerThreadId = 0;
                Monitor.PulseAll(_deferredDisposalLock);
            }
        }

        ThrowDeferredDisposalFailures(failures);
    }

    /// <summary>
    /// Performs API-specific disposal of resources controlled by this instance.
    /// </summary>
    protected abstract void PlatformDispose();

    /// <summary>
    /// Creates and caches common device resources and gates initialization diagnostics.
    /// Backend constructors own the surrounding initialization transaction and must call
    /// <see cref="FailDeviceCreation"/> if any acquisition step fails.
    /// </summary>
    protected void CompleteDeviceCreation()
    {
        PointSampler = ResourceFactory.CreateSampler(SamplerDescription.Point);
        LinearSampler = ResourceFactory.CreateSampler(SamplerDescription.Linear);
        if (Features.SamplerAnisotropy)
        {
            _aniso4xSampler = ResourceFactory.CreateSampler(SamplerDescription.Aniso4x);
        }

        CheckValidation("device initialization");
        _deviceCreationComplete = true;
    }

    /// <summary>
    /// Aborts a backend constructor, preserving both the initialization failure and any
    /// independent failures encountered while releasing partially acquired native state.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    protected void FailDeviceCreation(Exception initializationError)
    {
        ArgumentNullException.ThrowIfNull(initializationError);

        try
        {
            Dispose();
        }
        catch (Exception cleanupError)
        {
            throw new AggregateException(
                "Graphics-device initialization and cleanup both failed.",
                initializationError,
                cleanupError);
        }

        ExceptionDispatchInfo.Capture(initializationError).Throw();
        throw new UnreachableException();
    }

    /// <summary>
    /// Gets a simple point-filtered <see cref="Sampler"/> object owned by this instance.
    /// This object is created with <see cref="SamplerDescription.Point"/>.
    /// </summary>
    public Sampler PointSampler { get; private set; }

    /// <summary>
    /// Gets a simple linear-filtered <see cref="Sampler"/> object owned by this instance.
    /// This object is created with <see cref="SamplerDescription.Linear"/>.
    /// </summary>
    public Sampler LinearSampler { get; private set; }

    /// <summary>
    /// Gets a simple 4x anisotropic-filtered <see cref="Sampler"/> object owned by this instance.
    /// This object is created with <see cref="SamplerDescription.Aniso4x"/>.
    /// This property can only be used when <see cref="GraphicsDeviceFeatures.SamplerAnisotropy"/> is supported.
    /// </summary>
    public Sampler Aniso4xSampler
    {
        get
        {
            if (!Features.SamplerAnisotropy)
            {
                throw new NeoVeldridException(
                    "GraphicsDevice.Aniso4xSampler cannot be used unless GraphicsDeviceFeatures.SamplerAnisotropy is supported.");
            }

            Debug.Assert(_aniso4xSampler != null);
            return _aniso4xSampler;
        }
    }

    /// <summary>
    /// A bool indicating whether this instance has been disposed.
    /// </summary>
    public bool IsDisposed => Volatile.Read(ref _disposeSignaled) != 0;

    /// <summary>
    /// Frees unmanaged resources controlled by this device.
    /// All created child resources must be Disposed prior to calling this method.
    /// </summary>
    public void Dispose() => ExecuteDeviceDisposal(DisposeCore);

    /// <summary>
    /// Gives a thread-affine backend ownership of the thread on which the shared
    /// disposal transaction begins. The default backend executes inline.
    /// </summary>
    private protected virtual void ExecuteDeviceDisposal(Action disposeCore) =>
        disposeCore();

    /// <summary>
    /// Joins a disposal transaction which a backend has already admitted on
    /// behalf of the current caller. Unlike an ordinary repeated
    /// <see cref="Dispose"/> call, an admitted caller owns the outcome of that
    /// transaction and must observe its failure even if teardown completed
    /// before the caller resumed.
    /// </summary>
    private protected void JoinPublishedDeviceDisposal()
    {
        ExceptionDispatchInfo failure;
        int currentThreadId = Environment.CurrentManagedThreadId;
        lock (_deferredDisposalLock)
        {
            if (_disposeSignaled == 0)
            {
                throw new InvalidOperationException(
                    "Cannot join a graphics-device disposal transaction before it has been published.");
            }

            if (!_disposeCompleted && _disposeOwnerThreadId == currentThreadId)
            {
                throw new InvalidOperationException(
                    "The graphics-device disposal owner cannot join its own active transaction.");
            }

            while (!_disposeCompleted)
            {
                Monitor.Wait(_deferredDisposalLock);
            }

            failure = _disposeFailure;
        }

        failure?.Throw();
    }

    private void DisposeCore()
    {
        int currentThreadId = Environment.CurrentManagedThreadId;
        ExceptionDispatchInfo concurrentFailure = null;
        bool ownsDisposal = false;

        lock (_deferredDisposalLock)
        {
            if (_deferredDisposalDrainOwnerThreadId == currentThreadId)
            {
                throw new InvalidOperationException(
                    "A graphics device cannot be disposed reentrantly from a deferred resource disposal.");
            }

            if (_disposeSignaled != 0)
            {
                if (_disposeCompleted || _disposeOwnerThreadId == currentThreadId)
                {
                    return;
                }

                // A concurrent Dispose call joins the active teardown instead
                // of returning while native resources are still being released.
                while (!_disposeCompleted)
                {
                    Monitor.Wait(_deferredDisposalLock);
                }

                concurrentFailure = _disposeFailure;
            }
            else
            {
                // Publish disposal while holding the same lock used to inspect
                // the drain owner and to join concurrent Dispose calls.
                Volatile.Write(ref _disposeSignaled, 1);
                _disposeOwnerThreadId = currentThreadId;
                ownsDisposal = true;
            }
        }

        if (!ownsDisposal)
        {
            concurrentFailure?.Throw();
            return;
        }

        ExceptionDispatchInfo completionFailure = null;
        try
        {
            List<Exception> failures = null;
            if (_deviceCreationComplete)
            {
                AttemptCleanup(WaitForIdle, ref failures);
            }
            AttemptCleanup(CloseAndDrainDeferredDisposals, ref failures);
            AttemptCleanup(() => PointSampler?.Dispose(), ref failures);
            AttemptCleanup(() => LinearSampler?.Dispose(), ref failures);
            AttemptCleanup(() => _aniso4xSampler?.Dispose(), ref failures);
            AttemptCleanup(PlatformDispose, ref failures);
            if (_validation != null)
            {
                AttemptCleanup(
                    () => _validation.Seal("device teardown", _collectValidationMessages),
                    ref failures);
            }

            if (failures?.Count == 1)
            {
                completionFailure = ExceptionDispatchInfo.Capture(failures[0]);
            }
            else if (failures?.Count > 1)
            {
                completionFailure = ExceptionDispatchInfo.Capture(
                    new AggregateException(
                        "Graphics-device teardown encountered multiple failures.",
                        failures));
            }
        }
        catch (Exception exception)
        {
            // Preserve an unexpected orchestration failure for concurrent
            // callers and still publish completion in the finally block.
            completionFailure = ExceptionDispatchInfo.Capture(exception);
        }
        finally
        {
            lock (_deferredDisposalLock)
            {
                _disposeFailure = completionFailure;
                _disposeOwnerThreadId = 0;
                _disposeCompleted = true;
                Monitor.PulseAll(_deferredDisposalLock);
            }
        }

        completionFailure?.Throw();
    }

    private enum DeferredDisposalQueueState
    {
        Open,
        Closing,
        Closed,
    }

    private readonly struct CommandSubmissionBoundary
    {
        public CommandSubmissionBoundary(
            GraphicsDevice device,
            CommandList commandList,
            Fence fence)
        {
            Device = device;
            CommandList = commandList;
            Fence = fence;
        }

        public GraphicsDevice Device { get; }
        public CommandList CommandList { get; }
        public Fence Fence { get; }
    }

    private readonly struct SwapchainBoundary
    {
        public SwapchainBoundary(GraphicsDevice device, Swapchain swapchain)
        {
            Device = device;
            Swapchain = swapchain;
        }

        public GraphicsDevice Device { get; }
        public Swapchain Swapchain { get; }
    }

    private static void AttemptCleanup(Action action, ref List<Exception> failures)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            failures ??= new List<Exception>();
            failures.Add(exception);
        }
    }

    private static void AttemptCleanup(IDisposable disposable, ref List<Exception> failures)
    {
        try
        {
            disposable.Dispose();
        }
        catch (Exception exception)
        {
            failures ??= new List<Exception>();
            failures.Add(exception);
        }
    }

    private static void ThrowDeferredDisposalFailures(List<Exception> failures)
    {
        if (failures == null || failures.Count == 0)
        {
            return;
        }

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        throw new AggregateException(
            "Deferred resource disposal encountered multiple failures.",
            failures);
    }

#if !EXCLUDE_D3D11_BACKEND
    /// <summary>
    /// Tries to get a <see cref="BackendInfoD3D11"/> for this instance. This method will only succeed if this is a D3D11
    /// GraphicsDevice.
    /// </summary>
    /// <param name="info">If successful, this will contain the <see cref="BackendInfoD3D11"/> for this instance.</param>
    /// <returns>True if this is a D3D11 GraphicsDevice and the operation was successful. False otherwise.</returns>
    public virtual bool GetD3D11Info(out BackendInfoD3D11 info) { info = null; return false; }

    /// <summary>
    /// Gets a <see cref="BackendInfoD3D11"/> for this instance. This method will only succeed if this is a D3D11
    /// GraphicsDevice. Otherwise, this method will throw an exception.
    /// </summary>
    /// <returns>The <see cref="BackendInfoD3D11"/> for this instance.</returns>
    public BackendInfoD3D11 GetD3D11Info()
    {
        if (!GetD3D11Info(out BackendInfoD3D11 info))
        {
            throw new NeoVeldridException($"{nameof(GetD3D11Info)} can only be used on a D3D11 GraphicsDevice.");
        }

        return info;
    }
#endif

#if !EXCLUDE_VULKAN_BACKEND
    /// <summary>
    /// Tries to get a <see cref="BackendInfoVulkan"/> for this instance. This method will only succeed if this is a Vulkan
    /// GraphicsDevice.
    /// </summary>
    /// <param name="info">If successful, this will contain the <see cref="BackendInfoVulkan"/> for this instance.</param>
    /// <returns>True if this is a Vulkan GraphicsDevice and the operation was successful. False otherwise.</returns>
    public virtual bool GetVulkanInfo(out BackendInfoVulkan info) { info = null; return false; }

    /// <summary>
    /// Gets a <see cref="BackendInfoVulkan"/> for this instance. This method will only succeed if this is a Vulkan
    /// GraphicsDevice. Otherwise, this method will throw an exception.
    /// </summary>
    /// <returns>The <see cref="BackendInfoVulkan"/> for this instance.</returns>
    public BackendInfoVulkan GetVulkanInfo()
    {
        if (!GetVulkanInfo(out BackendInfoVulkan info))
        {
            throw new NeoVeldridException($"{nameof(GetVulkanInfo)} can only be used on a Vulkan GraphicsDevice.");
        }

        return info;
    }
#endif

#if !EXCLUDE_OPENGL_BACKEND
    /// <summary>
    /// Tries to get a <see cref="BackendInfoOpenGL"/> for this instance. This method will only succeed if this is an OpenGL
    /// GraphicsDevice.
    /// </summary>
    /// <param name="info">If successful, this will contain the <see cref="BackendInfoOpenGL"/> for this instance.</param>
    /// <returns>True if this is an OpenGL GraphicsDevice and the operation was successful. False otherwise.</returns>
    public virtual bool GetOpenGLInfo(out BackendInfoOpenGL info) { info = null; return false; }

    /// <summary>
    /// Gets a <see cref="BackendInfoOpenGL"/> for this instance. This method will only succeed if this is an OpenGL
    /// GraphicsDevice. Otherwise, this method will throw an exception.
    /// </summary>
    /// <returns>The <see cref="BackendInfoOpenGL"/> for this instance.</returns>
    public BackendInfoOpenGL GetOpenGLInfo()
    {
        if (!GetOpenGLInfo(out BackendInfoOpenGL info))
        {
            throw new NeoVeldridException($"{nameof(GetOpenGLInfo)} can only be used on an OpenGL GraphicsDevice.");
        }

        return info;
    }
#endif


    /// <summary>
    /// Checks whether the given <see cref="GraphicsBackend"/> is supported on this system.
    /// </summary>
    /// <param name="backend">The GraphicsBackend to check.</param>
    /// <returns>True if the GraphicsBackend is supported; false otherwise.</returns>
    public static bool IsBackendSupported(GraphicsBackend backend)
    {
        switch (backend)
        {
            case GraphicsBackend.Direct3D11:
#if !EXCLUDE_D3D11_BACKEND
                return RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
#else
                return false;
#endif
            case GraphicsBackend.Vulkan:
#if !EXCLUDE_VULKAN_BACKEND
                return Vk.VkGraphicsDevice.IsSupported();
#else
                return false;
#endif
            case GraphicsBackend.OpenGL:
#if !EXCLUDE_OPENGL_BACKEND
                return !RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
#else
                return false;
#endif
            case GraphicsBackend.OpenGLES:
#if !EXCLUDE_OPENGL_BACKEND
                return !RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
#else
                return false;
#endif
            default:
                throw Illegal.Value<GraphicsBackend>();
        }
    }

    /// <summary>
    /// Retrieves the latest API version of the given <see cref="GraphicsBackend"/>.
    /// </summary>
    /// <param name="backend">The <see cref="GraphicsBackend"/> to get the latest API version for.</param>
    /// <returns>The latest API version of the given <see cref="GraphicsBackend"/>. If <see cref="GraphicsApiVersion.Unknown"/>
    /// is returned, then the API was detected but the version was unable to be retrieved. If the API is not available
    /// or included in the build, a <see cref="NeoVeldridException"/> will be thrown.</returns>
    public static GraphicsApiVersion GetBackendVersion(GraphicsBackend backend)
    {
#if !EXCLUDE_D3D11_BACKEND
        if (backend == GraphicsBackend.Direct3D11 && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return D3D11.D3D11GraphicsDevice.GetApiVersion();
#endif
#if !EXCLUDE_VULKAN_BACKEND
        if (backend == GraphicsBackend.Vulkan && Vk.VkGraphicsDevice.IsSupported())
            return Vk.VkGraphicsDevice.GetApiVersion();
#endif
#if !EXCLUDE_OPENGL_BACKEND
        if (backend == GraphicsBackend.OpenGL || backend == GraphicsBackend.OpenGLES)
        {
            // Throw a specific exception to tell the user that the OpenGL backend
            // doesn't support OSX.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                throw new NeoVeldridException("The OpenGL backend doesn't support OSX/MacOS. Please use the Vulkan backend with MoltenVK.");

            return OpenGL.OpenGLVersionInfo.GetApiVersion(backend);
        }
#endif

        throw new NeoVeldridException("The provided graphics backend is either not supported, not included in the build, or out of bounds of the enumerator type.");
    }

    /// <summary>
    /// Retrieves the default <see cref="GraphicsBackend"/> for the executing platform, chosen by order of
    /// availability if a backend is not supported or included in the build.
    /// </summary>
    /// <returns>The default <see cref="GraphicsBackend"/> for the executing platform.</returns>
    public static GraphicsBackend GetPlatformDefaultBackend()
    {
#if !EXCLUDE_D3D11_BACKEND
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return GraphicsBackend.Direct3D11;
#endif

#if !EXCLUDE_VULKAN_BACKEND
        if (Vk.VkGraphicsDevice.IsSupported())
            return GraphicsBackend.Vulkan; // Common denominator backend.
#endif

#if !EXCLUDE_OPENGL_BACKEND                        
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return GraphicsBackend.OpenGL;
#endif

        throw new NeoVeldridException("No graphics backend is available. Enable at least one backend.");
    }

#if !EXCLUDE_D3D11_BACKEND
    /// <summary>
    /// Creates a new <see cref="GraphicsDevice"/> using Direct3D 11.
    /// </summary>
    /// <param name="options">Describes several common properties of the GraphicsDevice.</param>
    /// <returns>A new <see cref="GraphicsDevice"/> using the Direct3D 11 API.</returns>
    public static GraphicsDevice CreateD3D11(GraphicsDeviceOptions options)
    {
        return new D3D11.D3D11GraphicsDevice(options, new D3D11DeviceOptions(), null);
    }

    /// <summary>
    /// Creates a new <see cref="GraphicsDevice"/> using Direct3D 11, with a main Swapchain.
    /// </summary>
    /// <param name="options">Describes several common properties of the GraphicsDevice.</param>
    /// <param name="swapchainDescription">A description of the main Swapchain to create.</param>
    /// <returns>A new <see cref="GraphicsDevice"/> using the Direct3D 11 API.</returns>
    public static GraphicsDevice CreateD3D11(GraphicsDeviceOptions options, SwapchainDescription swapchainDescription)
    {
        return new D3D11.D3D11GraphicsDevice(options, new D3D11DeviceOptions(), swapchainDescription);
    }

    /// <summary>
    /// Creates a new <see cref="GraphicsDevice"/> using Direct3D 11.
    /// </summary>
    /// <param name="options">Describes several common properties of the GraphicsDevice.</param>
    /// <param name="d3d11Options">The Direct3D11-specific options used to create the device.</param>
    /// <returns>A new <see cref="GraphicsDevice"/> using the Direct3D 11 API.</returns>
    public static GraphicsDevice CreateD3D11(GraphicsDeviceOptions options, D3D11DeviceOptions d3d11Options)
    {
        return new D3D11.D3D11GraphicsDevice(options, d3d11Options, null);
    }

    /// <summary>
    /// Creates a new <see cref="GraphicsDevice"/> using Direct3D 11, with a main Swapchain.
    /// </summary>
    /// <param name="options">Describes several common properties of the GraphicsDevice.</param>
    /// <param name="d3d11Options">The Direct3D11-specific options used to create the device.</param>
    /// <param name="swapchainDescription">A description of the main Swapchain to create.</param>
    /// <returns>A new <see cref="GraphicsDevice"/> using the Direct3D 11 API.</returns>
    public static GraphicsDevice CreateD3D11(GraphicsDeviceOptions options, D3D11DeviceOptions d3d11Options, SwapchainDescription swapchainDescription)
    {
        return new D3D11.D3D11GraphicsDevice(options, d3d11Options, swapchainDescription);
    }

    /// <summary>
    /// Creates a new <see cref="GraphicsDevice"/> using Direct3D 11, with a main Swapchain.
    /// </summary>
    /// <param name="options">Describes several common properties of the GraphicsDevice.</param>
    /// <param name="hwnd">The Win32 window handle to render into.</param>
    /// <param name="width">The initial width of the window.</param>
    /// <param name="height">The initial height of the window.</param>
    /// <returns>A new <see cref="GraphicsDevice"/> using the Direct3D 11 API.</returns>
    public static GraphicsDevice CreateD3D11(GraphicsDeviceOptions options, IntPtr hwnd, uint width, uint height)
    {
        SwapchainDescription swapchainDescription = new SwapchainDescription(
            SwapchainSource.CreateWin32(hwnd, IntPtr.Zero),
            width, height,
            options.SwapchainDepthFormat,
            options.SyncToVerticalBlank,
            options.SwapchainSrgbFormat);

        return new D3D11.D3D11GraphicsDevice(options, new D3D11DeviceOptions(), swapchainDescription);
    }
#endif

#if !EXCLUDE_VULKAN_BACKEND
    /// <summary>
    /// Creates a new <see cref="GraphicsDevice"/> using Vulkan.
    /// </summary>
    /// <param name="options">Describes several common properties of the GraphicsDevice.</param>
    /// <returns>A new <see cref="GraphicsDevice"/> using the Vulkan API.</returns>
    public static GraphicsDevice CreateVulkan(GraphicsDeviceOptions options)
    {
        return new Vk.VkGraphicsDevice(options, null);
    }

    /// <summary>
    /// Creates a new <see cref="GraphicsDevice"/> using Vulkan.
    /// </summary>
    /// <param name="options">Describes several common properties of the GraphicsDevice.</param>
    /// <param name="vkOptions">The Vulkan-specific options used to create the device.</param>
    /// <returns>A new <see cref="GraphicsDevice"/> using the Vulkan API.</returns>
    public static GraphicsDevice CreateVulkan(GraphicsDeviceOptions options, VulkanDeviceOptions vkOptions)
    {
        return new Vk.VkGraphicsDevice(options, null, vkOptions);
    }

    /// <summary>
    /// Creates a new <see cref="GraphicsDevice"/> using Vulkan, with a main Swapchain.
    /// </summary>
    /// <param name="options">Describes several common properties of the GraphicsDevice.</param>
    /// <param name="swapchainDescription">A description of the main Swapchain to create.</param>
    /// <returns>A new <see cref="GraphicsDevice"/> using the Vulkan API.</returns>
    public static GraphicsDevice CreateVulkan(GraphicsDeviceOptions options, SwapchainDescription swapchainDescription)
    {
        return new Vk.VkGraphicsDevice(options, swapchainDescription);
    }

    /// <summary>
    /// Creates a new <see cref="GraphicsDevice"/> using Vulkan, with a main Swapchain.
    /// </summary>
    /// <param name="options">Describes several common properties of the GraphicsDevice.</param>
    /// <param name="vkOptions">The Vulkan-specific options used to create the device.</param>
    /// <param name="swapchainDescription">A description of the main Swapchain to create.</param>
    /// <returns>A new <see cref="GraphicsDevice"/> using the Vulkan API.</returns>
    public static GraphicsDevice CreateVulkan(
        GraphicsDeviceOptions options,
        SwapchainDescription swapchainDescription,
        VulkanDeviceOptions vkOptions)
    {
        return new Vk.VkGraphicsDevice(options, swapchainDescription, vkOptions);
    }

    /// <summary>
    /// Creates a new <see cref="GraphicsDevice"/> using Vulkan, with a main Swapchain.
    /// </summary>
    /// <param name="options">Describes several common properties of the GraphicsDevice.</param>
    /// <param name="surfaceSource">The source from which a Vulkan surface can be created.</param>
    /// <param name="width">The initial width of the window.</param>
    /// <param name="height">The initial height of the window.</param>
    /// <returns>A new <see cref="GraphicsDevice"/> using the Vulkan API.</returns>
    public static GraphicsDevice CreateVulkan(GraphicsDeviceOptions options, Vk.VkSurfaceSource surfaceSource, uint width, uint height)
    {
        SwapchainDescription scDesc = new SwapchainDescription(
            surfaceSource.GetSurfaceSource(),
            width, height,
            options.SwapchainDepthFormat,
            options.SyncToVerticalBlank,
            options.SwapchainSrgbFormat);

        return new Vk.VkGraphicsDevice(options, scDesc);
    }
#endif

#if !EXCLUDE_OPENGL_BACKEND
    /// <summary>
    /// Creates a new <see cref="GraphicsDevice"/> using OpenGL or OpenGL ES, with a main Swapchain.
    /// </summary>
    /// <param name="options">Describes several common properties of the GraphicsDevice.</param>
    /// <param name="platformInfo">An <see cref="OpenGL.OpenGLPlatformInfo"/> object encapsulating necessary OpenGL context
    /// information.</param>
    /// <param name="width">The initial width of the window.</param>
    /// <param name="height">The initial height of the window.</param>
    /// <returns>A new <see cref="GraphicsDevice"/> using the OpenGL or OpenGL ES API.</returns>
    public static GraphicsDevice CreateOpenGL(
        GraphicsDeviceOptions options,
        OpenGL.OpenGLPlatformInfo platformInfo,
        uint width,
        uint height)
    {
        return new OpenGL.OpenGLGraphicsDevice(options, platformInfo, width, height);
    }
#endif

}
