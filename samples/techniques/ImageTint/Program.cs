using SampleBase;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.IO;
using System.Numerics;
using NeoVeldrid;
using NeoVeldrid.Sdl2;
using NeoVeldrid.ImageSharp;
using NeoVeldrid.SPIRV;
using NeoVeldrid.StartupUtilities;
using NeoVeldrid.Utilities;

namespace ImageTint;

/*
   ImageTint: This is a sample application which does the following:
     * Loads the image passed in as the first argument, using ImageSharp.
     * Uploads that image to a GPU texture.
     * Renders a new image, with the same dimensions, by sampling the original texture and mixing in a red tint.
     * Copies thew new image into a CPU-visible staging texture
     * Maps the staging texture into CPU address space and copies it into a linear buffer.
     * Constructs a new ImageSharp image from that pixel data array and saves it to a second file.
*/
class Program
{
    private const int VerificationSubmissionCount = 16;
    private const int VerificationTolerance = 1;
    private const float TintFactor = 0.25f;
    private static readonly Vector3 TintColor = new Vector3(1f, 0.2f, 0.1f);

    static int Main(string[] args)
    {
        bool verifyOutput = args.Length == 3 && args[2] == "--verify";
        if (args.Length != 2 && !verifyOutput)
        {
            Console.WriteLine(
                "ImageTint <image-path> <out> [--verify]: Tints the image at <image-path> and saves it to <out>.");
            return 1;
        }

        string inPath = args[0];
        string outPath = args[1];
        int submissionCount = verifyOutput ? VerificationSubmissionCount : 1;

        // This demo uses WindowState.Hidden to avoid popping up an unnecessary window to the user.

        NeoVeldridStartup.CreateWindowAndGraphicsDevice(
            new WindowCreateInfo
            {
                WindowInitialState = WindowState.Hidden,
            },
            new GraphicsDeviceOptions() { ResourceBindingModel = ResourceBindingModel.Improved, PreferStandardClipSpaceYDirection = true },
            BackendHelper.GetPreferredBackend(),
            out Sdl2Window window,
            out GraphicsDevice gd);

        DisposeCollectorResourceFactory factory = new DisposeCollectorResourceFactory(gd.ResourceFactory);

        ImageSharpTexture inputImage = new ImageSharpTexture(inPath, false);
        Texture inputTexture = inputImage.CreateDeviceTexture(gd, factory);
        TextureView view = factory.CreateTextureView(inputTexture);

        Texture[] outputs = new Texture[submissionCount];
        Framebuffer[] framebuffers = new Framebuffer[submissionCount];
        Texture[] captures = new Texture[submissionCount];
        for (int submissionIndex = 0; submissionIndex < submissionCount; submissionIndex++)
        {
            outputs[submissionIndex] = factory.CreateTexture(TextureDescription.Texture2D(
                inputImage.Width,
                inputImage.Height,
                1,
                1,
                PixelFormat.R8_G8_B8_A8_UNorm,
                TextureUsage.RenderTarget));
            framebuffers[submissionIndex] = factory.CreateFramebuffer(
                new FramebufferDescription(null, outputs[submissionIndex]));
            captures[submissionIndex] = factory.CreateTexture(TextureDescription.Texture2D(
                inputImage.Width,
                inputImage.Height,
                1,
                1,
                PixelFormat.R8_G8_B8_A8_UNorm,
                TextureUsage.Staging));
        }

        DeviceBuffer vertexBuffer = factory.CreateBuffer(new BufferDescription(64, BufferUsage.VertexBuffer));

        Vector4[] quadVerts =
        {
            new Vector4(-1, 1, 0, 0),
            new Vector4(1, 1, 1, 0),
            new Vector4(-1, -1, 0, 1),
            new Vector4(1, -1, 1, 1),
        };
        gd.UpdateBuffer(vertexBuffer, 0, quadVerts);

        ShaderSetDescription shaderSet = new ShaderSetDescription(
            new[]
            {
                new VertexLayoutDescription(
                    new VertexElementDescription("Position", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2),
                    new VertexElementDescription("TextureCoordinates", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2))
            },
            factory.CreateFromSpirv(
                new ShaderDescription(ShaderStages.Vertex, ReadEmbeddedAssetBytes("TintShader-vertex.glsl"), "main"),
                new ShaderDescription(ShaderStages.Fragment, ReadEmbeddedAssetBytes("TintShader-fragment.glsl"), "main")));

        ResourceLayout layout = factory.CreateResourceLayout(new ResourceLayoutDescription(
            new ResourceLayoutElementDescription("Input", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
            new ResourceLayoutElementDescription("Sampler", ResourceKind.Sampler, ShaderStages.Fragment),
            new ResourceLayoutElementDescription("Tint", ResourceKind.UniformBuffer, ShaderStages.Fragment)));

        Pipeline pipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription(
            BlendStateDescription.SingleOverrideBlend,
            DepthStencilStateDescription.Disabled,
            RasterizerStateDescription.Default,
            PrimitiveTopology.TriangleStrip,
            shaderSet,
            layout,
            framebuffers[0].OutputDescription));

        DeviceBuffer tintInfoBuffer = factory.CreateBuffer(new BufferDescription(16, BufferUsage.UniformBuffer));
        gd.UpdateBuffer(
            tintInfoBuffer, 0,
            new TintInfo(TintColor, TintFactor));

        ResourceSet resourceSet = factory.CreateResourceSet(
            new ResourceSetDescription(layout, view, gd.PointSampler, tintInfoBuffer));

        // Repeatedly reuse a bounded command list. Vulkan recycles two native
        // submission states here. Independent outputs and captures avoid
        // cross-submission write hazards while retaining each resource set.
        CommandList cl = factory.CreateCommandList(new CommandListDescription
        {
            MaximumInFlightSubmissionCount = 2,
            InitialTrackedResourceCapacityPerSubmission = 16
        });
        for (int submissionIndex = 0; submissionIndex < submissionCount; submissionIndex++)
        {
            cl.Begin();
            cl.SetFramebuffer(framebuffers[submissionIndex]);
            cl.SetFullViewports();
            cl.SetVertexBuffer(0, vertexBuffer);
            cl.SetPipeline(pipeline);
            cl.SetGraphicsResourceSet(0, resourceSet);
            cl.Draw(4, 1, 0, 0);
            cl.CopyTexture(
                outputs[submissionIndex], 0, 0, 0, 0, 0,
                captures[submissionIndex], 0, 0, 0, 0, 0,
                inputImage.Width, inputImage.Height, 1, 1);
            cl.End();
            gd.SubmitCommands(cl);
        }
        gd.WaitForIdle();

        // When a texture is mapped into a CPU-visible region, it is often not laid out linearly.
        // Instead, it is laid out as a series of rows, which are all spaced out evenly by a "row pitch".
        // This spacing is provided in MappedResource.RowPitch.

        // It is also possible to obtain a "structured view" of a mapped data region, which is what is done below.
        // With a structured view, you can read individual elements from the region.
        // The code below simply iterates over the two-dimensional region and places each texel into a linear buffer.
        // ImageSharp requires the pixel data be contained in a linear buffer.
        // Rgba32 is synonymous with PixelFormat.R8_G8_B8_A8_UNorm.
        Rgba32[] pixelData = new Rgba32[inputImage.Width * inputImage.Height];
        int firstCaptureIndex = verifyOutput ? 0 : captures.Length - 1;
        for (int captureIndex = firstCaptureIndex; captureIndex < captures.Length; captureIndex++)
        {
            Texture capture = captures[captureIndex];
            MappedResourceView<Rgba32> map = gd.Map<Rgba32>(capture, MapMode.Read);
            for (int y = 0; y < capture.Height; y++)
            {
                // OpenGL staging textures expose their first row at the bottom,
                // while PNG rows are top-first. Canonicalize the saved artifact so
                // every backend produces the same inspectable image.
                int sourceY = gd.IsUvOriginTopLeft ? y : (int)capture.Height - y - 1;
                for (int x = 0; x < capture.Width; x++)
                {
                    int index = (int)(y * capture.Width + x);
                    pixelData[index] = map[x, sourceY];
                }
            }
            gd.Unmap(capture);

            if (verifyOutput)
            {
                VerifyTintedPixels(inputImage.Images[0], pixelData);
            }
        }

        using Image<Rgba32> outputImage = Image.LoadPixelData(
            pixelData,
            (int)inputImage.Width,
            (int)inputImage.Height);
        outputImage.Save(outPath);
        if (verifyOutput)
        {
            Console.WriteLine(
                $"Verified {pixelData.Length * captures.Length} tinted pixels across "
                + $"{captures.Length} bounded submissions within a tolerance of {VerificationTolerance}.");
        }

        factory.DisposeCollector.DisposeAll();

        gd.Dispose();
        window.Close();
        return 0;
    }

