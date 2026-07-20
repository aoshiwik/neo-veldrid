using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Silk.NET.Direct3D11;

namespace NeoVeldrid.D3D11;

/// <summary>
/// Owns the native playback boundary for deferred texture uploads. The
/// software-runtime path always starts with the actual rebased source. A real
/// software runtime consumes that pointer unchanged, while forced
/// qualification translates it only at the final native-capable boundary.
/// </summary>
internal unsafe sealed class D3D11DeferredTextureUploadExecutor
{
    private readonly D3D11DeferredTextureUploadPlaybackAdapter _playbackAdapter;
    private readonly object _traceLock = new();
    private readonly List<D3D11DeferredTextureUploadPlaybackTrace>
        _forcedPlaybackTraces = new();
    private long _executionCount;
    private long _rebasedExecutionCount;
    private long _nativePlaybackTranslationCount;

    public D3D11DeferredTextureUploadPlaybackPath PlaybackPath { get; }

    public bool UsesSoftwareCommandListSourceContract =>
        PlaybackPath != D3D11DeferredTextureUploadPlaybackPath.NativeCommandList;

    public bool IsForcedSoftwareCommandListEmulation =>
        PlaybackPath ==
            D3D11DeferredTextureUploadPlaybackPath
                .ForcedSoftwareCommandListRuntime;

    public bool UsesNativeSoftwareRuntimePlaybackTranslation =>
        _playbackAdapter.AppliesNativePlaybackTranslation;

    public ulong ExecutionCount =>
        checked((ulong)Interlocked.Read(ref _executionCount));

    public ulong RebasedExecutionCount =>
        checked((ulong)Interlocked.Read(ref _rebasedExecutionCount));

    public ulong NativePlaybackTranslationCount =>
        checked((ulong)Interlocked.Read(ref _nativePlaybackTranslationCount));

    private D3D11DeferredTextureUploadExecutor(
        D3D11DeferredTextureUploadPlaybackPath playbackPath,
        D3D11DeferredTextureUploadPlaybackAdapter playbackAdapter)
    {
        PlaybackPath = playbackPath;
        _playbackAdapter = playbackAdapter;
    }

    public static D3D11DeferredTextureUploadExecutor Create(
        bool driverSupportsCommandLists,
        D3D11DeferredTextureUploadMode mode)
    {
        if (driverSupportsCommandLists
            && mode == D3D11DeferredTextureUploadMode.Automatic)
        {
            return new D3D11DeferredTextureUploadExecutor(
                D3D11DeferredTextureUploadPlaybackPath.NativeCommandList,
                new NativeCommandListPlaybackAdapter());
        }

        bool isForced = mode ==
            D3D11DeferredTextureUploadMode
                .ForceSoftwareCommandListEmulation;
        return new D3D11DeferredTextureUploadExecutor(
            isForced
                ? D3D11DeferredTextureUploadPlaybackPath
                    .ForcedSoftwareCommandListRuntime
                : D3D11DeferredTextureUploadPlaybackPath
                    .SoftwareCommandListRuntime,
            new SoftwareCommandListPlaybackAdapter(
                applyNativePlaybackTranslation:
                    driverSupportsCommandLists));
    }

    public void Execute(
        ID3D11DeviceContext* context,
        ID3D11Resource* destination,
        uint subresource,
        Box* region,
        D3D11DeferredTextureUploadSource source,
        uint rowPitch,
        uint depthPitch)
    {
        var playback = new D3D11DeferredTextureUploadPlayback(
            context,
            destination,
            subresource,
            region,
            source,
            rowPitch,
            depthPitch);
        D3D11DeferredTextureUploadPlaybackResult result =
            _playbackAdapter.Execute(in playback);

        // Forced mode is a qualification seam. Automatic native and actual
        // software-runtime devices avoid trace allocation and atomics.
        if (!IsForcedSoftwareCommandListEmulation)
            return;

        Interlocked.Increment(ref _executionCount);
        if (result.LogicalSourceMinusRebasedSource != 0)
            Interlocked.Increment(ref _rebasedExecutionCount);
        if (result.NativePlaybackTranslationApplied)
            Interlocked.Increment(ref _nativePlaybackTranslationCount);

        var trace = new D3D11DeferredTextureUploadPlaybackTrace(
            PlaybackPath,
            result.LogicalSourceMinusRebasedSource,
            source.DestinationX,
            source.DestinationY,
            source.DestinationZ,
            region != null,
            region == null ? 0u : region->Right,
            region == null ? 0u : region->Bottom,
            region == null ? 0u : region->Back,
            rowPitch,
            depthPitch,
            source.Format,
            result.NativePlaybackTranslationApplied);
        lock (_traceLock)
            _forcedPlaybackTraces.Add(trace);
    }

