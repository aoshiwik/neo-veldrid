using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace NeoVeldrid.Tests;

public abstract partial class TextureTestBase<T> where T : GraphicsDeviceCreator
{
    public static IEnumerable<object[]> OrderedUploadShapeCases()
    {
        foreach (OrderedTextureUploadScenario scenario in OrderedTextureUploadMatrix.ShapeScenarios)
            yield return new object[] { scenario.Identity };
    }

    public static IEnumerable<object[]> OrderedUploadCompressedCases()
    {
        foreach (OrderedTextureUploadScenario scenario in OrderedTextureUploadMatrix.CompressedScenarios)
            yield return new object[] { scenario.Identity };
    }

    public static IEnumerable<object[]> OrderedUploadCopyOnlyUsageCases()
    {
        foreach (OrderedTextureUploadScenario scenario in OrderedTextureUploadMatrix.CopyOnlyUsageScenarios)
            yield return new object[] { scenario.Identity };
    }

    [SkippableTheory]
    [MemberData(nameof(OrderedUploadShapeCases))]
    public unsafe void CommandListOrderedTextureUploadShapeMatrixHasExactContents(
        string scenarioIdentity)
    {
        ExecuteOrderedUploadScenario(
            OrderedTextureUploadMatrix.GetShapeScenario(scenarioIdentity));
    }

    [SkippableTheory]
    [MemberData(nameof(OrderedUploadCompressedCases))]
    public unsafe void CommandListOrderedCompressedUploadMatrixHasExactBlocks(
        string scenarioIdentity)
    {
        ExecuteOrderedUploadScenario(
            OrderedTextureUploadMatrix.GetCompressedScenario(scenarioIdentity));
    }

    [SkippableTheory]
    [MemberData(nameof(OrderedUploadCopyOnlyUsageCases))]
    public unsafe void CommandListOrderedUploadSupportsZeroFlagCopyOnlyTextures(
        string scenarioIdentity)
    {
        ExecuteOrderedUploadScenario(
            OrderedTextureUploadMatrix.GetCopyOnlyUsageScenario(scenarioIdentity));
    }

    [SkippableFact]
    public void CommandListTextureUploadThenStorageReadIsOrdered()
    {
        const string identity =
            "usage=Storage|Sampled;workflow=upload-imageLoad-bufferReadback;format=R32_UInt";
        Skip.IfNot(
            GD.Features.ComputeShader,
            $"NV-SKIP-ORDERED-UPLOAD-USAGE: {identity}; compute shaders are unavailable on {GD.BackendType}.");

        TextureDescription description = TextureDescription.Texture2D(
            2,
            2,
            1,
            1,
            PixelFormat.R32_UInt,
            TextureUsage.Storage | TextureUsage.Sampled);
        RequireOrderedUploadSupport(description, identity, "storage destination");

        Texture destination = RF.CreateTexture(description);
        DeviceBuffer storageReadOutput = RF.CreateBuffer(new BufferDescription(
            16,
            BufferUsage.StructuredBufferReadWrite,
            16));
        DeviceBuffer readback = RF.CreateBuffer(new BufferDescription(
            16,
            BufferUsage.Staging));
        ResourceLayout layout = RF.CreateResourceLayout(
            new ResourceLayoutDescription(
                new ResourceLayoutElementDescription(
                    "InputTexture",
                    ResourceKind.TextureReadWrite,
                    ShaderStages.Compute),
                new ResourceLayoutElementDescription(
                    "OutputBuffer",
                    ResourceKind.StructuredBufferReadWrite,
                    ShaderStages.Compute)));
        ResourceSet resourceSet = RF.CreateResourceSet(
            new ResourceSetDescription(
                layout,
                destination,
                storageReadOutput));
        Pipeline pipeline = RF.CreateComputePipeline(
            new ComputePipelineDescription(
                TestShaders.LoadCompute(RF, "ComputeTextureStorageReader"),
                layout,
                1,
                1,
                1));
        uint[] expected =
        {
            0xA5C39E17u,
            0x27D14B63u,
            0xF08A35C1u,
            0x6197E24Du,
        };
        uint[] source = expected.ToArray();

        CommandList commandList = RF.CreateCommandList();
        commandList.EnableSubmissionDiagnostics(initialBufferAccessCapacity: 3);
        commandList.Begin();
        commandList.UpdateTexture(
            destination,
            source,
            0, 0, 0,
            2, 2, 1,
            0, 0);
        Array.Fill(source, 0u);
        commandList.SetPipeline(pipeline);
        commandList.SetComputeResourceSet(0, resourceSet);
        commandList.Dispatch(1, 1, 1);
        commandList.CopyBuffer(storageReadOutput, 0, readback, 0, 16);
        commandList.End();
        GD.SubmitCommands(commandList);
        GD.WaitForIdle();

        Assert.True(commandList.TryGetLastSubmissionMetrics(
            out CommandListSubmissionMetrics metrics));
        Assert.Equal(1, metrics.UpdateTextureCallCount);
        Assert.Equal(16UL, metrics.UpdatedTextureBytes);
        Assert.Equal(1, metrics.DispatchCallCount);
        Assert.Equal(1, metrics.CopyBufferCallCount);
        Assert.Equal(16UL, metrics.CopiedBufferBytes);
        Assert.Equal(0, metrics.GenerateMipmapsCallCount);

        MappedResourceView<uint> mapped =
            GD.Map<uint>(readback, MapMode.Read);
        try
        {
            for (int index = 0; index < expected.Length; index++)
                Assert.Equal(expected[index], mapped[index]);
        }
        finally
        {
            GD.Unmap(readback);
        }
    }

