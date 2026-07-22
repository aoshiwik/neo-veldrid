#if TEST_D3D11
using System.Text;
using NeoVeldrid.D3D11;
using NeoVeldrid.SPIRV;
using Xunit;

namespace NeoVeldrid.Tests;

[Trait("Backend", "D3D11")]
public sealed class D3D11CommandListBufferRangeTests
{
    [Theory]
    [InlineData(0u, 256u)]
    [InlineData(1u, 256u)]
    [InlineData(256u, 256u)]
    [InlineData(272u, 512u)]
    [InlineData(336u, 512u)]
    [InlineData(512u, 512u)]
    [InlineData(656u, 768u)]
    [InlineData(65536u, 65536u)]
    public void ConstantBufferRangeBindingSizeUsesD3D11RequiredAlignment(
        uint requestedSize,
        uint expectedBindingSize)
    {
        Assert.Equal(
            expectedBindingSize,
            D3D11CommandList.CalculateConstantBufferRangeBindingSize(
                requestedSize));
    }
}

[Trait("Backend", "D3D11")]
public sealed class D3D11CommandListBufferRangeIntegrationTests
    : GraphicsDeviceTestBase<D3D11DeviceCreator>
{
    private const string ReadUniformRangeShader = @"
#version 450
layout(set = 0, binding = 0) uniform Parameters
{
    uvec4 Prefix[16];
    uvec4 Tail;
};
layout(set = 0, binding = 1) buffer ResultBuffer
{
    uint Result;
};
layout(local_size_x = 1, local_size_y = 1, local_size_z = 1) in;
void main()
{
    Result = Tail.x;
}";

    [Fact]
    public void BindsTwoHundredSeventyTwoByteConstantBufferRange()
    {
        const uint rangeOffset = 256u;
        const uint rangeSize = 272u;
        const uint tailOffsetInRange = 256u;
        const uint expectedValue = 0x1234ABCDu;
        const uint valueAtWrongBase = 0xDEADBEEFu;
        const uint initialResult = 0xBAD0C0DEu;

        using Shader computeShader = RF.CreateFromSpirv(new ShaderDescription(
            ShaderStages.Compute,
            Encoding.ASCII.GetBytes(ReadUniformRangeShader),
            "main"));
        using ResourceLayout resourceLayout = RF.CreateResourceLayout(
            new ResourceLayoutDescription(
                new ResourceLayoutElementDescription(
                    "Parameters",
                    ResourceKind.UniformBuffer,
                    ShaderStages.Compute),
                new ResourceLayoutElementDescription(
                    "ResultBuffer",
                    ResourceKind.StructuredBufferReadWrite,
                    ShaderStages.Compute)));
        using Pipeline pipeline = RF.CreateComputePipeline(
            new ComputePipelineDescription(
                computeShader,
                resourceLayout,
                threadGroupSizeX: 1,
                threadGroupSizeY: 1,
                threadGroupSizeZ: 1));
        using DeviceBuffer uniformBuffer = RF.CreateBuffer(new BufferDescription(
            rangeOffset + rangeSize,
            BufferUsage.UniformBuffer));
        using DeviceBuffer resultBuffer = RF.CreateBuffer(new BufferDescription(
            sizeof(uint),
            BufferUsage.StructuredBufferReadWrite,
            structureByteStride: sizeof(uint)));
        using DeviceBuffer readbackBuffer = RF.CreateBuffer(new BufferDescription(
            sizeof(uint),
            BufferUsage.Staging));
        using ResourceSet resourceSet = RF.CreateResourceSet(
            new ResourceSetDescription(
                resourceLayout,
                new DeviceBufferRange(uniformBuffer, rangeOffset, rangeSize),
                resultBuffer));
        using CommandList commandList = RF.CreateCommandList();

        GD.UpdateBuffer(uniformBuffer, rangeOffset, valueAtWrongBase);
        GD.UpdateBuffer(
            uniformBuffer,
            rangeOffset + tailOffsetInRange,
            expectedValue);
        GD.UpdateBuffer(resultBuffer, 0, initialResult);

        commandList.Begin();
        commandList.SetPipeline(pipeline);
        commandList.SetComputeResourceSet(0, resourceSet);
        commandList.Dispatch(1, 1, 1);
        commandList.CopyBuffer(
            source: resultBuffer,
            sourceOffset: 0,
            destination: readbackBuffer,
            destinationOffset: 0,
            sizeInBytes: sizeof(uint));
        commandList.End();

        GD.SubmitCommands(commandList);
        GD.WaitForIdle();
        GD.CheckValidation("272-byte D3D11 constant-buffer range binding");

        MappedResourceView<uint> mapped = GD.Map<uint>(readbackBuffer, MapMode.Read);
        try
        {
            Assert.Equal(expectedValue, mapped[0]);
        }
        finally
        {
            GD.Unmap(readbackBuffer);
        }
    }
}
#endif
