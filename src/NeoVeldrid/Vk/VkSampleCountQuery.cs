using Silk.NET.Vulkan;

namespace NeoVeldrid.Vk;

/// <summary>
/// Describes the exact Vulkan image-format query used to determine a public
/// texture sample-count limit.
/// </summary>
internal readonly record struct VkSampleCountQuery(
    Format Format,
    ImageUsageFlags Usage)
{
    internal static VkSampleCountQuery Create(
        PixelFormat format,
        bool depthFormat)
        => new VkSampleCountQuery(
            VkFormats.VdToVkPixelFormat(format, depthFormat),
            ImageUsageFlags.SampledBit |
            (depthFormat
                ? ImageUsageFlags.DepthStencilAttachmentBit
                : ImageUsageFlags.ColorAttachmentBit));
}