    [SkippableFact]
    public void CommandListTextureUploadGenerateMipmapsAndSampleAreOrdered()
    {
        const string identity =
            "usage=Sampled|GenerateMipmaps;workflow=upload-generate-textureLod2-bufferReadback;format=R8_G8_B8_A8_UNorm";
        Skip.IfNot(
            GD.Features.ComputeShader,
            $"NV-SKIP-ORDERED-UPLOAD-USAGE: {identity}; compute shaders are unavailable on {GD.BackendType}.");

        TextureDescription description = TextureDescription.Texture2D(
            8,
            8,
            4,
            1,
            PixelFormat.R8_G8_B8_A8_UNorm,
            TextureUsage.Sampled | TextureUsage.GenerateMipmaps);
        RequireOrderedUploadSupport(description, identity, "mipmap destination");

        Texture destination = RF.CreateTexture(description);
        DeviceBuffer sampledOutput = RF.CreateBuffer(new BufferDescription(
            16,
            BufferUsage.StructuredBufferReadWrite,
            16));
        DeviceBuffer readback = RF.CreateBuffer(new BufferDescription(
            16,
            BufferUsage.Staging));
        ResourceLayout layout = RF.CreateResourceLayout(
            new ResourceLayoutDescription(
                new ResourceLayoutElementDescription(
                    "InputTexture",
                    ResourceKind.TextureReadOnly,
                    ShaderStages.Compute),
                new ResourceLayoutElementDescription(
                    "InputSampler",
                    ResourceKind.Sampler,
                    ShaderStages.Compute),
                new ResourceLayoutElementDescription(
                    "OutputBuffer",
                    ResourceKind.StructuredBufferReadWrite,
                    ShaderStages.Compute)));
        ResourceSet resourceSet = RF.CreateResourceSet(
            new ResourceSetDescription(
                layout,
                destination,
                GD.PointSampler,
                sampledOutput));
        Pipeline pipeline = RF.CreateComputePipeline(
            new ComputePipelineDescription(
                TestShaders.LoadCompute(RF, "ComputeTextureMipSampler"),
                layout,
                1,
                1,
                1));
        RgbaByte expectedByte = new RgbaByte(173, 61, 229, 255);
        RgbaByte[] source = Enumerable.Repeat(expectedByte, 64).ToArray();
        RgbaByte sentinel = new RgbaByte(19, 83, 41, 255);
        GD.UpdateTexture(
            destination,
            Enumerable.Repeat(sentinel, 64).ToArray(),
            0, 0, 0,
            8, 8, 1,
            0, 0);
        GD.UpdateTexture(
            destination,
            Enumerable.Repeat(sentinel, 4).ToArray(),
            0, 0, 0,
            2, 2, 1,
            2, 0);

        CommandList commandList = RF.CreateCommandList();
        commandList.EnableSubmissionDiagnostics(initialBufferAccessCapacity: 3);
        commandList.Begin();
        commandList.UpdateTexture(
            destination,
            source,
            0, 0, 0,
            8, 8, 1,
            0, 0);
        Array.Fill(source, RgbaByte.Clear);
        commandList.GenerateMipmaps(destination);
        commandList.SetPipeline(pipeline);
        commandList.SetComputeResourceSet(0, resourceSet);
        commandList.Dispatch(1, 1, 1);
        commandList.CopyBuffer(sampledOutput, 0, readback, 0, 16);
        commandList.End();
        GD.SubmitCommands(commandList);
        GD.WaitForIdle();

        Assert.True(commandList.TryGetLastSubmissionMetrics(
            out CommandListSubmissionMetrics metrics));
        Assert.Equal(1, metrics.UpdateTextureCallCount);
        Assert.Equal(256UL, metrics.UpdatedTextureBytes);
        Assert.Equal(1, metrics.GenerateMipmapsCallCount);
        Assert.Equal(1, metrics.DispatchCallCount);
        Assert.Equal(1, metrics.CopyBufferCallCount);
        Assert.Equal(16UL, metrics.CopiedBufferBytes);

        MappedResourceView<RgbaFloat> mapped =
            GD.Map<RgbaFloat>(readback, MapMode.Read);
        try
        {
            RgbaFloat expected = new RgbaFloat(
                expectedByte.R / 255f,
                expectedByte.G / 255f,
                expectedByte.B / 255f,
                1f);
            Assert.Equal(
                expected,
                mapped[0],
                RgbaFloatFuzzyComparer.Instance);
        }
        finally
        {
            GD.Unmap(readback);
        }
    }