    private static void VerifyTintedPixels(Image<Rgba32> inputImage, ReadOnlySpan<Rgba32> actualPixels)
    {
        for (int y = 0; y < inputImage.Height; y++)
        {
            for (int x = 0; x < inputImage.Width; x++)
            {
                Rgba32 input = inputImage[x, y];
                Rgba32 expected = new Rgba32(
                    ApplyTint(input.R, TintColor.X),
                    ApplyTint(input.G, TintColor.Y),
                    ApplyTint(input.B, TintColor.Z),
                    input.A);
                Rgba32 actual = actualPixels[y * inputImage.Width + x];
                if (!AreEquivalent(expected, actual))
                {
                    throw new InvalidOperationException(
                        $"Tint verification failed at ({x}, {y}). Expected {expected}; actual {actual}.");
                }
            }
        }
    }

    private static byte ApplyTint(byte channel, float tintScale)
    {
        float blendedScale = 1f + TintFactor * (tintScale - 1f);
        return (byte)Math.Clamp((int)MathF.Round(channel * blendedScale), byte.MinValue, byte.MaxValue);
    }

    private static bool AreEquivalent(Rgba32 expected, Rgba32 actual)
    {
        return Math.Abs(expected.R - actual.R) <= VerificationTolerance
            && Math.Abs(expected.G - actual.G) <= VerificationTolerance
            && Math.Abs(expected.B - actual.B) <= VerificationTolerance
            && Math.Abs(expected.A - actual.A) <= VerificationTolerance;
    }

