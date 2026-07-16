using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace NeoVeldrid.Tests;

public sealed class CommandListSubmissionDiagnosticsTests
{
    [Fact]
    public void CaptureReturnsNullUntilSubmissionSucceeds()
    {
        var diagnostics = new CommandListSubmissionDiagnostics(
            initialBufferAccessCapacity: 1);

        Assert.Null(diagnostics.CaptureLastSubmission());

        diagnostics.BeginRecording();
        diagnostics.RecordDraw(3, 1, 0, 0);
        diagnostics.EndRecording();

        Assert.Null(diagnostics.CaptureLastSubmission());

        diagnostics.CompleteSuccessfulSubmission();

        Assert.NotNull(diagnostics.CaptureLastSubmission());
    }

    [Fact]
    public void PublicFacadeExposesSnapshotsWithoutPublishingMutableRecorderState()
    {
        Assert.True(typeof(CommandListSubmissionMetrics).IsPublic);
        Assert.True(typeof(CommandListSubmissionSnapshot).IsPublic);
        Assert.False(typeof(CommandListSubmissionDiagnostics).IsPublic);
        Assert.False(typeof(CommandListBufferAccess).IsPublic);
        Assert.False(typeof(CommandListBufferAccessKind).IsPublic);

        var enable = typeof(CommandList).GetMethod(
            nameof(CommandList.EnableSubmissionDiagnostics));
        var disable = typeof(CommandList).GetMethod(
            nameof(CommandList.DisableSubmissionDiagnostics));
        var enabled = typeof(CommandList).GetProperty(
            nameof(CommandList.SubmissionDiagnosticsEnabled));
        var capture = typeof(CommandList).GetMethod(
            nameof(CommandList.CaptureLastSubmissionDiagnostics));
        var tryGetMetrics = typeof(CommandList).GetMethod(
            nameof(CommandList.TryGetLastSubmissionMetrics));

        Assert.NotNull(enable);
        Assert.Equal(typeof(void), enable.ReturnType);
        Assert.NotNull(disable);
        Assert.NotNull(enabled);
        Assert.Equal(typeof(bool), enabled.PropertyType);
        Assert.NotNull(capture);
        Assert.Equal(typeof(CommandListSubmissionSnapshot), capture.ReturnType);
        Assert.Single(capture.ReturnParameter.GetCustomAttributes(
            typeof(MaybeNullAttribute),
            inherit: false));
        Assert.NotNull(tryGetMetrics);
        Assert.Equal(typeof(bool), tryGetMetrics.ReturnType);
        Assert.Collection(
            tryGetMetrics.GetParameters(),
            parameter =>
            {
                Assert.True(parameter.IsOut);
                Assert.Equal(
                    typeof(CommandListSubmissionMetrics).MakeByRefType(),
                    parameter.ParameterType);
            });
        Assert.Null(typeof(CommandList).GetProperty("SubmissionDiagnostics"));
    }