    [SkippableFact]
    public unsafe void CommandListMixedSizeTextureUploadsPreserveAdjacentAllocations()
    {
        IReadOnlyList<OrderedTextureUploadScenario> scenarios =
            OrderedTextureUploadMatrix.MixedSizeScenarios;
        Texture[] destinations = new Texture[scenarios.Count];
        Texture[] captures = new Texture[scenarios.Count];
        byte[][] sources = new byte[scenarios.Count][];
        byte[][] expected = new byte[scenarios.Count][];

        for (int index = 0; index < scenarios.Count; index++)
        {
            OrderedTextureUploadScenario scenario = scenarios[index];
            RequireOrderedUploadSupport(
                scenario.Description,
                OrderedTextureUploadMatrix.MixedSizeIdentity,
                $"{scenario.Description.Format} destination");
            TextureDescription captureDescription = scenario.CreateCaptureDescription();
            RequireOrderedUploadSupport(
                captureDescription,
                OrderedTextureUploadMatrix.MixedSizeIdentity,
                $"{scenario.Description.Format} staging capture");
            destinations[index] = RF.CreateTexture(scenario.Description);
            captures[index] = RF.CreateTexture(captureDescription);
            sources[index] = scenario.CreatePatchPattern(
                unchecked((byte)(0x21 + (index * 0x29))));
            expected[index] = sources[index].ToArray();
        }

        CommandList commandList = RF.CreateCommandList();
        commandList.EnableSubmissionDiagnostics(initialBufferAccessCapacity: 10);
        commandList.Begin();
        for (int index = 0; index < scenarios.Count; index++)
        {
            OrderedTextureUploadScenario scenario = scenarios[index];
            commandList.UpdateTexture(
                destinations[index],
                sources[index],
                scenario.X,
                scenario.Y,
                scenario.Z,
                scenario.Width,
                scenario.Height,
                scenario.Depth,
                scenario.MipLevel,
                scenario.ArrayLayer);
        }
        for (int index = 0; index < sources.Length; index++)
            Array.Fill(sources[index], (byte)0xEE);
        for (int index = 0; index < scenarios.Count; index++)
        {
            CopyScenarioSubresource(
                commandList,
                destinations[index],
                captures[index],
                scenarios[index],
                scenarios[index].ArrayLayer);
        }
        commandList.End();

        GD.SubmitCommands(commandList);
        GD.WaitForIdle();

        Assert.True(commandList.TryGetLastSubmissionMetrics(
            out CommandListSubmissionMetrics metrics));
        Assert.Equal(5, metrics.UpdateTextureCallCount);
        Assert.Equal(45UL, metrics.UpdatedTextureBytes);
        Assert.Equal(5, metrics.CopyTextureCallCount);
        for (int index = 0; index < scenarios.Count; index++)
        {
            AssertScenarioSubresource(
                captures[index],
                scenarios[index],
                scenarios[index].ArrayLayer,
                expected[index]);
        }
    }

    private unsafe void ExecuteOrderedUploadScenario(
        OrderedTextureUploadScenario scenario)
    {
        TextureDescription destinationDescription = scenario.Description;
        RequireOrderedUploadSupport(
            destinationDescription,
            scenario.Identity,
            "destination");

        TextureDescription captureDescription = scenario.CreateCaptureDescription();
        RequireOrderedUploadSupport(
            captureDescription,
            scenario.Identity,
            "staging capture");

        Texture destination = RF.CreateTexture(destinationDescription);
        Texture capture = RF.CreateTexture(captureDescription);

        scenario.GetMipDimensions(out uint mipWidth, out uint mipHeight, out uint mipDepth);
        byte[] selectedBaseline = scenario.CreateSubresourcePattern(0x31);
        byte[] selectedExpected = selectedBaseline.ToArray();
        GD.UpdateTexture(
            destination,
            selectedBaseline,
            0,
            0,
            0,
            mipWidth,
            mipHeight,
            mipDepth,
            scenario.MipLevel,
            scenario.ArrayLayer);

        byte[] neighborExpected = null;
        if (scenario.NeighborArrayLayer.HasValue)
        {
            neighborExpected = scenario.CreateSubresourcePattern(0xA7);
            GD.UpdateTexture(
                destination,
                neighborExpected,
                0,
                0,
                0,
                mipWidth,
                mipHeight,
                mipDepth,
                scenario.MipLevel,
                scenario.NeighborArrayLayer.Value);
        }

        byte[] patch = scenario.CreatePatchPattern(0xD3);
        byte[] retainedPatch = patch.ToArray();
        scenario.ApplyPatch(selectedExpected, retainedPatch);

        CommandList commandList = RF.CreateCommandList();
        commandList.EnableSubmissionDiagnostics(initialBufferAccessCapacity: 2);
        commandList.Begin();
        commandList.UpdateTexture(
            destination,
            patch,
            scenario.X,
            scenario.Y,
            scenario.Z,
            scenario.Width,
            scenario.Height,
            scenario.Depth,
            scenario.MipLevel,
            scenario.ArrayLayer);
        Array.Fill(patch, (byte)0xEE);
        CopyScenarioSubresource(
            commandList,
            destination,
            capture,
            scenario,
            scenario.ArrayLayer);
        if (scenario.NeighborArrayLayer.HasValue)
        {
            CopyScenarioSubresource(
                commandList,
                destination,
                capture,
                scenario,
                scenario.NeighborArrayLayer.Value);
        }
        commandList.End();

        GD.SubmitCommands(commandList);
        GD.WaitForIdle();

        Assert.True(commandList.TryGetLastSubmissionMetrics(
            out CommandListSubmissionMetrics metrics));
        Assert.Equal(1, metrics.UpdateTextureCallCount);
        Assert.Equal((ulong)retainedPatch.Length, metrics.UpdatedTextureBytes);
        Assert.Equal(
            scenario.NeighborArrayLayer.HasValue ? 2 : 1,
            metrics.CopyTextureCallCount);

        AssertScenarioSubresource(
            capture,
            scenario,
            scenario.ArrayLayer,
            selectedExpected);
        if (scenario.NeighborArrayLayer.HasValue)
        {
            AssertScenarioSubresource(
                capture,
                scenario,
                scenario.NeighborArrayLayer.Value,
                neighborExpected);
        }
    }

    private void RequireOrderedUploadSupport(
        in TextureDescription description,
        string scenarioIdentity,
        string role)
    {
        TextureSupportResult result = GD.GetTextureSupport(description);
        if (!result.IsSupported)
        {
            Assert.True(
                result.Classification == TextureSupportClassification.BackendContract
                    || result.Classification == TextureSupportClassification.DeviceCapability,
                $"The authoritative ordered-upload matrix contains an invalid {role} description: "
                    + $"{scenarioIdentity}; {result}.");
        }

        Skip.IfNot(
            result.IsSupported,
            $"NV-SKIP-ORDERED-UPLOAD-MATRIX: {scenarioIdentity}; {role} is {result} on {GD.BackendType}.");
    }

