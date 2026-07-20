using Silk.NET.OpenGL;
using static NeoVeldrid.OpenGL.OpenGLUtil;
using GLFramebufferAttachment = Silk.NET.OpenGL.FramebufferAttachment;
using GLPixelFormat = Silk.NET.OpenGL.PixelFormat;

namespace NeoVeldrid.OpenGL;

/// <summary>
/// Encodes texture formats which OpenGL ES cannot read losslessly through a
/// legal ReadPixels format/type pair into the universally readable RGBA8 pair.
/// </summary>
internal sealed unsafe class OpenGLTextureReadbackConverter
{
    private const uint LocalSize = 8;

    private readonly OpenGLGraphicsDevice _gd;
    private readonly OpenGLTextureSamplerManager _textureSamplerManager;
    private GL _gl => _gd.GL;

    private uint _rg16ToRgba8Program;
    private int _sourceTextureLocation;
    private int _sourceMipLocation;
    private int _widthLocation;
    private int _heightLocation;

    internal OpenGLTextureReadbackConverter(
        OpenGLGraphicsDevice gd,
        OpenGLTextureSamplerManager textureSamplerManager)
    {
        _gd = gd;
        _textureSamplerManager = textureSamplerManager;
    }

    internal void ReadR16G16UNorm(
        OpenGLTexture source,
        uint mipLevel,
        uint width,
        uint height,
        void* destination)
    {
        if (!_gd.Extensions.ComputeShaders || source.TextureTarget != TextureTarget.Texture2D)
        {
            throw new NeoVeldridException(
                "Lossless R16_G16_UNorm staging readback on OpenGL ES requires compute shaders and a two-dimensional, non-array texture.");
        }

        EnsureR16G16ToRgba8Program();
        _gl.GetInteger(GetPName.CurrentProgram, out int previousProgram);
        CheckLastError();

        uint encodedTexture = 0;
        uint readFramebuffer = 0;
        StagingBlock encodedPixels = default;
        bool ownsEncodedPixels = false;
        try
        {
            encodedTexture = _gl.GenTexture();
            CheckLastError();
            _textureSamplerManager.SetTextureTransient(
                TextureTarget.Texture2D,
                encodedTexture);
            _gl.TexStorage2D(
                TextureTarget.Texture2D,
                1,
                SizedInternalFormat.Rgba8,
                width,
                height);
            CheckLastError();

            _textureSamplerManager.SetTextureTransient(
                TextureTarget.Texture2D,
                source.Texture);
            uint sourceTextureUnit = _textureSamplerManager.TransientTextureUnit;

            _gl.UseProgram(_rg16ToRgba8Program);
            CheckLastError();
            _gl.Uniform1(_sourceTextureLocation, (int)sourceTextureUnit);
            _gl.Uniform1(_sourceMipLocation, (int)mipLevel);
            _gl.Uniform1(_widthLocation, (int)width);
            _gl.Uniform1(_heightLocation, (int)height);
            CheckLastError();

            _gl.BindImageTexture(
                0,
                encodedTexture,
                0,
                false,
                0,
                BufferAccessARB.WriteOnly,
                InternalFormat.Rgba8);
            CheckLastError();
            _gl.MemoryBarrier(MemoryBarrierMask.AllBarrierBits);
            CheckLastError();
            _gl.DispatchCompute(
                (width + LocalSize - 1) / LocalSize,
                (height + LocalSize - 1) / LocalSize,
                1);
            CheckLastError();
            _gl.MemoryBarrier(MemoryBarrierMask.AllBarrierBits);
            CheckLastError();

            readFramebuffer = _gl.GenFramebuffer();
            CheckLastError();
            _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, readFramebuffer);
            CheckLastError();
            _gl.FramebufferTexture2D(
                FramebufferTarget.ReadFramebuffer,
                GLFramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D,
                encodedTexture,
                0);
            CheckLastError();
            FramebufferStatus framebufferStatus = (FramebufferStatus)_gl.CheckFramebufferStatus(
                FramebufferTarget.ReadFramebuffer);
            CheckLastError();
            if (framebufferStatus != FramebufferStatus.Complete)
            {
                throw new NeoVeldridException(
                    "The OpenGL ES RG16 readback framebuffer is incomplete: "
                    + framebufferStatus);
            }
            encodedPixels = _gd.StagingMemoryPool.GetStagingBlock(
                checked(width * height * 4));
            ownsEncodedPixels = true;
            _gl.ReadPixels(
                0,
                0,
                width,
                height,
                GLPixelFormat.Rgba,
                PixelType.UnsignedByte,
                encodedPixels.Data);
            CheckLastError();
            DecodeR16G16UNorm(
                (byte*)encodedPixels.Data,
                (ushort*)destination,
                checked(width * height));
        }
        finally
        {
            _gl.BindImageTexture(
                0,
                0,
                0,
                false,
                0,
                BufferAccessARB.ReadOnly,
                InternalFormat.Rgba8);
            CheckLastError();
            _gl.UseProgram((uint)previousProgram);
            CheckLastError();
            if (readFramebuffer != 0)
            {
                _gl.DeleteFramebuffer(readFramebuffer);
                CheckLastError();
            }
            if (encodedTexture != 0)
            {
                _gl.DeleteTexture(encodedTexture);
                CheckLastError();
            }
            if (ownsEncodedPixels)
            {
                _gd.StagingMemoryPool.Free(encodedPixels);
            }
        }
    }

    internal void DestroyGLResources()
    {
        if (_rg16ToRgba8Program != 0)
        {
            _gl.DeleteProgram(_rg16ToRgba8Program);
            CheckLastError();
            _rg16ToRgba8Program = 0;
        }
    }

    private void EnsureR16G16ToRgba8Program()
    {
        if (_rg16ToRgba8Program != 0)
        {
            return;
        }

        string source = @"#version 310 es
precision highp float;
precision highp int;
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;
layout(rgba8, binding = 0) writeonly uniform highp image2D Destination;
uniform highp sampler2D Source;
uniform int SourceMip;
uniform int Width;
uniform int Height;

void main()
{
    ivec2 position = ivec2(gl_GlobalInvocationID.xy);
    if (position.x >= Width || position.y >= Height)
    {
        return;
    }

    vec2 normalized = texelFetch(Source, position, SourceMip).rg;
    uvec2 bits = uvec2(floor(normalized * 65535.0 + 0.5));
    uvec4 bytes = uvec4(
        bits.r & 255u,
        bits.r >> 8,
        bits.g & 255u,
        bits.g >> 8);
    imageStore(Destination, position, vec4(bytes) / 255.0);
}
";

        uint shader = CompileShader(source);
        uint program = 0;
        try
        {
            program = _gl.CreateProgram();
            CheckLastError();
            _gl.AttachShader(program, shader);
            CheckLastError();
            _gl.LinkProgram(program);
            CheckLastError();
            _gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int linkStatus);
            CheckLastError();
            if (linkStatus != 1)
            {
                throw new NeoVeldridException(
                    "Unable to link the OpenGL ES RG16 readback program: "
                    + _gl.GetProgramInfoLog(program));
            }

            _sourceTextureLocation = GetUniformLocation(program, "Source");
            _sourceMipLocation = GetUniformLocation(program, "SourceMip");
            _widthLocation = GetUniformLocation(program, "Width");
            _heightLocation = GetUniformLocation(program, "Height");
            _rg16ToRgba8Program = program;
        }
        catch
        {
            if (program != 0)
            {
                _gl.DeleteProgram(program);
            }
            throw;
        }
        finally
        {
            _gl.DeleteShader(shader);
            CheckLastError();
        }
    }

    private uint CompileShader(string source)
    {
        uint shader = _gl.CreateShader(ShaderType.ComputeShader);
        CheckLastError();
        try
        {
            _gl.ShaderSource(shader, source);
            CheckLastError();
            _gl.CompileShader(shader);
            CheckLastError();
            _gl.GetShader(shader, ShaderParameterName.CompileStatus, out int compileStatus);
            CheckLastError();
            if (compileStatus != 1)
            {
                throw new NeoVeldridException(
                    "Unable to compile the OpenGL ES RG16 readback shader: "
                    + _gl.GetShaderInfoLog(shader));
            }
            return shader;
        }
        catch
        {
            _gl.DeleteShader(shader);
            throw;
        }
    }

    private int GetUniformLocation(uint program, string name)
    {
        int location = _gl.GetUniformLocation(program, name);
        CheckLastError();
        if (location < 0)
        {
            throw new NeoVeldridException(
                $"The OpenGL ES RG16 readback program did not expose its required '{name}' uniform.");
        }
        return location;
    }

    private static void DecodeR16G16UNorm(
        byte* source,
        ushort* destination,
        uint pixelCount)
    {
        for (uint pixel = 0; pixel < pixelCount; pixel++)
        {
            destination[(pixel * 2) + 0] = (ushort)(
                source[(pixel * 4) + 0]
                | (source[(pixel * 4) + 1] << 8));
            destination[(pixel * 2) + 1] = (ushort)(
                source[(pixel * 4) + 2]
                | (source[(pixel * 4) + 3] << 8));
        }
    }
}