    [Fact]
    public void SuccessfulSubmissionPublishesExactCommandAndBufferRangeEvidence()
    {
        var diagnostics = new CommandListSubmissionDiagnostics(
            initialBufferAccessCapacity: 4);
        var source = new StubBuffer("source", 256);
        var destination = new StubBuffer("destination", 512);

        diagnostics.BeginRecording();
        diagnostics.RecordDraw(12, 2, 3, 4);
        diagnostics.RecordDrawIndexed(6, 3, 2, -1, 5);
        diagnostics.RecordUpdateBuffer(source, 8, 20);
        diagnostics.RecordCopyBuffer(source, 16, destination, 32, 40);
        diagnostics.RecordDrawIndirect(source, 64, 4, 32, indexed: false);
        diagnostics.RecordDispatchIndirect(destination, 96, 12);
        diagnostics.EndRecording();

        Assert.Equal(default, diagnostics.LastSubmittedMetrics);

        diagnostics.CompleteSuccessfulSubmission();

        CommandListSubmissionSnapshot snapshot =
            diagnostics.CaptureLastSubmission();
        CommandListSubmissionMetrics metrics = snapshot.Metrics;
        Assert.Equal(1L, metrics.SubmissionSequence);
        Assert.Equal(6, metrics.InstrumentedCommandCount);
        Assert.Equal(4, metrics.RenderCommandCount);
        Assert.Equal(1, metrics.DrawCallCount);
        Assert.Equal(1, metrics.DrawIndexedCallCount);
        Assert.Equal(1, metrics.DrawIndirectCallCount);
        Assert.Equal(0, metrics.DrawIndexedIndirectCallCount);
        Assert.Equal(3, metrics.TotalDrawCallCount);
        Assert.Equal(24UL, metrics.DrawVertexCount);
        Assert.Equal(18UL, metrics.DrawIndexCount);
        Assert.Equal(5UL, metrics.DrawInstanceCount);
        Assert.Equal(1, metrics.DispatchIndirectCallCount);
        Assert.Equal(1, metrics.UpdateBufferCallCount);
        Assert.Equal(20UL, metrics.UpdatedBufferBytes);
        Assert.Equal(1, metrics.CopyBufferCallCount);
        Assert.Equal(40UL, metrics.CopiedBufferBytes);
        Assert.Equal(4, metrics.BufferAccessCount);
        Assert.NotEqual(0UL, metrics.CommandSignature);
        Assert.NotEqual(0UL, metrics.RenderCommandSignature);

        Assert.Equal(4, snapshot.BufferAccesses.Count);
        CommandListBufferAccess update = snapshot.BufferAccesses[0];
        Assert.Equal(CommandListBufferAccessKind.UpdateDestination, update.Kind);
        Assert.Equal("source", update.PrimaryResourceName);
        Assert.Equal(8UL, update.PrimaryOffsetInBytes);
        Assert.Equal(20UL, update.SizeInBytes);

        CommandListBufferAccess copy = snapshot.BufferAccesses[1];
        Assert.Equal(
            CommandListBufferAccessKind.CopySourceAndDestination,
            copy.Kind);
        Assert.Equal(update.PrimaryResourceIdentity, copy.PrimaryResourceIdentity);
        Assert.Equal("destination", copy.SecondaryResourceName);
        Assert.Equal(16UL, copy.PrimaryOffsetInBytes);
        Assert.Equal(32UL, copy.SecondaryOffsetInBytes);
        Assert.Equal(40UL, copy.SizeInBytes);

        CommandListBufferAccess indirect = snapshot.BufferAccesses[2];
        Assert.Equal(
            CommandListBufferAccessKind.DrawIndirectArguments,
            indirect.Kind);
        Assert.Equal(64UL, indirect.PrimaryOffsetInBytes);
        Assert.Equal(112UL, indirect.SizeInBytes);
    }

    [Fact]
    public void FrozenSnapshotSurvivesRetainedCommandListReuse()
    {
        var diagnostics = new CommandListSubmissionDiagnostics(
            initialBufferAccessCapacity: 1);
        var buffer = new StubBuffer("frame-one", 128);

        diagnostics.BeginRecording();
        diagnostics.RecordUpdateBuffer(buffer, 4, 12);
        diagnostics.EndRecording();
        diagnostics.CompleteSuccessfulSubmission();
        CommandListSubmissionSnapshot first =
            diagnostics.CaptureLastSubmission();

        diagnostics.BeginRecording();
        diagnostics.RecordDraw(3, 1, 0, 0);
        diagnostics.EndRecording();
        diagnostics.CompleteSuccessfulSubmission();
        CommandListSubmissionSnapshot second =
            diagnostics.CaptureLastSubmission();

        Assert.Equal(1L, first.Metrics.SubmissionSequence);
        Assert.Equal(1, first.Metrics.UpdateBufferCallCount);
        Assert.Equal(0, first.Metrics.RenderCommandCount);
        Assert.Single(first.BufferAccesses);
        Assert.Equal("frame-one", first.BufferAccesses[0].PrimaryResourceName);

        Assert.Equal(2L, second.Metrics.SubmissionSequence);
        Assert.Equal(0, second.Metrics.UpdateBufferCallCount);
        Assert.Equal(1, second.Metrics.DrawCallCount);
        Assert.Equal(1, second.Metrics.RenderCommandCount);
        Assert.Empty(second.BufferAccesses);
        Assert.NotEqual(
            first.Metrics.CommandSignature,
            second.Metrics.CommandSignature);
    }