    private static void CopyScenarioSubresource(
        CommandList commandList,
        Texture source,
        Texture destination,
        OrderedTextureUploadScenario scenario,
        uint arrayLayer)
    {
        scenario.GetMipDimensions(out uint width, out uint height, out uint depth);
        commandList.CopyTexture(
            source,
            0,
            0,
            0,
            scenario.MipLevel,
            arrayLayer,
            destination,
            0,
            0,
            0,
            scenario.MipLevel,
            arrayLayer,
            width,
            height,
            depth,
            1);
    }

    private unsafe void AssertScenarioSubresource(
        Texture capture,
        OrderedTextureUploadScenario scenario,
        uint arrayLayer,
        byte[] expected)
    {
        uint subresource = capture.CalculateSubresource(
            scenario.MipLevel,
            arrayLayer);
        MappedResource mapped = GD.Map(capture, MapMode.Read, subresource);
        try
        {
            scenario.GetMipDimensions(out uint width, out uint height, out uint depth);
            uint rowBytes = scenario.Storage.GetRowSize(width);
            uint rowCount = scenario.Storage.GetRowCount(height);
            uint tightDepthPitch = checked(rowBytes * rowCount);
            byte* actualBase = (byte*)mapped.Data;

            for (uint z = 0; z < depth; z++)
            {
                for (uint row = 0; row < rowCount; row++)
                {
                    uint expectedOffset = checked((z * tightDepthPitch) + (row * rowBytes));
                    nuint actualOffset = checked(
                        ((nuint)z * mapped.DepthPitch)
                        + ((nuint)row * mapped.RowPitch));
                    for (uint byteIndex = 0; byteIndex < rowBytes; byteIndex++)
                    {
                        Assert.Equal(
                            expected[checked((int)(expectedOffset + byteIndex))],
                            *(actualBase + checked((nint)(actualOffset + byteIndex))));
                    }
                }
            }
        }
        finally
        {
            GD.Unmap(capture, subresource);
        }
    }
}

public sealed class OrderedTextureUploadMatrixDefinitionTests
{
    [Fact]
    public void TextureDescriptionValidationReturnsStructuredContractOwnership()
    {
        TextureSupportResult uninitialized = default;
        Assert.False(uninitialized.IsSupported);
        Assert.Equal(
            TextureSupportClassification.Uninitialized,
            uninitialized.Classification);

        TextureDescription nonSquareCube = TextureDescription.Texture2D(
            8,
            4,
            1,
            1,
            PixelFormat.R8_UNorm,
            TextureUsage.Cubemap);
        TextureSupportResult invalid =
            TextureDescriptionValidation.Query(nonSquareCube);
        Assert.False(invalid.IsSupported);
        Assert.Equal(
            TextureSupportClassification.InvalidDescription,
            invalid.Classification);
        Assert.Equal(
            TextureSupportReason.CubemapSquareFaces,
            invalid.Reason);

        TextureDescription packedStaging = TextureDescription.Texture2D(
            4,
            4,
            1,
            1,
            PixelFormat.D24_UNorm_S8_UInt,
            TextureUsage.Staging);
        TextureSupportResult libraryContract =
            TextureDescriptionValidation.Query(packedStaging);
        Assert.False(libraryContract.IsSupported);
        Assert.Equal(
            TextureSupportClassification.LibraryContract,
            libraryContract.Classification);
        Assert.Equal(
            TextureSupportReason.PackedDepthStencilStaging,
            libraryContract.Reason);

        TextureDescription multisampledStaging = new TextureDescription(
            4,
            4,
            1,
            1,
            1,
            PixelFormat.R8_UNorm,
            TextureUsage.Staging,
            TextureType.Texture2D,
            TextureSampleCount.Count2);
        libraryContract =
            TextureDescriptionValidation.Query(multisampledStaging);
        Assert.False(libraryContract.IsSupported);
        Assert.Equal(
            TextureSupportClassification.LibraryContract,
            libraryContract.Classification);
        Assert.Equal(
            TextureSupportReason.StagingMultisample,
            libraryContract.Reason);

        TextureDescription colorDepthStencil =
            TextureDescription.Texture2D(
                4,
                4,
                1,
                1,
                PixelFormat.R8_UNorm,
                TextureUsage.DepthStencil);
        libraryContract =
            TextureDescriptionValidation.Query(colorDepthStencil);
        Assert.False(libraryContract.IsSupported);
        Assert.Equal(
            TextureSupportClassification.LibraryContract,
            libraryContract.Classification);
        Assert.Equal(
            TextureSupportReason.DepthStencilFormat,
            libraryContract.Reason);
        Assert.Equal(
            "TextureUsage.DepthStencil requires a depth-stencil format.",
            TextureDescriptionValidation.GetFailureMessage(
                libraryContract.Reason));

        TextureDescription copyOnly = TextureDescription.Texture2D(
            4,
            4,
            1,
            1,
            PixelFormat.R8_UNorm,
            (TextureUsage)0);
        Assert.True(TextureDescriptionValidation.Query(copyOnly).IsSupported);

        TextureDescription mipmappedArray = TextureDescription.Texture2D(
            8,
            4,
            4,
            3,
            PixelFormat.R16_UNorm,
            TextureUsage.Sampled);
        Assert.Equal(258UL, GetMinimumStorageFootprint(mipmappedArray));

        TextureDescription compressedCubeArray = TextureDescription.Texture2D(
            13,
            13,
            4,
            2,
            PixelFormat.BC1_Rgba_UNorm,
            TextureUsage.Cubemap | TextureUsage.Sampled);
        Assert.Equal(
            2_112UL,
            GetMinimumStorageFootprint(compressedCubeArray));

        TextureDescription volume = TextureDescription.Texture3D(
            8,
            4,
            2,
            4,
            PixelFormat.R8_UNorm,
            TextureUsage.Sampled);
        Assert.Equal(75UL, GetMinimumStorageFootprint(volume));

        TextureDescription multisampled = TextureDescription.Texture2D(
            4,
            4,
            1,
            1,
            PixelFormat.R8_UNorm,
            TextureUsage.RenderTarget,
            TextureSampleCount.Count4);
        Assert.Equal(64UL, GetMinimumStorageFootprint(multisampled));

        TextureDescription depthStencil =
            TextureDescription.Texture2D(
                1,
                1,
                1,
                1,
                PixelFormat.D32_Float_S8_UInt,
                TextureUsage.DepthStencil);
        Assert.Equal(
            5UL,
            GetMinimumStorageFootprint(depthStencil));

        TextureDescription overflowingVolume = TextureDescription.Texture3D(
            uint.MaxValue,
            uint.MaxValue,
            uint.MaxValue,
            1,
            PixelFormat.R32_G32_B32_A32_Float,
            TextureUsage.Sampled);
        Assert.False(
            TextureStorageFootprint.TryCalculateMinimumSizeInBytes(
                overflowingVolume,
                out _));

        static ulong GetMinimumStorageFootprint(
            in TextureDescription description)
        {
            Assert.True(
                TextureStorageFootprint.TryCalculateMinimumSizeInBytes(
                    description,
                    out ulong footprint));
            return footprint;
        }
    }

