using System;

namespace NeoVeldrid.Vk;

/// <summary>
/// Coordinates host access with submitted GPU uses of one Vulkan mappable
/// resource. Resource reference counts protect lifetime; this state protects
/// the bytes from concurrent host and device access.
/// </summary>
internal sealed class VkMappableResourceSubmissionAccess
{
    private readonly object _sync = new object();
    private int _submissionUseCount;
    private bool _hostWriteActive;
    private int _hostMapCount;

    public int SubmissionUseCount
    {
        get
        {
            lock (_sync)
            {
                return _submissionUseCount;
            }
        }
    }

    public bool HostWriteActive
    {
        get
        {
            lock (_sync)
            {
                return _hostWriteActive;
            }
        }
    }

    public int HostMapCount
    {
        get
        {
            lock (_sync)
            {
                return _hostMapCount;
            }
        }
    }

    public void BeginSubmissionUse()
    {
        lock (_sync)
        {
            if (_hostWriteActive || _hostMapCount != 0)
            {
                throw new NeoVeldridException(
                    "A Vulkan mappable resource cannot enter submission use while host access is active.");
            }

            _submissionUseCount = checked(_submissionUseCount + 1);
        }
    }

    public void EndSubmissionUse()
    {
        lock (_sync)
        {
            if (_submissionUseCount <= 0)
            {
                throw new NeoVeldridException(
                    "A Vulkan mappable resource submission use was released without a matching acquisition.");
            }

            _submissionUseCount--;
        }
    }

    public void BeginHostWrite(string resourceName)
    {
        lock (_sync)
        {
            if (_hostWriteActive)
            {
                throw new NeoVeldridException(
                    $"Vulkan resource '{DisplayName(resourceName)}' already has an active direct host write.");
            }

            if (_hostMapCount != 0)
            {
                throw new NeoVeldridException(
                    $"Direct host update of Vulkan resource '{DisplayName(resourceName)}' was rejected because " +
                    $"it has {_hostMapCount} active map(s).");
            }

            if (_submissionUseCount != 0)
            {
                throw new NeoVeldridException(
                    $"Direct host update of Vulkan resource '{DisplayName(resourceName)}' was rejected because " +
                    $"{_submissionUseCount} submitted GPU use(s) have not completed. Use a frame slot or " +
                    "a command-list update instead.");
            }

            _hostWriteActive = true;
        }
    }

    public void EndHostWrite()
    {
        lock (_sync)
        {
            if (!_hostWriteActive)
            {
                throw new NeoVeldridException(
                    "A Vulkan mappable resource host write was released without a matching acquisition.");
            }

            _hostWriteActive = false;
        }
    }

    public void BeginHostMap(string resourceName)
    {
        lock (_sync)
        {
            if (_hostWriteActive)
            {
                throw new NeoVeldridException(
                    $"Vulkan resource '{DisplayName(resourceName)}' cannot be mapped during a direct host write.");
            }
            if (_submissionUseCount != 0)
            {
                throw new NeoVeldridException(
                    $"Mapping Vulkan resource '{DisplayName(resourceName)}' was rejected because " +
                    $"{_submissionUseCount} submitted GPU use(s) have not completed.");
            }

            _hostMapCount = checked(_hostMapCount + 1);
        }
    }

    public void EndHostMap()
    {
        lock (_sync)
        {
            if (_hostMapCount <= 0)
            {
                throw new NeoVeldridException(
                    "A Vulkan mappable resource map was released without a matching acquisition.");
            }

            _hostMapCount--;
        }
    }

    private static string DisplayName(string resourceName) =>
        string.IsNullOrWhiteSpace(resourceName) ? "<unnamed>" : resourceName;
}
