using NeoVeldrid.Sdl2;
using NeoVeldrid.StartupUtilities;
using Silk.NET.SDL;
using Xunit;

namespace NeoVeldrid.Tests;

public class StartupRegressionTests
{
#if TEST_OPENGL
    [Fact]
    [Trait("Backend", "OpenGL")]
    public void CreateWindowAndGraphicsDevice_FailureClosesCreatedWindow()
    {
        WindowCreateInfo wci = new WindowCreateInfo
        {
            WindowWidth = 200,
            WindowHeight = 200,
            WindowInitialState = WindowState.Hidden,
        };

        Sdl2Window window = null;
        GraphicsDevice gd = null;
        try
        {
            NeoVeldridStartup.SetSDLGLContextAttributes(
                new GraphicsDeviceOptions(),
                GraphicsBackend.OpenGL);

            Assert.Throws<NeoVeldridException>(() =>
                NeoVeldridStartup.CreateWindowAndGraphicsDevice(
                    wci,
                    new GraphicsDeviceOptions(),
                    (GraphicsBackend)byte.MaxValue,
                    out window,
                    out gd));

            Assert.NotNull(window);
            Assert.False(window.Exists);
            Assert.Null(gd);

            window.Close();
            Assert.False(window.Exists);
        }
        finally
        {
            gd?.Dispose();
            if (window?.Exists == true)
            {
                window.Close();
            }
        }
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, (int)GLcontextFlag.DebugFlag)]
    [Trait("Backend", "OpenGLES")]
    public unsafe void CreateWindowAndGraphicsDevice_OpenGLES_DoesNotRequestForwardCompatibility(
        bool debug,
        int expectedContextFlags)
    {
        WindowCreateInfo wci = new WindowCreateInfo
        {
            WindowWidth = 200,
            WindowHeight = 200,
            WindowInitialState = WindowState.Hidden,
        };
        GraphicsDeviceOptions options = new GraphicsDeviceOptions { Debug = debug };
        Sdl2Window window = null;
        GraphicsDevice gd = null;
        try
        {
            NeoVeldridStartup.CreateWindowAndGraphicsDevice(
                wci,
                options,
                GraphicsBackend.OpenGLES,
                out window,
                out gd);

            int actualContextFlags;
            int result = Sdl2Window.SdlInstance.GLGetAttribute(GLattr.ContextFlags, &actualContextFlags);

            Assert.Equal(0, result);
            Assert.Equal(expectedContextFlags, actualContextFlags);
            Assert.Equal(0, actualContextFlags & (int)GLcontextFlag.ForwardCompatibleFlag);
        }
        finally
        {
            gd?.Dispose();
            if (window?.Exists == true)
            {
                window.Close();
            }
        }
    }
#endif
}