    [Fact]
    public void TransferVariationChangesFullSignatureButPreservesRenderSignature()
    {
        var diagnostics = new CommandListSubmissionDiagnostics(
            initialBufferAccessCapacity: 1);
        var buffer = new StubBuffer("dynamic", 256);

        diagnostics.BeginRecording();
        diagnostics.RecordUpdateBuffer(buffer, 0, 16);
        diagnostics.RecordDraw(3, 1, 0, 0);
        diagnostics.EndRecording();
        diagnostics.CompleteSuccessfulSubmission();
        CommandListSubmissionMetrics first = diagnostics.LastSubmittedMetrics;

        diagnostics.BeginRecording();
        diagnostics.RecordUpdateBuffer(buffer, 32, 48);
        diagnostics.RecordDraw(3, 1, 0, 0);
        diagnostics.EndRecording();
        diagnostics.CompleteSuccessfulSubmission();
        CommandListSubmissionMetrics second = diagnostics.LastSubmittedMetrics;

        Assert.NotEqual(first.CommandSignature, second.CommandSignature);
        Assert.Equal(first.RenderCommandCount, second.RenderCommandCount);
        Assert.Equal(first.RenderCommandSignature, second.RenderCommandSignature);
    }

    [Fact]
    public void EquivalentFrameVersionedResourceSetsPreserveRenderTopology()
    {
        var graphicsLayout = new StubResourceLayout("graphics-layout");
        var computeLayout = new StubResourceLayout("compute-layout");
        CommandListSubmissionMetrics first = RecordRenderStateTopology(
            new StubResourceSet("graphics-frame-0", graphicsLayout),
            new StubResourceSet("compute-frame-0", computeLayout));
        CommandListSubmissionMetrics second = RecordRenderStateTopology(
            new StubResourceSet("graphics-frame-1", graphicsLayout),
            new StubResourceSet("compute-frame-1", computeLayout));

        Assert.Equal(5, first.InstrumentedCommandCount);
        Assert.Equal(5, first.RenderCommandCount);
        Assert.Equal(1, first.SetGraphicsResourceSetCallCount);
        Assert.Equal(1, first.SetComputeResourceSetCallCount);
        Assert.Equal(1, first.SetViewportCallCount);
        Assert.Equal(1, first.SetScissorRectCallCount);
        Assert.NotEqual(first.CommandSignature, second.CommandSignature);
        Assert.Equal(first.RenderCommandCount, second.RenderCommandCount);
        Assert.Equal(first.RenderCommandSignature, second.RenderCommandSignature);
    }

    [Fact]
    public void DifferentResourceLayoutsChangeRenderTopology()
    {
        var firstGraphicsLayout = new StubResourceLayout("graphics-layout-a");
        var secondGraphicsLayout = new StubResourceLayout("graphics-layout-b");
        var computeLayout = new StubResourceLayout("compute-layout");
        var computeSet = new StubResourceSet("compute", computeLayout);
        CommandListSubmissionMetrics first = RecordRenderStateTopology(
            new StubResourceSet("graphics-a", firstGraphicsLayout),
            computeSet);
        CommandListSubmissionMetrics second = RecordRenderStateTopology(
            new StubResourceSet("graphics-b", secondGraphicsLayout),
            computeSet);

        Assert.NotEqual(
            first.RenderCommandSignature,
            second.RenderCommandSignature);
    }

    [Fact]
    public void DynamicOffsetsArePartOfNormalizedRenderTopology()
    {
        var graphicsSet = new StubResourceSet("graphics");
        var computeSet = new StubResourceSet("compute");
        CommandListSubmissionMetrics first = RecordRenderStateTopology(
            graphicsSet,
            computeSet,
            graphicsDynamicOffset: 256);
        CommandListSubmissionMetrics second = RecordRenderStateTopology(
            graphicsSet,
            computeSet,
            graphicsDynamicOffset: 512);

        Assert.NotEqual(
            first.RenderCommandSignature,
            second.RenderCommandSignature);
    }

    [Fact]
    public void ViewportAndScissorArePartOfNormalizedRenderTopology()
    {
        var graphicsSet = new StubResourceSet("graphics");
        var computeSet = new StubResourceSet("compute");
        CommandListSubmissionMetrics baseline = RecordRenderStateTopology(
            graphicsSet,
            computeSet);
        CommandListSubmissionMetrics changedViewport = RecordRenderStateTopology(
            graphicsSet,
            computeSet,
            viewportWidth: 1279f);
        CommandListSubmissionMetrics changedScissor = RecordRenderStateTopology(
            graphicsSet,
            computeSet,
            scissorWidth: 1279);

        Assert.NotEqual(
            baseline.RenderCommandSignature,
            changedViewport.RenderCommandSignature);
        Assert.NotEqual(
            baseline.RenderCommandSignature,
            changedScissor.RenderCommandSignature);
    }

