using System;
using System.Diagnostics;
using Silk.NET.Vulkan;
using VkApi = Silk.NET.Vulkan.Vk;

namespace NeoVeldrid.Vk;

internal unsafe static class VulkanUtil
{
    private static Lazy<bool> s_isVulkanLoaded = new Lazy<bool>(TryLoadVulkan);
    private static readonly Lazy<string[]> s_instanceExtensions = new Lazy<string[]>(EnumerateInstanceExtensions);

    public static void CheckResult(Result result)
    {
        if (result != Result.Success)
        {
            throw new NeoVeldridException("Unsuccessful VkResult: " + result);
        }
    }

    public static bool TryFindMemoryType(PhysicalDeviceMemoryProperties memProperties, uint typeFilter, MemoryPropertyFlags properties, out uint typeIndex)
    {
        typeIndex = 0;

        for (int i = 0; i < memProperties.MemoryTypeCount; i++)
        {
            if (((typeFilter & (1 << i)) != 0)
                && (memProperties.MemoryTypes[i].PropertyFlags & properties) == properties)
            {
                typeIndex = (uint)i;
                return true;
            }
        }

        return false;
    }

    public static string[] EnumerateInstanceLayers()
    {
        using var vk = VkApi.GetApi();
        uint propCount = 0;
        Result result = vk.EnumerateInstanceLayerProperties(ref propCount, null);
        CheckResult(result);
        if (propCount == 0)
        {
            return Array.Empty<string>();
        }

        LayerProperties[] props = new LayerProperties[propCount];
        fixed (LayerProperties* propsPtr = props)
        {
            result = vk.EnumerateInstanceLayerProperties(ref propCount, propsPtr);
        }
        CheckResult(result);

        string[] ret = new string[propCount];
        for (int i = 0; i < propCount; i++)
        {
            fixed (byte* layerNamePtr = props[i].LayerName)
            {
                ret[i] = Util.GetString(layerNamePtr);
            }
        }

        return ret;
    }

    public static string[] GetInstanceExtensions() => s_instanceExtensions.Value;

    public static uint GetInstanceExtensionSpecVersion(string extensionName, string layerName = null)
    {
        if (extensionName == null)
        {
            throw new ArgumentNullException(nameof(extensionName));
        }

        // A layer-specific validation-feature transport must be advertised by
        // that layer. A same-named implementation extension is not evidence
        // that the selected layer accepts the create-info. The advertised
        // revision is retained for diagnostics; individual feature activation
        // is proved from the selected validation layer itself.
        return GetInstanceExtensionSpecVersionCore(extensionName, layerName);
    }

    private static uint GetInstanceExtensionSpecVersionCore(string extensionName, string layerName)
    {
        if (!IsVulkanLoaded())
        {
            return 0;
        }

        using var vk = VkApi.GetApi();
        using FixedUtf8String layerNameUtf8 = layerName == null
            ? null
            : new FixedUtf8String(layerName);
        byte* layerNamePtr = layerNameUtf8 == null ? null : layerNameUtf8.StringPtr;

        uint propertyCount = 0;
        Result result = vk.EnumerateInstanceExtensionProperties(layerNamePtr, ref propertyCount, null);
        if (result != Result.Success || propertyCount == 0)
        {
            return 0;
        }

        ExtensionProperties[] properties = new ExtensionProperties[propertyCount];
        fixed (ExtensionProperties* propertiesPtr = properties)
        {
            result = vk.EnumerateInstanceExtensionProperties(layerNamePtr, ref propertyCount, propertiesPtr);
        }
        if (result != Result.Success)
        {
            return 0;
        }

        uint version = 0;
        for (int i = 0; i < propertyCount; i++)
        {
            fixed (byte* extensionNamePtr = properties[i].ExtensionName)
            {
                if (string.Equals(Util.GetString(extensionNamePtr), extensionName, StringComparison.Ordinal))
                {
                    version = Math.Max(version, properties[i].SpecVersion);
                }
            }
        }

        return version;
    }