    public D3D11DeferredTextureUploadPlaybackTrace[]
        GetForcedPlaybackTraces()
    {
        lock (_traceLock)
            return _forcedPlaybackTraces.ToArray();
    }

    private abstract class D3D11DeferredTextureUploadPlaybackAdapter
    {
        public abstract bool AppliesNativePlaybackTranslation { get; }

        public abstract D3D11DeferredTextureUploadPlaybackResult Execute(
            in D3D11DeferredTextureUploadPlayback playback);

        protected static void UpdateSubresource(
            in D3D11DeferredTextureUploadPlayback playback,
            void* nativeSource)
        {
            playback.Context->UpdateSubresource(
                playback.Destination,
                playback.Subresource,
                playback.Region,
                nativeSource,
                playback.RowPitch,
                playback.DepthPitch);
        }
    }

    private sealed class NativeCommandListPlaybackAdapter
        : D3D11DeferredTextureUploadPlaybackAdapter
    {
        public override bool AppliesNativePlaybackTranslation => false;

        public override D3D11DeferredTextureUploadPlaybackResult Execute(
            in D3D11DeferredTextureUploadPlayback playback)
        {
            UpdateSubresource(in playback, playback.Source.LogicalSource);
            return default;
        }
    }

    private sealed class SoftwareCommandListPlaybackAdapter
        : D3D11DeferredTextureUploadPlaybackAdapter
    {
        private readonly bool _applyNativePlaybackTranslation;

        public override bool AppliesNativePlaybackTranslation =>
            _applyNativePlaybackTranslation;

        public SoftwareCommandListPlaybackAdapter(
            bool applyNativePlaybackTranslation)
        {
            _applyNativePlaybackTranslation =
                applyNativePlaybackTranslation;
        }

        public override D3D11DeferredTextureUploadPlaybackResult Execute(
            in D3D11DeferredTextureUploadPlayback playback)
        {
            void* rebasedSource =
                playback.Source.GetSoftwareRuntimeInput();
            nuint logicalSourceMinusRebasedSource = unchecked(
                (nuint)playback.Source.LogicalSource -
                (nuint)rebasedSource);
            Debug.Assert(
                logicalSourceMinusRebasedSource ==
                    playback.Source.SoftwareRuntimeAdjustment);

            void* nativeSource = rebasedSource;
            if (_applyNativePlaybackTranslation)
            {
                // Consume the same rebased address that is forwarded unchanged
                // on a real !DriverCommandLists runtime. Translation uses the
                // actual pointer delta, not a second coordinate formula.
                nativeSource = (void*)unchecked(
                    (nuint)rebasedSource +
                    logicalSourceMinusRebasedSource);
                if (nativeSource != playback.Source.LogicalSource)
                {
                    throw new InvalidOperationException(
                        "The forced D3D11 software command-list playback "
                        + "translation did not reconstruct the logical source.");
                }
            }

            UpdateSubresource(in playback, nativeSource);
            return new D3D11DeferredTextureUploadPlaybackResult(
                logicalSourceMinusRebasedSource,
                _applyNativePlaybackTranslation);
        }
    }
}

