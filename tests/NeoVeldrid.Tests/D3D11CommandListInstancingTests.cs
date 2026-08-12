#if TEST_D3D11
using NeoVeldrid.D3D11;
using Xunit;

namespace NeoVeldrid.Tests;

[Trait("Backend", "D3D11")]
public sealed class D3D11CommandListInstancingTests
{
    [Fact]
    public void InstanceRateInputAlwaysUsesNativeInstancedDraw()
    {
        Assert.True(D3D11CommandList.RequiresNativeInstancedDraw(
            hasInstancedVertexInput: true,
            instanceCount: 1u,
            instanceStart: 0u));
        Assert.False(D3D11CommandList.RequiresNativeInstancedDraw(
            hasInstancedVertexInput: false,
            instanceCount: 1u,
            instanceStart: 0u));
        Assert.True(D3D11CommandList.RequiresNativeInstancedDraw(
            hasInstancedVertexInput: false,
            instanceCount: 2u,
            instanceStart: 0u));
        Assert.True(D3D11CommandList.RequiresNativeInstancedDraw(
            hasInstancedVertexInput: false,
            instanceCount: 1u,
            instanceStart: 3u));
    }
}
#endif
