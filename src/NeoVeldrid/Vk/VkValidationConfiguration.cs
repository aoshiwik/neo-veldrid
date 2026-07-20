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
    internal const string SynchronizationValidationEnableName =
        "VK_VALIDATION_FEATURE_ENABLE_SYNCHRONIZATION_VALIDATION";

    private const string CurrentEnablesPrefix = "Current Enables:";
    private const string CurrentValidationEnabledMessageId =
        "CURRENT-VALIDATION-ENABLED";
    private const string CurrentSynchronizationValidationStatusLine =
        "  - Synchronization";
    private const string CurrentCreateInstanceStatusMessageId =
        "WARNING-CreateInstance-status-message";
    private const string TransitionalCreateInstanceStatusMessageId =
        "UNASSIGNED-CreateInstance-status-message";
    private const string LegacyCreateInstanceStatusMessageId =
        "UNASSIGNED-khronos-validation-createinstance-status-message";

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
            && capabilities.ValidationFeaturesSpecVersion == 0)
        {
            return Missing(
                true,
                capabilities.ValidationFeaturesSpecVersion,
                "VK_EXT_validation_features is unavailable from VK_LAYER_KHRONOS_validation");
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

    internal static bool IsSynchronizationValidationActivationEvidence(
        GraphicsDeviceValidationMessage message)
    {
        // VK_EXT_validation_features revision 4 introduced the synchronization
        // enum, but the Khronos validation layer advertises extension revision
        // 2 even in releases which implement it. The advertised revision is
        // therefore diagnostic metadata, not a usable feature capability.
        // RequiredSynchronization is proved instead by the layer's own exact
        // instance-creation report of the enabled feature set.
        if (string.Equals(
                message.Id,
                CurrentValidationEnabledMessageId,
                System.StringComparison.Ordinal))
        {
            return ContainsExactLine(
                message.Text ?? string.Empty,
                CurrentSynchronizationValidationStatusLine);
        }

        if (!string.Equals(
                message.Id,
                CurrentCreateInstanceStatusMessageId,
                System.StringComparison.Ordinal)
            && !string.Equals(
                message.Id,
                TransitionalCreateInstanceStatusMessageId,
                System.StringComparison.Ordinal)
            && !string.Equals(
                message.Id,
                LegacyCreateInstanceStatusMessageId,
                System.StringComparison.Ordinal))
        {
            return false;
        }

        string text = message.Text ?? string.Empty;
        int enabledStart = text.IndexOf(
            CurrentEnablesPrefix,
            System.StringComparison.Ordinal);
        if (enabledStart < 0)
        {
            return false;
        }

        enabledStart += CurrentEnablesPrefix.Length;
        int enabledEnd = text.IndexOf('\n', enabledStart);
        if (enabledEnd < 0)
        {
            enabledEnd = text.Length;
        }

        return ContainsExactToken(
            text,
            enabledStart,
            enabledEnd,
            SynchronizationValidationEnableName);
    }

    private static bool ContainsExactToken(
        string text,
        int start,
        int end,
        string expectedToken)
    {
        int searchStart = start;
        while (searchStart < end)
        {
            int tokenStart = text.IndexOf(
                expectedToken,
                searchStart,
                end - searchStart,
                System.StringComparison.Ordinal);
            if (tokenStart < 0)
            {
                return false;
            }

            int tokenEnd = tokenStart + expectedToken.Length;
            bool startsAtBoundary = tokenStart == start
                || !IsTokenCharacter(text[tokenStart - 1]);
            bool endsAtBoundary = tokenEnd == end
                || !IsTokenCharacter(text[tokenEnd]);
            if (startsAtBoundary && endsAtBoundary)
            {
                return true;
            }

            searchStart = tokenStart + 1;
        }

        return false;
    }

    private static bool IsTokenCharacter(char value)
        => char.IsLetterOrDigit(value) || value == '_';

    private static bool ContainsExactLine(string text, string expectedLine)
    {
        int lineStart = 0;
        while (lineStart <= text.Length)
        {
            int lineEnd = text.IndexOf('\n', lineStart);
            if (lineEnd < 0)
            {
                lineEnd = text.Length;
            }

            int lineLength = lineEnd - lineStart;
            if (lineLength > 0 && text[lineStart + lineLength - 1] == '\r')
            {
                lineLength--;
            }

            if (lineLength == expectedLine.Length
                && string.CompareOrdinal(
                    text,
                    lineStart,
                    expectedLine,
                    0,
                    lineLength) == 0)
            {
                return true;
            }

            if (lineEnd == text.Length)
            {
                return false;
            }

            lineStart = lineEnd + 1;
        }

        return false;
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
