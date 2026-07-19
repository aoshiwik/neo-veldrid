#if TEST_D3D11
using NeoVeldrid.D3D11;
using Xunit;

namespace NeoVeldrid.Tests;

[Trait("Backend", "D3D11")]
public sealed class D3D11CommandListTextureUploadTests
{
    [Fact]
    public void DeferredSourceAdjustmentIncludesEveryUncompressedAxis()
    {
        nuint adjustment =
            D3D11CommandList.CalculateDeferredTextureUpdateSourceAdjustment(
                PixelFormat.R8_G8_B8_A8_UNorm,
                x: 3,
                y: 2,
                z: 1,
                sourceRowPitch: 32,
                sourceDepthPitch: 128);

        Assert.Equal((nuint)204, adjustment);
    }

    [Fact]
    public void DeferredSourceAdjustmentUsesCompressedBlockCoordinates()
    {
        nuint adjustment =
            D3D11CommandList.CalculateDeferredTextureUpdateSourceAdjustment(
                PixelFormat.BC3_UNorm,
                x: 4,
                y: 4,
                z: 2,
                sourceRowPitch: 32,
                sourceDepthPitch: 64);

        Assert.Equal((nuint)176, adjustment);
    }
}
#endif
