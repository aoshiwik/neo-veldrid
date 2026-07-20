namespace NeoVeldrid.Vk;

internal enum VkValidationLayer
{
    None,
    Standard,
    Khronos,
}

internal readonly record struct VkValidationCapabilities(
    bool HasDebugUtils,
    bool HasStandardValidationLayer,
    bool HasKhronosValidationLayer,
    uint ValidationFeaturesSpecVersion);

internal readonly record struct VkValidationConfiguration(
    bool Requested,
    bool Required,
    bool EnableDebugUtils,
    bool EnableSynchronizationValidation,
    VkValidationLayer Layer,
    uint ValidationFeaturesSpecVersion,
    string InactiveReason)
{
    internal const uint MinimumSynchronizationValidationFeaturesSpecVersion = 4;

    public static VkValidationConfiguration Resolve(
        bool debugRequested,
        VulkanValidationMode mode,
        VkValidationCapabilities capabilities)
    {
        if (!System.Enum.IsDefined(mode))
        {
            throw new System.ArgumentOutOfRangeException(nameof(mode));
        }

        bool required = mode != VulkanValidationMode.Automatic;
        if (required && !debugRequested)
        {
            throw new NeoVeldridException(
                $"{nameof(VulkanDeviceOptions.ValidationMode)}.{mode} requires " +
                $"{nameof(GraphicsDeviceOptions.Debug)} to be enabled.");
        }

        if (!debugRequested)
        {
            return new VkValidationConfiguration(
                false,
                false,
                false,
                false,
                VkValidationLayer.None,
                capabilities.ValidationFeaturesSpecVersion,
                "debug validation was not requested");
        }

        if (!capabilities.HasDebugUtils)
        {
            return Missing(
                required,
                capabilities.ValidationFeaturesSpecVersion,
                "VK_EXT_debug_utils is unavailable");
        }

        VkValidationLayer layer = capabilities.HasKhronosValidationLayer
            ? VkValidationLayer.Khronos
            : capabilities.HasStandardValidationLayer
                ? VkValidationLayer.Standard
                : VkValidationLayer.None;

        if (required && layer != VkValidationLayer.Khronos)
        {
            return Missing(
                true,
                capabilities.ValidationFeaturesSpecVersion,
                "VK_LAYER_KHRONOS_validation is unavailable");
        }

        bool synchronizationRequested = mode == VulkanValidationMode.RequiredSynchronization;
        if (synchronizationRequested
            && capabilities.ValidationFeaturesSpecVersion
                < MinimumSynchronizationValidationFeaturesSpecVersion)
        {
            return Missing(
                true,
                capabilities.ValidationFeaturesSpecVersion,
                "VK_EXT_validation_features revision 4 or newer is unavailable");
        }

        return new VkValidationConfiguration(
            true,
            required,
            true,
            synchronizationRequested,
            layer,
            capabilities.ValidationFeaturesSpecVersion,
            string.Empty);
    }

    private static VkValidationConfiguration Missing(
        bool required,
        uint validationFeaturesSpecVersion,
        string reason)
    {
        if (required)
        {
            throw new NeoVeldridException(
                "Required Vulkan validation could not be activated: " + reason + ".");
        }

        return new VkValidationConfiguration(
            true,
            false,
            false,
            false,
            VkValidationLayer.None,
            validationFeaturesSpecVersion,
            reason);
    }
}