    private static string[] EnumerateInstanceExtensions()
    {
        if (!IsVulkanLoaded())
        {
            return Array.Empty<string>();
        }

        using var vk = VkApi.GetApi();
        uint propCount = 0;
        Result result = vk.EnumerateInstanceExtensionProperties((byte*)null, ref propCount, null);
        if (result != Result.Success)
        {
            return Array.Empty<string>();
        }

        if (propCount == 0)
        {
            return Array.Empty<string>();
        }

        ExtensionProperties[] props = new ExtensionProperties[propCount];
        fixed (ExtensionProperties* propsPtr = props)
        {
            result = vk.EnumerateInstanceExtensionProperties(
                (byte*)null,
                ref propCount,
                propsPtr);
        }
        if (result != Result.Success)
        {
            return Array.Empty<string>();
        }

        string[] ret = new string[propCount];
        for (int i = 0; i < propCount; i++)
        {
            fixed (byte* extensionNamePtr = props[i].ExtensionName)
            {
                ret[i] = Util.GetString(extensionNamePtr);
            }
        }

        return ret;
    }

    public static bool IsVulkanLoaded() => s_isVulkanLoaded.Value;
    private static bool TryLoadVulkan()
    {
        try
        {
            using var vk = VkApi.GetApi();
            uint propCount;
            vk.EnumerateInstanceExtensionProperties((byte*)null, &propCount, null);
            return true;
        }
        catch { return false; }
    }

    public static void TransitionImageLayout(
        VkApi vk,
        CommandBuffer cb,
        Image image,
        uint baseMipLevel,
        uint levelCount,
        uint baseArrayLayer,
        uint layerCount,
        ImageAspectFlags aspectMask,
        ImageLayout oldLayout,
        ImageLayout newLayout)
    {
        Debug.Assert(oldLayout != newLayout);
        ImageMemoryBarrier barrier = new ImageMemoryBarrier(sType: StructureType.ImageMemoryBarrier);
        barrier.OldLayout = oldLayout;
        barrier.NewLayout = newLayout;
        barrier.SrcQueueFamilyIndex = VkApi.QueueFamilyIgnored;
        barrier.DstQueueFamilyIndex = VkApi.QueueFamilyIgnored;
        barrier.Image = image;
        barrier.SubresourceRange.AspectMask = aspectMask;
        barrier.SubresourceRange.BaseMipLevel = baseMipLevel;
        barrier.SubresourceRange.LevelCount = levelCount;
        barrier.SubresourceRange.BaseArrayLayer = baseArrayLayer;
        barrier.SubresourceRange.LayerCount = layerCount;

        VkImageLayoutTransitionContract contract =
            DescribeImageLayoutTransition(oldLayout, newLayout);
        barrier.SrcAccessMask = contract.SourceAccess;
        barrier.DstAccessMask = contract.DestinationAccess;
        PipelineStageFlags srcStageFlags = contract.SourceStages;
        PipelineStageFlags dstStageFlags = contract.DestinationStages;

        vk.CmdPipelineBarrier(
            cb,
            srcStageFlags,
            dstStageFlags,
            DependencyFlags.None,
            0, null,
            0, null,
            1, &barrier);
    }

    /// <summary>
    /// Resolves the complete synchronization scope for an image-layout
    /// transition. Layout tracking is intentionally conservative because the
    /// command list does not retain the exact shader stage of the last or next
    /// texture consumer.
    /// </summary>
    internal static VkImageLayoutTransitionContract DescribeImageLayoutTransition(
        ImageLayout oldLayout,
        ImageLayout newLayout)
    {
        if (oldLayout == newLayout)
        {
            throw new NeoVeldridException(
                $"A Vulkan image-layout transition requires two distinct layouts, but both were {oldLayout}.");
        }

        VkImageLayoutAccessScope source = DescribeImageLayoutAccess(
            oldLayout,
            isSource: true);
        VkImageLayoutAccessScope destination = DescribeImageLayoutAccess(
            newLayout,
            isSource: false);
        return new VkImageLayoutTransitionContract(
            source.Access,
            destination.Access,
            source.Stages,
            destination.Stages);
    }

