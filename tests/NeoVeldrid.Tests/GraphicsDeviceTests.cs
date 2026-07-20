using Xunit;

namespace NeoVeldrid.Tests;

public abstract class GraphicsDeviceTestBase_Debug<T> : GraphicsDeviceTestBase<T> where T : GraphicsDeviceCreator
{
    [Fact]
    public void IsDebugRequested_ReflectsRequestedOption()
    {
        // Every test fixture creates its device with GraphicsDeviceOptions.Debug = true.
        Assert.True(GD.IsDebugRequested);
    }

    [Fact]
    public void RequiredValidationContract_IsProvedByActiveStatus()
    {
        GraphicsDeviceValidationRequirements.EnsureSatisfied(GD);
        if (GraphicsDeviceValidationRequirements.IsApiValidationRequired)
        {
            Assert.True(GD.IsDebugActive, GD.Validation.Status.ToString());
            Assert.True(
                GD.Validation.Status.HasFeature(GraphicsDeviceValidationFeatures.ApiDebugOutput),
                GD.Validation.Status.ToString());
        }
    }
}

#if TEST_VULKAN
[Trait("Backend", "Vulkan")]
public class VulkanGraphicsDeviceTests : GraphicsDeviceTestBase_Debug<VulkanDeviceCreator> { }
#endif
#if TEST_D3D11
[Trait("Backend", "D3D11")]
public class D3D11GraphicsDeviceTests : GraphicsDeviceTestBase_Debug<D3D11DeviceCreator> { }
#endif
#if TEST_OPENGL
[Trait("Backend", "OpenGL")]
public class OpenGLGraphicsDeviceTests : GraphicsDeviceTestBase_Debug<OpenGLDeviceCreator> { }
#endif
#if TEST_OPENGLES
[Trait("Backend", "OpenGLES")]
public class OpenGLESGraphicsDeviceTests : GraphicsDeviceTestBase_Debug<OpenGLESDeviceCreator> { }
#endif
