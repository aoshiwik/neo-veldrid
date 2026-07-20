namespace NeoVeldrid;

/// <summary>
/// Selects how Vulkan API validation is activated when a debug graphics device is created.
/// </summary>
public enum VulkanValidationMode
{
    /// <summary>
    /// Activates the best available validation message transport without making host validation-layer
    /// availability a device-creation requirement.
    /// </summary>
    Automatic,

    /// <summary>
    /// Requires the Khronos validation layer and an active debug-message transport.
    /// </summary>
    Required,

    /// <summary>
    /// Requires the Khronos validation layer, an active debug-message transport, and Vulkan
    /// synchronization validation.
    /// </summary>
    RequiredSynchronization,
}

/// <summary>
/// A structure describing Vulkan-specific device creation options.
/// </summary>
public struct VulkanDeviceOptions
{
    /// <summary>
    /// An array of required Vulkan instance extensions. Entries in this array will be enabled in the GraphicsDevice's
    /// created VkInstance.
    /// </summary>
    public string[] InstanceExtensions;
    /// <summary>
    /// An array of required Vulkan device extensions. Entries in this array will be enabled in the GraphicsDevice's
    /// created VkDevice.
    /// </summary>
    public string[] DeviceExtensions;
    /// <summary>
    /// Selects the validation facilities to activate when <see cref="GraphicsDeviceOptions.Debug"/> is true.
    /// Required modes fail device creation instead of silently degrading when their facilities are unavailable.
    /// </summary>
    public VulkanValidationMode ValidationMode;

    /// <summary>
    /// Constructs a new VulkanDeviceOptions.
    /// </summary>
    /// <param name="instanceExtensions">An array of required Vulkan instance extensions. Entries in this array will be
    /// enabled in the GraphicsDevice's created VkInstance.</param>
    /// <param name="deviceExtensions">An array of required Vulkan device extensions. Entries in this array will be enabled
    /// in the GraphicsDevice's created VkDevice.</param>
    public VulkanDeviceOptions(string[] instanceExtensions, string[] deviceExtensions)
        : this(instanceExtensions, deviceExtensions, VulkanValidationMode.Automatic)
    {
    }

    /// <summary>
    /// Constructs a new VulkanDeviceOptions.
    /// </summary>
    /// <param name="instanceExtensions">An array of required Vulkan instance extensions. Entries in this array will be
    /// enabled in the GraphicsDevice's created VkInstance.</param>
    /// <param name="deviceExtensions">An array of required Vulkan device extensions. Entries in this array will be enabled
    /// in the GraphicsDevice's created VkDevice.</param>
    /// <param name="validationMode">The validation facilities to activate for a debug device.</param>
    public VulkanDeviceOptions(
        string[] instanceExtensions,
        string[] deviceExtensions,
        VulkanValidationMode validationMode)
    {
        InstanceExtensions = instanceExtensions;
        DeviceExtensions = deviceExtensions;
        ValidationMode = validationMode;
    }
}