    /// <summary>
    /// Creates the external dependency which orders a render pass's attachment
    /// prior uses and this render pass's attachment loads. A render pass with
    /// an explicit external dependency no longer receives Vulkan's implicit
    /// one, so its source must cover every way the image could have been used
    /// before the pass, including shader, compute, transfer, and attachment
    /// access. The destination remains the exact attachment load/write stages.
    /// </summary>
    internal static SubpassDependency CreateRenderPassAttachmentDependency(
        bool hasColorAttachments,
        bool hasDepthStencilAttachment)
    {
        if (!hasColorAttachments && !hasDepthStencilAttachment)
        {
            throw new NeoVeldridException(
                "A Vulkan attachment dependency requires at least one color or depth-stencil attachment.");
        }

        PipelineStageFlags sourceStages = PipelineStageFlags.AllCommandsBit;
        PipelineStageFlags destinationStages = PipelineStageFlags.None;
        AccessFlags sourceAccess =
            AccessFlags.MemoryReadBit |
            AccessFlags.MemoryWriteBit;
        AccessFlags destinationAccess = AccessFlags.None;

        if (hasColorAttachments)
        {
            // Color loads and stores execute in ColorAttachmentOutput.
            destinationStages |= PipelineStageFlags.ColorAttachmentOutputBit;
            destinationAccess |=
                AccessFlags.ColorAttachmentReadBit
                | AccessFlags.ColorAttachmentWriteBit;
        }

        if (hasDepthStencilAttachment)
        {
            // Loads, including loadOp Clear, execute in early fragment tests
            // before all subsequent depth/stencil attachment access.
            destinationStages |= PipelineStageFlags.EarlyFragmentTestsBit;
            destinationAccess |=
                AccessFlags.DepthStencilAttachmentReadBit
                | AccessFlags.DepthStencilAttachmentWriteBit;
        }

        return new SubpassDependency
        {
            SrcSubpass = VkApi.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = sourceStages,
            DstStageMask = destinationStages,
            SrcAccessMask = sourceAccess,
            DstAccessMask = destinationAccess
        };
    }

    private static VkImageLayoutAccessScope DescribeImageLayoutAccess(
        ImageLayout layout,
        bool isSource)
        => layout switch
        {
            ImageLayout.Undefined when isSource =>
                new VkImageLayoutAccessScope(
                    AccessFlags.None,
                    PipelineStageFlags.TopOfPipeBit),
            ImageLayout.Preinitialized when isSource =>
                new VkImageLayoutAccessScope(
                    AccessFlags.HostWriteBit,
                    PipelineStageFlags.HostBit),
            ImageLayout.TransferSrcOptimal =>
                new VkImageLayoutAccessScope(
                    AccessFlags.TransferReadBit,
                    PipelineStageFlags.TransferBit),
            ImageLayout.TransferDstOptimal =>
                new VkImageLayoutAccessScope(
                    AccessFlags.TransferWriteBit,
                    PipelineStageFlags.TransferBit),
            ImageLayout.ShaderReadOnlyOptimal =>
                new VkImageLayoutAccessScope(
                    AccessFlags.ShaderReadBit,
                    PipelineStageFlags.AllCommandsBit),
            ImageLayout.General =>
                new VkImageLayoutAccessScope(
                    AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
                    PipelineStageFlags.AllCommandsBit),
            ImageLayout.ColorAttachmentOptimal =>
                new VkImageLayoutAccessScope(
                    AccessFlags.ColorAttachmentReadBit |
                    AccessFlags.ColorAttachmentWriteBit,
                    PipelineStageFlags.ColorAttachmentOutputBit),
            ImageLayout.DepthStencilAttachmentOptimal =>
                new VkImageLayoutAccessScope(
                    AccessFlags.DepthStencilAttachmentReadBit |
                    AccessFlags.DepthStencilAttachmentWriteBit,
                    PipelineStageFlags.EarlyFragmentTestsBit |
                    PipelineStageFlags.LateFragmentTestsBit),
            ImageLayout.PresentSrcKhr =>
                new VkImageLayoutAccessScope(
                    AccessFlags.MemoryReadBit,
                    PipelineStageFlags.BottomOfPipeBit),
            _ => throw new NeoVeldridException(
                $"The Vulkan image layout {layout} is not supported as a transition {(isSource ? "source" : "destination")}.")
        };
}

internal readonly record struct VkImageLayoutTransitionContract(
    AccessFlags SourceAccess,
    AccessFlags DestinationAccess,
    PipelineStageFlags SourceStages,
    PipelineStageFlags DestinationStages);

internal readonly record struct VkImageLayoutAccessScope(
    AccessFlags Access,
    PipelineStageFlags Stages);

internal unsafe static class VkPhysicalDeviceMemoryPropertiesEx
{
    public static MemoryType GetMemoryType(this PhysicalDeviceMemoryProperties memoryProperties, uint index)
    {
        return memoryProperties.MemoryTypes[(int)index];
    }
}