/// <summary>
/// Immutable arguments for one final deferred texture-upload native call.
/// </summary>
internal readonly unsafe struct D3D11DeferredTextureUploadPlayback
{
    public ID3D11DeviceContext* Context { get; }
    public ID3D11Resource* Destination { get; }
    public uint Subresource { get; }
    public Box* Region { get; }
    public D3D11DeferredTextureUploadSource Source { get; }
    public uint RowPitch { get; }
    public uint DepthPitch { get; }

    public D3D11DeferredTextureUploadPlayback(
        ID3D11DeviceContext* context,
        ID3D11Resource* destination,
        uint subresource,
        Box* region,
        D3D11DeferredTextureUploadSource source,
        uint rowPitch,
        uint depthPitch)
    {
        Context = context;
        Destination = destination;
        Subresource = subresource;
        Region = region;
        Source = source;
        RowPitch = rowPitch;
        DepthPitch = depthPitch;
    }
}

internal readonly struct D3D11DeferredTextureUploadPlaybackResult
{
    public nuint LogicalSourceMinusRebasedSource { get; }
    public bool NativePlaybackTranslationApplied { get; }

    public D3D11DeferredTextureUploadPlaybackResult(
        nuint logicalSourceMinusRebasedSource,
        bool nativePlaybackTranslationApplied)
    {
        LogicalSourceMinusRebasedSource =
            logicalSourceMinusRebasedSource;
        NativePlaybackTranslationApplied =
            nativePlaybackTranslationApplied;
    }
}

/// <summary>
/// Describes the logical and rebased source addresses for one deferred texture
/// upload. This is the single authoritative owner of the software-runtime
/// coordinate-to-byte adjustment.
/// </summary>
internal readonly unsafe struct D3D11DeferredTextureUploadSource
{
    public void* LogicalSource { get; }
    public nuint SoftwareRuntimeAdjustment { get; }
    public uint SizeInBytes { get; }
    public uint DestinationX { get; }
    public uint DestinationY { get; }
    public uint DestinationZ { get; }
    public PixelFormat Format { get; }

    private D3D11DeferredTextureUploadSource(
        void* logicalSource,
        uint sizeInBytes,
        uint destinationX,
        uint destinationY,
        uint destinationZ,
        PixelFormat format,
        nuint softwareRuntimeAdjustment)
    {
        LogicalSource = logicalSource;
        SizeInBytes = sizeInBytes;
        DestinationX = destinationX;
        DestinationY = destinationY;
        DestinationZ = destinationZ;
        Format = format;
        SoftwareRuntimeAdjustment = softwareRuntimeAdjustment;
    }

    public static D3D11DeferredTextureUploadSource Create(
        void* logicalSource,
        uint sizeInBytes,
        uint destinationX,
        uint destinationY,
        uint destinationZ,
        uint sourceRowPitch,
        uint sourceDepthPitch,
        PixelFormat format) =>
        new(
            logicalSource,
            sizeInBytes,
            destinationX,
            destinationY,
            destinationZ,
            format,
            CalculateSoftwareRuntimeAdjustment(
                format,
                destinationX,
                destinationY,
                destinationZ,
                sourceRowPitch,
                sourceDepthPitch));

    internal static nuint CalculateSoftwareRuntimeAdjustment(
        PixelFormat format,
        uint x,
        uint y,
        uint z,
        uint sourceRowPitch,
        uint sourceDepthPitch)
    {
        bool compressed = FormatHelpers.IsCompressedFormat(format);
        ulong blockX = compressed ? x / 4u : x;
        ulong blockY = compressed ? y / 4u : y;
        uint bytesPerStorageElement = compressed
            ? FormatHelpers.GetBlockSizeInBytes(format)
            : FormatSizeHelpers.GetSizeInBytes(format);
        ulong adjustment = checked(
            ((ulong)z * sourceDepthPitch)
            + (blockY * sourceRowPitch)
            + (blockX * bytesPerStorageElement));
        return checked((nuint)adjustment);
    }

    public void* GetSoftwareRuntimeInput()
    {
        // Direct3D consumes this as an address-sized unsigned byte offset. No
        // memory is dereferenced until the software playback contract reapplies
        // the destination origin or the forced adapter translates it safely.
        nuint adjustedAddress = unchecked(
            (nuint)LogicalSource - SoftwareRuntimeAdjustment);
        return (void*)adjustedAddress;
    }
}
