using System;
using Silk.NET.OpenGL;
using static NeoVeldrid.OpenGL.OpenGLUtil;

namespace NeoVeldrid.OpenGL;

/// <summary>
/// Performs logical texture copies whose OpenGL storage representations are
/// intentionally different. A NeoVeldrid R32_Float depth texture is backed by
/// DEPTH_COMPONENT32F while an R32_Float staging texture is backed by R32F, so
/// CopyImageSubData is not legal even though the public formats are identical.
/// </summary>
internal sealed class OpenGLTextureCopyConverter
{
    private const uint LocalSize = 8;

    private readonly OpenGLGraphicsDevice _gd;
    private readonly OpenGLTextureSamplerManager _textureSamplerManager;
    private GL _gl => _gd.GL;

    private uint _depthToR32FloatProgram;
    private int _sourceTextureLocation;
    private int _sourceMipLocation;
    private int _sourceXLocation;
    private int _sourceYLocation;
    private int _destinationXLocation;
    private int _destinationYLocation;
    private int _copyWidthLocation;
    private int _copyHeightLocation;

    internal OpenGLTextureCopyConverter(
        OpenGLGraphicsDevice gd,
        OpenGLTextureSamplerManager textureSamplerManager)
    {
        _gd = gd;
        _textureSamplerManager = textureSamplerManager;
    }

    internal void CopyDepthToR32Float(
        OpenGLTexture source,
        uint srcX,
        uint srcY,
        uint srcMipLevel,
        OpenGLTexture destination,
        uint dstX,
        uint dstY,
        uint dstMipLevel,
        uint width,
        uint height)
    {
        if (!_gd.Extensions.ComputeShaders)
        {
            throw new NeoVeldridException(
                "Copying R32_Float depth storage into color staging storage requires compute shader support on OpenGL.");
        }
        if (source.TextureTarget != TextureTarget.Texture2D
            || destination.TextureTarget != TextureTarget.Texture2D)
        {
            throw new NeoVeldridException(
                "R32_Float depth-to-color texture conversion currently requires two-dimensional, non-array textures.");
        }

        EnsureDepthToR32FloatProgram();

        _gl.GetInteger(GetPName.CurrentProgram, out int previousProgram);
        CheckLastError();
        try
        {
            _textureSamplerManager.SetTextureTransient(
                TextureTarget.Texture2D,
                source.Texture);
            uint sourceTextureUnit = _textureSamplerManager.TransientTextureUnit;

            _gl.UseProgram(_depthToR32FloatProgram);
            CheckLastError();
            _gl.Uniform1(_sourceTextureLocation, (int)sourceTextureUnit);
            _gl.Uniform1(_sourceMipLocation, (int)srcMipLevel);
            _gl.Uniform1(_sourceXLocation, (int)srcX);
            _gl.Uniform1(_sourceYLocation, (int)srcY);
            _gl.Uniform1(_destinationXLocation, (int)dstX);
            _gl.Uniform1(_destinationYLocation, (int)dstY);
            _gl.Uniform1(_copyWidthLocation, (int)width);
            _gl.Uniform1(_copyHeightLocation, (int)height);
            CheckLastError();

            _gl.BindImageTexture(
                0,
                destination.Texture,
                (int)dstMipLevel,
                false,
                0,
                BufferAccessARB.WriteOnly,
                InternalFormat.R32f);
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
                InternalFormat.R32f);
            CheckLastError();
            _gl.UseProgram((uint)previousProgram);
            CheckLastError();
        }
    }

    internal void DestroyGLResources()
    {
        if (_depthToR32FloatProgram != 0)
        {
            _gl.DeleteProgram(_depthToR32FloatProgram);
            CheckLastError();
            _depthToR32FloatProgram = 0;
        }
    }

    private void EnsureDepthToR32FloatProgram()
    {
        if (_depthToR32FloatProgram != 0)
        {
            return;
        }

        string versionAndPrecision = _gd.BackendType == GraphicsBackend.OpenGLES
            ? "#version 310 es\nprecision highp float;\nprecision highp int;\n"
            : "#version 430\n";
        string source = versionAndPrecision + @"
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;
layout(r32f, binding = 0) writeonly uniform highp image2D Destination;
uniform highp sampler2D Source;
uniform int SourceMip;
uniform int SourceX;
uniform int SourceY;
uniform int DestinationX;
uniform int DestinationY;
uniform int CopyWidth;
uniform int CopyHeight;

void main()
{
    ivec2 localPosition = ivec2(gl_GlobalInvocationID.xy);
    if (localPosition.x >= CopyWidth || localPosition.y >= CopyHeight)
    {
        return;
    }

    float depth = texelFetch(
        Source,
        localPosition + ivec2(SourceX, SourceY),
        SourceMip).r;
    imageStore(
        Destination,
        localPosition + ivec2(DestinationX, DestinationY),
        vec4(depth, 0.0, 0.0, 1.0));
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
                    "Unable to link the OpenGL depth texture copy program: "
                    + _gl.GetProgramInfoLog(program));
            }

            _sourceTextureLocation = GetUniformLocation(program, "Source");
            _sourceMipLocation = GetUniformLocation(program, "SourceMip");
            _sourceXLocation = GetUniformLocation(program, "SourceX");
            _sourceYLocation = GetUniformLocation(program, "SourceY");
            _destinationXLocation = GetUniformLocation(program, "DestinationX");
            _destinationYLocation = GetUniformLocation(program, "DestinationY");
            _copyWidthLocation = GetUniformLocation(program, "CopyWidth");
            _copyHeightLocation = GetUniformLocation(program, "CopyHeight");
            _depthToR32FloatProgram = program;
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
                    "Unable to compile the OpenGL depth texture copy shader: "
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
                $"The OpenGL depth texture copy program did not expose its required '{name}' uniform.");
        }
        return location;
    }
}