    [Fact]
    public void TextureCopyRegionParticipatesInCommandSignature()
    {
        var source = new StubTexture("source");
        var destination = new StubTexture("destination");

        ulong first = RecordTextureCopy(source, destination, width: 8);
        ulong second = RecordTextureCopy(source, destination, width: 7);

        Assert.NotEqual(first, second);
    }

    private static CommandListSubmissionMetrics RecordRenderStateTopology(
        ResourceSet graphicsSet,
        ResourceSet computeSet,
        uint graphicsDynamicOffset = 256,
        float viewportWidth = 1280f,
        uint scissorWidth = 1280)
    {
        var diagnostics = new CommandListSubmissionDiagnostics(
            initialBufferAccessCapacity: 0);
        uint computeDynamicOffset = 1024;
        var viewport = new Viewport(0f, 0f, viewportWidth, 720f, 0f, 1f);

        diagnostics.BeginRecording();
        diagnostics.RecordSetGraphicsResourceSet(
            0,
            graphicsSet,
            1,
            ref graphicsDynamicOffset);
        diagnostics.RecordSetComputeResourceSet(
            1,
            computeSet,
            1,
            ref computeDynamicOffset);
        diagnostics.RecordSetViewport(0, in viewport);
        diagnostics.RecordSetScissorRect(0, 0, 0, scissorWidth, 720);
        diagnostics.RecordDraw(3, 1, 0, 0);
        diagnostics.EndRecording();
        diagnostics.CompleteSuccessfulSubmission();

        return diagnostics.LastSubmittedMetrics;
    }

    private static ulong RecordTextureCopy(
        Texture source,
        Texture destination,
        uint width)
    {
        var diagnostics = new CommandListSubmissionDiagnostics(
            initialBufferAccessCapacity: 0);
        diagnostics.BeginRecording();
        diagnostics.RecordCopyTexture(
            source,
            1,
            2,
            3,
            4,
            5,
            destination,
            6,
            7,
            8,
            9,
            10,
            width,
            11,
            12,
            13);
        diagnostics.EndRecording();
        diagnostics.CompleteSuccessfulSubmission();
        return diagnostics.LastSubmittedMetrics.CommandSignature;
    }

    private sealed class StubBuffer(
        string name,
        uint sizeInBytes) : DeviceBuffer
    {
        private bool _isDisposed;

        public override uint SizeInBytes { get; } = sizeInBytes;
        public override BufferUsage Usage => BufferUsage.Dynamic;
        public override string Name { get; set; } = name;
        public override bool IsDisposed => _isDisposed;

        public override void Dispose()
        {
            _isDisposed = true;
        }
    }

    private sealed class StubResourceSet : ResourceSet
    {
        private bool _isDisposed;

        public StubResourceSet(string name)
            : this(name, new StubResourceLayout($"{name}-layout"))
        {
        }

        public StubResourceSet(string name, ResourceLayout layout)
            : this(name, new ResourceSetDescription(
                layout,
                System.Array.Empty<BindableResource>()))
        {
        }

        private StubResourceSet(
            string name,
            ResourceSetDescription description)
            : base(ref description)
        {
            Name = name;
        }

        public override string Name { get; set; }
        public override bool IsDisposed => _isDisposed;

        public override void Dispose()
        {
            _isDisposed = true;
        }
    }

    private sealed class StubResourceLayout : ResourceLayout
    {
        private bool _isDisposed;

        public StubResourceLayout(string name)
            : this(name, new ResourceLayoutDescription(
                System.Array.Empty<ResourceLayoutElementDescription>()))
        {
        }

        private StubResourceLayout(
            string name,
            ResourceLayoutDescription description)
            : base(ref description)
        {
            Name = name;
        }

        public override string Name { get; set; }
        public override bool IsDisposed => _isDisposed;

        public override void Dispose()
        {
            _isDisposed = true;
        }
    }

    private sealed class StubTexture(string name) : Texture
    {
        private bool _isDisposed;

        public override PixelFormat Format => PixelFormat.R8_G8_B8_A8_UNorm;
        public override uint Width => 16;
        public override uint Height => 16;
        public override uint Depth => 1;
        public override uint MipLevels => 1;
        public override uint ArrayLayers => 1;
        public override TextureUsage Usage => TextureUsage.Sampled;
        public override TextureType Type => TextureType.Texture2D;
        public override TextureSampleCount SampleCount =>
            TextureSampleCount.Count1;
        public override string Name { get; set; } = name;
        public override bool IsDisposed => _isDisposed;

        private protected override void DisposeCore()
        {
            _isDisposed = true;
        }
    }
}