    [Fact]
    public void ShapeMatrixDefinesEveryRequiredRoutingAndTexelSizeAxis()
    {
        IReadOnlyList<OrderedTextureUploadScenario> scenarios =
            OrderedTextureUploadMatrix.ShapeScenarios;

        Assert.Equal(scenarios.Count, scenarios.Select(scenario => scenario.Identity).Distinct().Count());
        Assert.Contains(scenarios, scenario => scenario.Description.Type == TextureType.Texture1D
            && scenario.Description.ArrayLayers == 1);
        Assert.Contains(scenarios, scenario => scenario.Description.Type == TextureType.Texture1D
            && scenario.Description.ArrayLayers > 1);
        Assert.Contains(scenarios, scenario => scenario.Description.Type == TextureType.Texture2D
            && scenario.Description.ArrayLayers > 1
            && (scenario.Description.Usage & TextureUsage.Cubemap) == 0);
        Assert.Contains(scenarios, scenario => scenario.Description.Type == TextureType.Texture3D
            && scenario.Z > 0
            && scenario.Depth > 1);
        Assert.Contains(scenarios, scenario => (scenario.Description.Usage & TextureUsage.Cubemap) != 0
            && scenario.Description.ArrayLayers == 1);
        Assert.Contains(scenarios, scenario => (scenario.Description.Usage & TextureUsage.Cubemap) != 0
            && scenario.Description.ArrayLayers > 1);

        uint[] requiredTexelSizes = { 1, 2, 4, 8, 16 };
        Assert.Equal(
            requiredTexelSizes,
            scenarios
                .Select(scenario => scenario.Storage.BytesPerUnit)
                .Distinct()
                .OrderBy(size => size)
                .ToArray());
        Assert.All(scenarios, scenario => Assert.True(scenario.MipLevel > 0));
    }

    [Fact]
    public void CompressedAndMixedMatricesDefineRequiredStorageAlignmentAxes()
    {
        Assert.Equal(
            new uint[] { 8, 16 },
            OrderedTextureUploadMatrix.CompressedScenarios
                .Select(scenario => scenario.Storage.BytesPerUnit)
                .OrderBy(size => size)
                .ToArray());
        Assert.Equal(
            new[]
            {
                PixelFormat.R8_UNorm,
                PixelFormat.R16_UNorm,
                PixelFormat.R8_G8_B8_A8_UNorm,
                PixelFormat.BC1_Rgba_UNorm,
                PixelFormat.BC3_UNorm,
            },
            OrderedTextureUploadMatrix.MixedSizeScenarios
                .Select(scenario => scenario.Description.Format)
                .ToArray());
        Assert.Equal(
            new[] { 3, 6, 12, 8, 16 },
            OrderedTextureUploadMatrix.MixedSizeScenarios
                .Select(scenario => checked((int)scenario.CreatePatchPattern(0).Length))
                .ToArray());

        OrderedTextureUploadScenario copyOnly =
            Assert.Single(OrderedTextureUploadMatrix.CopyOnlyUsageScenarios);
        Assert.Equal((TextureUsage)0, copyOnly.Description.Usage);
    }
}

internal static class OrderedTextureUploadMatrix
{
    internal const string MixedSizeIdentity =
        "mixed=R8:3,R16:6,RGBA8:12,BC1:8,BC3:16;aligned-starts=0,4,12,24,32";