    public static Stream OpenEmbeddedAssetStream(string name, Type t) => t.Assembly.GetManifestResourceStream(name);

    public static Shader LoadShader(ResourceFactory factory, string set, ShaderStages stage, string entryPoint)
    {
        string name = $"{set}-{stage.ToString().ToLower()}.{GetExtension(factory.BackendType)}";
        return factory.CreateShader(new ShaderDescription(stage, ReadEmbeddedAssetBytes(name), entryPoint));
    }

    public static byte[] ReadEmbeddedAssetBytes(string name)
    {
        using (Stream stream = OpenEmbeddedAssetStream(name, typeof(Program)))
        {
            byte[] bytes = new byte[stream.Length];
            using (MemoryStream ms = new MemoryStream(bytes))
            {
                stream.CopyTo(ms);
                return bytes;
            }
        }
    }

    private static string GetExtension(GraphicsBackend backendType)
    {
        return (backendType == GraphicsBackend.Direct3D11)
            ? "hlsl.bytes"
            : (backendType == GraphicsBackend.Vulkan)
                ? "450.glsl.spv"
                : (backendType == GraphicsBackend.OpenGL)
                    ? "330.glsl"
                    : "300.glsles";
    }
}

public struct TintInfo
{
    public Vector3 RGBTintColor;
    public float TintFactor;
    public TintInfo(Vector3 rGBTintColor, float tintFactor)
    {
        RGBTintColor = rGBTintColor;
        TintFactor = tintFactor;
    }
}
