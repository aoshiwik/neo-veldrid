using Silk.NET.OpenGL;
using GLPixelFormat = Silk.NET.OpenGL.PixelFormat;

namespace NeoVeldrid.OpenGL;

/// <summary>
/// Describes a lossless OpenGL ES ReadPixels transfer. OpenGL ES does not
/// promise that the texture's upload format/type pair is legal for ReadPixels;
/// the framebuffer instead advertises an implementation-selected pair. When
/// that pair only widens the component vector, the extra components can be
/// removed without changing any represented bits.
/// </summary>
internal readonly unsafe struct OpenGLReadPixelsTransfer
{
    private OpenGLReadPixelsTransfer(
        GLPixelFormat format,
        PixelType type,
        uint sourcePixelSize,
        uint destinationPixelSize)
    {
        Format = format;
        Type = type;
        SourcePixelSize = sourcePixelSize;
        DestinationPixelSize = destinationPixelSize;
    }

    internal GLPixelFormat Format { get; }

    internal PixelType Type { get; }

    internal uint SourcePixelSize { get; }

    internal uint DestinationPixelSize { get; }

    internal bool RequiresCompaction => SourcePixelSize != DestinationPixelSize;

    internal static OpenGLReadPixelsTransfer Select(
        GLPixelFormat requestedFormat,
        PixelType requestedType,
        GLPixelFormat implementationFormat,
        PixelType implementationType)
    {
        uint requestedComponents = GetComponentCount(requestedFormat);
        uint requestedComponentSize = GetScalarSize(requestedType);
        if (requestedComponents == 0 || requestedComponentSize == 0)
        {
            return Direct(requestedFormat, requestedType);
        }

        uint implementationComponents = GetComponentCount(implementationFormat);
        uint implementationComponentSize = GetScalarSize(implementationType);
        bool preservesLeadingComponents = implementationType == requestedType
            && implementationComponentSize == requestedComponentSize
            && IsIntegerFormat(implementationFormat) == IsIntegerFormat(requestedFormat)
            && HasRedFirstComponentOrder(implementationFormat)
            && implementationComponents >= requestedComponents;

        return preservesLeadingComponents
            ? new OpenGLReadPixelsTransfer(
                implementationFormat,
                implementationType,
                implementationComponents * implementationComponentSize,
                requestedComponents * requestedComponentSize)
            : Direct(requestedFormat, requestedType);
    }

    internal void Compact(void* source, void* destination, uint pixelCount)
    {
        if (!RequiresCompaction)
        {
            System.Buffer.MemoryCopy(
                source,
                destination,
                (ulong)DestinationPixelSize * pixelCount,
                (ulong)DestinationPixelSize * pixelCount);
            return;
        }

        byte* sourceBytes = (byte*)source;
        byte* destinationBytes = (byte*)destination;
        for (uint pixel = 0; pixel < pixelCount; pixel++)
        {
            System.Buffer.MemoryCopy(
                sourceBytes + (pixel * SourcePixelSize),
                destinationBytes + (pixel * DestinationPixelSize),
                DestinationPixelSize,
                DestinationPixelSize);
        }
    }

    private static OpenGLReadPixelsTransfer Direct(
        GLPixelFormat format,
        PixelType type)
    {
        uint pixelSize = GetComponentCount(format) * GetScalarSize(type);
        return new OpenGLReadPixelsTransfer(format, type, pixelSize, pixelSize);
    }

    private static bool HasRedFirstComponentOrder(GLPixelFormat format)
        => format != GLPixelFormat.Bgra;

    private static bool IsIntegerFormat(GLPixelFormat format)
        => format == GLPixelFormat.RedInteger
            || format == GLPixelFormat.RGInteger
            || format == GLPixelFormat.RgbInteger
            || format == GLPixelFormat.RgbaInteger;

    private static uint GetComponentCount(GLPixelFormat format)
    {
        switch (format)
        {
            case GLPixelFormat.Red:
            case GLPixelFormat.RedInteger:
                return 1;
            case GLPixelFormat.RG:
            case GLPixelFormat.RGInteger:
                return 2;
            case GLPixelFormat.Rgb:
            case GLPixelFormat.RgbInteger:
                return 3;
            case GLPixelFormat.Rgba:
            case GLPixelFormat.RgbaInteger:
            case GLPixelFormat.Bgra:
                return 4;
            default:
                return 0;
        }
    }

    private static uint GetScalarSize(PixelType type)
    {
        switch (type)
        {
            case PixelType.Byte:
            case PixelType.UnsignedByte:
                return 1;
            case PixelType.Short:
            case PixelType.UnsignedShort:
            case PixelType.HalfFloat:
                return 2;
            case PixelType.Int:
            case PixelType.UnsignedInt:
            case PixelType.Float:
                return 4;
            default:
                return 0;
        }
    }
}