    internal static IReadOnlyList<OrderedTextureUploadScenario> ShapeScenarios { get; } =
        new[]
        {
            new OrderedTextureUploadScenario(
                "shape=1d;format=R16_UNorm;texel=2;mip=1;layer=0;region=3,0,0+3x1x1",
                new TextureDescription(
                    16, 1, 1, 3, 1,
                    PixelFormat.R16_UNorm,
                    TextureUsage.Sampled,
                    TextureType.Texture1D),
                new TextureStorageUnit(1, 1, 2),
                mipLevel: 1,
                arrayLayer: 0,
                neighborArrayLayer: null,
                x: 3, y: 0, z: 0,
                width: 3, height: 1, depth: 1),
            new OrderedTextureUploadScenario(
                "shape=1d-array;format=R8_G8_B8_A8_UNorm;texel=4;mip=1;layer=1;region=2,0,0+4x1x1",
                new TextureDescription(
                    16, 1, 1, 3, 3,
                    PixelFormat.R8_G8_B8_A8_UNorm,
                    TextureUsage.Sampled,
                    TextureType.Texture1D),
                new TextureStorageUnit(1, 1, 4),
                mipLevel: 1,
                arrayLayer: 1,
                neighborArrayLayer: 0,
                x: 2, y: 0, z: 0,
                width: 4, height: 1, depth: 1),
            new OrderedTextureUploadScenario(
                "shape=2d-array;format=R32_G32_Float;texel=8;mip=1;layer=2;region=2,1,0+3x3x1",
                new TextureDescription(
                    12, 10, 1, 3, 3,
                    PixelFormat.R32_G32_Float,
                    TextureUsage.Sampled,
                    TextureType.Texture2D),
                new TextureStorageUnit(1, 1, 8),
                mipLevel: 1,
                arrayLayer: 2,
                neighborArrayLayer: 1,
                x: 2, y: 1, z: 0,
                width: 3, height: 3, depth: 1),
            new OrderedTextureUploadScenario(
                "shape=3d;format=R8_G8_B8_A8_UNorm;texel=4;mip=1;layer=0;region=1,1,1+3x3x2",
                new TextureDescription(
                    12, 10, 8, 2, 1,
                    PixelFormat.R8_G8_B8_A8_UNorm,
                    TextureUsage.Sampled,
                    TextureType.Texture3D),
                new TextureStorageUnit(1, 1, 4),
                mipLevel: 1,
                arrayLayer: 0,
                neighborArrayLayer: null,
                x: 1, y: 1, z: 1,
                width: 3, height: 3, depth: 2),
            new OrderedTextureUploadScenario(
                "shape=cube;format=R8_UNorm;texel=1;mip=1;face=4;region=1,2,0+4x3x1",
                new TextureDescription(
                    12, 12, 1, 3, 1,
                    PixelFormat.R8_UNorm,
                    TextureUsage.Sampled | TextureUsage.Cubemap,
                    TextureType.Texture2D),
                new TextureStorageUnit(1, 1, 1),
                mipLevel: 1,
                arrayLayer: 4,
                neighborArrayLayer: 3,
                x: 1, y: 2, z: 0,
                width: 4, height: 3, depth: 1),
            new OrderedTextureUploadScenario(
                "shape=cube-array;format=R32_G32_B32_A32_Float;texel=16;mip=1;face-layer=8;region=2,1,0+3x4x1",
                new TextureDescription(
                    12, 12, 1, 3, 2,
                    PixelFormat.R32_G32_B32_A32_Float,
                    TextureUsage.Sampled | TextureUsage.Cubemap,
                    TextureType.Texture2D),
                new TextureStorageUnit(1, 1, 16),
                mipLevel: 1,
                arrayLayer: 8,
                neighborArrayLayer: 7,
                x: 2, y: 1, z: 0,
                width: 3, height: 4, depth: 1),
        };

    internal static IReadOnlyList<OrderedTextureUploadScenario> CompressedScenarios { get; } =
        new[]
        {
            new OrderedTextureUploadScenario(
                "shape=2d;format=BC1_Rgba_UNorm;block=8;mip=0;layer=0;region=4,4,0+10x9x1",
                new TextureDescription(
                    14, 13, 1, 1, 1,
                    PixelFormat.BC1_Rgba_UNorm,
                    TextureUsage.Sampled,
                    TextureType.Texture2D),
                new TextureStorageUnit(4, 4, 8),
                mipLevel: 0,
                arrayLayer: 0,
                neighborArrayLayer: null,
                x: 4, y: 4, z: 0,
                width: 10, height: 9, depth: 1),
            new OrderedTextureUploadScenario(
                "shape=2d;format=BC3_UNorm;block=16;mip=1;layer=0;region=4,4,0+6x6x1",
                new TextureDescription(
                    21, 21, 1, 2, 1,
                    PixelFormat.BC3_UNorm,
                    TextureUsage.Sampled,
                    TextureType.Texture2D),
                new TextureStorageUnit(4, 4, 16),
                mipLevel: 1,
                arrayLayer: 0,
                neighborArrayLayer: null,
                x: 4, y: 4, z: 0,
                width: 6, height: 6, depth: 1),
        };

    internal static IReadOnlyList<OrderedTextureUploadScenario> MixedSizeScenarios { get; } =
        new[]
        {
            CreateFullUpload(
                "mixed-r8",
                PixelFormat.R8_UNorm,
                width: 3,
                height: 1,
                new TextureStorageUnit(1, 1, 1)),
            CreateFullUpload(
                "mixed-r16",
                PixelFormat.R16_UNorm,
                width: 3,
                height: 1,
                new TextureStorageUnit(1, 1, 2)),
            CreateFullUpload(
                "mixed-rgba8",
                PixelFormat.R8_G8_B8_A8_UNorm,
                width: 3,
                height: 1,
                new TextureStorageUnit(1, 1, 4)),
            CreateFullUpload(
                "mixed-bc1",
                PixelFormat.BC1_Rgba_UNorm,
                width: 4,
                height: 4,
                new TextureStorageUnit(4, 4, 8)),
            CreateFullUpload(
                "mixed-bc3",
                PixelFormat.BC3_UNorm,
                width: 4,
                height: 4,
                new TextureStorageUnit(4, 4, 16)),
        };

    internal static IReadOnlyList<OrderedTextureUploadScenario> CopyOnlyUsageScenarios { get; } =
        new[]
        {
            new OrderedTextureUploadScenario(
                "usage=None;workflow=upload-copy-readback;format=R8_G8_UNorm;mip=1;region=1,1,0+3x2x1",
                new TextureDescription(
                    10, 8, 1, 2, 1,
                    PixelFormat.R8_G8_UNorm,
                    (TextureUsage)0,
                    TextureType.Texture2D),
                new TextureStorageUnit(1, 1, 2),
                mipLevel: 1,
                arrayLayer: 0,
                neighborArrayLayer: null,
                x: 1, y: 1, z: 0,
                width: 3, height: 2, depth: 1),
        };

