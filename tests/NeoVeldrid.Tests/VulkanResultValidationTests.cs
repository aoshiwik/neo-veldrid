using System;
using System.Diagnostics;
using System.Reflection;
using Xunit;

namespace NeoVeldrid.Tests;

public class VulkanResultValidationTests
{
    [Fact]
    public void ResultValidationIsEnabledInAllBuildConfigurations()
    {
        Type vulkanUtil = typeof(GraphicsDevice).Assembly.GetType(
            "NeoVeldrid.Vk.VulkanUtil",
            throwOnError: true)!;
        MethodInfo checkResult = vulkanUtil.GetMethod(
            "CheckResult",
            BindingFlags.Public | BindingFlags.Static)!;

        Assert.Null(checkResult.GetCustomAttribute<ConditionalAttribute>());
    }
}
