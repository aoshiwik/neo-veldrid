using NeoVeldrid.Sdl2;
using NeoVeldrid.StartupUtilities;
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