    private static readonly IReadOnlyDictionary<string, OrderedTextureUploadScenario>
        s_shapeScenariosByIdentity = ShapeScenarios.ToDictionary(scenario => scenario.Identity);
    private static readonly IReadOnlyDictionary<string, OrderedTextureUploadScenario>
        s_compressedScenariosByIdentity = CompressedScenarios.ToDictionary(scenario => scenario.Identity);
    private static readonly IReadOnlyDictionary<string, OrderedTextureUploadScenario>
        s_copyOnlyUsageScenariosByIdentity = CopyOnlyUsageScenarios.ToDictionary(scenario => scenario.Identity);

    internal static OrderedTextureUploadScenario GetShapeScenario(string identity)
    {
        Assert.True(
            s_shapeScenariosByIdentity.TryGetValue(identity, out OrderedTextureUploadScenario scenario),
            $"Unknown ordered texture upload matrix identity: {identity}");
        return scenario;
    }

    internal static OrderedTextureUploadScenario GetCompressedScenario(string identity)
    {
        Assert.True(
            s_compressedScenariosByIdentity.TryGetValue(identity, out OrderedTextureUploadScenario scenario),
            $"Unknown ordered compressed texture upload matrix identity: {identity}");
        return scenario;
    }

    internal static OrderedTextureUploadScenario GetCopyOnlyUsageScenario(string identity)
    {
        Assert.True(
            s_copyOnlyUsageScenariosByIdentity.TryGetValue(identity, out OrderedTextureUploadScenario scenario),
            $"Unknown ordered copy-only texture upload matrix identity: {identity}");
        return scenario;
    }

    private static OrderedTextureUploadScenario CreateFullUpload(
        string identity,
        PixelFormat format,
        uint width,
        uint height,
        TextureStorageUnit storage) =>
        new OrderedTextureUploadScenario(
            identity,
            TextureDescription.Texture2D(
                width,
                height,
                1,
                1,
                format,
                TextureUsage.Sampled),
            storage,
            mipLevel: 0,
            arrayLayer: 0,
            neighborArrayLayer: null,
            x: 0,
            y: 0,
            z: 0,
            width,
            height,
            depth: 1);
}

internal sealed class OrderedTextureUploadScenario
{
    internal string Identity { get; }
    internal TextureDescription Description { get; }
    internal TextureStorageUnit Storage { get; }
    internal uint MipLevel { get; }
    internal uint ArrayLayer { get; }
    internal uint? NeighborArrayLayer { get; }
    internal uint X { get; }
    internal uint Y { get; }
    internal uint Z { get; }
    internal uint Width { get; }
    internal uint Height { get; }
    internal uint Depth { get; }

    internal OrderedTextureUploadScenario(
        string identity,
        TextureDescription description,
        TextureStorageUnit storage,
        uint mipLevel,
        uint arrayLayer,
        uint? neighborArrayLayer,
        uint x,
        uint y,
        uint z,
        uint width,
        uint height,
        uint depth)
    {
        Identity = identity;
        Description = description;
        Storage = storage;
        MipLevel = mipLevel;
        ArrayLayer = arrayLayer;
        NeighborArrayLayer = neighborArrayLayer;
        X = x;
        Y = y;
        Z = z;
        Width = width;
        Height = height;
        Depth = depth;
    }

    internal TextureDescription CreateCaptureDescription()
    {
        uint physicalArrayLayers =
            (Description.Usage & TextureUsage.Cubemap) != 0
                ? checked(Description.ArrayLayers * 6u)
                : Description.ArrayLayers;
        return new TextureDescription(
            Description.Width,
            Description.Height,
            Description.Depth,
            Description.MipLevels,
            physicalArrayLayers,
            Description.Format,
            TextureUsage.Staging,
            Description.Type,
            TextureSampleCount.Count1);
    }

    internal byte[] CreateSubresourcePattern(byte seed)
    {
        GetMipDimensions(out uint width, out uint height, out uint depth);
        return CreatePattern(Storage.GetRegionSize(width, height, depth), seed);
    }

    internal byte[] CreatePatchPattern(byte seed) =>
        CreatePattern(Storage.GetRegionSize(Width, Height, Depth), seed);

    internal void ApplyPatch(byte[] destination, byte[] patch)
    {
        GetMipDimensions(out uint mipWidth, out uint mipHeight, out _);
        uint destinationRowBytes = Storage.GetRowSize(mipWidth);
        uint destinationRows = Storage.GetRowCount(mipHeight);
        uint destinationDepthPitch = checked(destinationRowBytes * destinationRows);
        uint patchRowBytes = Storage.GetRowSize(Width);
        uint patchRows = Storage.GetRowCount(Height);
        uint patchDepthPitch = checked(patchRowBytes * patchRows);
        uint destinationBlockX = X / Storage.BlockWidth;
        uint destinationBlockY = Y / Storage.BlockHeight;

        for (uint z = 0; z < Depth; z++)
        {
            for (uint row = 0; row < patchRows; row++)
            {
                uint destinationOffset = checked(
                    ((Z + z) * destinationDepthPitch)
                    + ((destinationBlockY + row) * destinationRowBytes)
                    + (destinationBlockX * Storage.BytesPerUnit));
                uint patchOffset = checked((z * patchDepthPitch) + (row * patchRowBytes));
                Array.Copy(
                    patch,
                    checked((int)patchOffset),
                    destination,
                    checked((int)destinationOffset),
                    checked((int)patchRowBytes));
            }
        }
    }

    internal void GetMipDimensions(out uint width, out uint height, out uint depth)
    {
        width = GetMipDimension(Description.Width, MipLevel);
        height = GetMipDimension(Description.Height, MipLevel);
        depth = GetMipDimension(Description.Depth, MipLevel);
    }

    private static byte[] CreatePattern(uint sizeInBytes, byte seed)
    {
        byte[] data = new byte[checked((int)sizeInBytes)];
        for (int index = 0; index < data.Length; index++)
            data[index] = unchecked((byte)(seed + (index * 37) + (index / 7)));
        return data;
    }

    private static uint GetMipDimension(uint value, uint mipLevel) =>
        Math.Max(1u, value >> checked((int)mipLevel));
}

internal readonly struct TextureStorageUnit
{
    internal uint BlockWidth { get; }
    internal uint BlockHeight { get; }
    internal uint BytesPerUnit { get; }

    internal TextureStorageUnit(
        uint blockWidth,
        uint blockHeight,
        uint bytesPerUnit)
    {
        BlockWidth = blockWidth;
        BlockHeight = blockHeight;
        BytesPerUnit = bytesPerUnit;
    }

    internal uint GetRowSize(uint logicalWidth) =>
        checked(DivideRoundUp(logicalWidth, BlockWidth) * BytesPerUnit);

    internal uint GetRowCount(uint logicalHeight) =>
        DivideRoundUp(logicalHeight, BlockHeight);

    internal uint GetRegionSize(uint width, uint height, uint depth) =>
        checked(GetRowSize(width) * GetRowCount(height) * depth);

    private static uint DivideRoundUp(uint value, uint divisor) =>
        checked((uint)(((ulong)value + divisor - 1u) / divisor));
}

#if TEST_OPENGLES
public partial class OpenGLESTextureTests
{
    [SkippableFact]
    public unsafe void CommandListOrderedEtc2UploadHasExactNativeBlocks()
    {
        const string identity =
            "shape=2d;format=ETC2_R8_G8_B8_A8_UNorm;block=16;mip=0;layer=0;region=0,0,0+8x8x1";
        TextureDescription destinationDescription = TextureDescription.Texture2D(
            8,
            8,
            1,
            1,
            PixelFormat.ETC2_R8_G8_B8_A8_UNorm,
            TextureUsage.Sampled);
        TextureSupportResult destinationSupport =
            GD.GetTextureSupport(destinationDescription);
        Assert.True(
            destinationSupport.IsSupported
                || destinationSupport.Classification == TextureSupportClassification.BackendContract
                || destinationSupport.Classification == TextureSupportClassification.DeviceCapability,
            $"The ETC2 matrix description is invalid: {destinationSupport}.");
        Skip.IfNot(
            destinationSupport.IsSupported,
            $"NV-SKIP-ORDERED-ETC2: {identity}; destination is {destinationSupport} on {GD.BackendType}.");

        bool hasCopyImage =
            GraphicsApiVersion.TryParseGLVersion(
                GD.GetOpenGLInfo().Version,
                out GraphicsApiVersion apiVersion)
            && (apiVersion.Major > 3
                || apiVersion.Major == 3 && apiVersion.Minor >= 2);
        Skip.IfNot(
            hasCopyImage,
            $"NV-SKIP-ORDERED-ETC2: {identity}; OpenGL ES 3.2 CopyImageSubData is unavailable.");

        TextureDescription captureDescription = TextureDescription.Texture2D(
            8,
            8,
            1,
            1,
            PixelFormat.R32_G32_B32_A32_UInt,
            TextureUsage.Staging);
        TextureSupportResult captureSupport = GD.GetTextureSupport(captureDescription);
        Assert.True(
            captureSupport.IsSupported
                || captureSupport.Classification == TextureSupportClassification.BackendContract
                || captureSupport.Classification == TextureSupportClassification.DeviceCapability,
            $"The ETC2 raw capture description is invalid: {captureSupport}.");
        Skip.IfNot(
            captureSupport.IsSupported,
            $"NV-SKIP-ORDERED-ETC2: {identity}; raw-block staging capture is {captureSupport} on {GD.BackendType}.");

        Texture destination = RF.CreateTexture(destinationDescription);
        Texture capture = RF.CreateTexture(captureDescription);
        byte[] source = new byte[64];
        for (int index = 0; index < source.Length; index++)
            source[index] = unchecked((byte)(0x43 + (index * 31) + (index / 5)));
        byte[] expected = source.ToArray();

        CommandList commandList = RF.CreateCommandList();
        commandList.EnableSubmissionDiagnostics(initialBufferAccessCapacity: 2);
        commandList.Begin();
        commandList.UpdateTexture(
            destination,
            source,
            0, 0, 0,
            8, 8, 1,
            0, 0);
        Array.Fill(source, (byte)0xEE);
        commandList.CopyTexture(
            destination,
            0, 0, 0, 0, 0,
            capture,
            0, 0, 0, 0, 0,
            8, 8, 1, 1);
        commandList.End();
        GD.SubmitCommands(commandList);
        GD.WaitForIdle();

        Assert.True(commandList.TryGetLastSubmissionMetrics(
            out CommandListSubmissionMetrics metrics));
        Assert.Equal(1, metrics.UpdateTextureCallCount);
        Assert.Equal(64UL, metrics.UpdatedTextureBytes);
        Assert.Equal(1, metrics.CopyTextureCallCount);

        MappedResource mapped = GD.Map(capture, MapMode.Read);
        try
        {
            const uint blockRows = 2;
            const uint rowBytes = 32;
            byte* actualBase = (byte*)mapped.Data;
            for (uint row = 0; row < blockRows; row++)
            {
                for (uint byteIndex = 0; byteIndex < rowBytes; byteIndex++)
                {
                    Assert.Equal(
                        expected[checked((int)((row * rowBytes) + byteIndex))],
                        *(actualBase + checked((nint)(
                            ((nuint)row * mapped.RowPitch) + byteIndex))));
                }
            }
        }
        finally
        {
            GD.Unmap(capture);
        }
    }
}
#endif
