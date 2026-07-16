#if TEST_VULKAN
using System;
using System.Threading;
using System.Threading.Tasks;
using NeoVeldrid.Vk;

namespace NeoVeldrid.Tests;

internal sealed class VulkanAutomaticSubmissionReclamationGate :
    VkGraphicsDevice.IAutomaticSubmissionReclamationGate,
    IDisposable
{
    private VkGraphicsDevice _graphicsDevice;
    private int _isOpen;

    public bool IsReclamationAllowed => Volatile.Read(ref _isOpen) != 0;

    public VulkanAutomaticSubmissionReclamationGate(
        VkGraphicsDevice graphicsDevice)
    {
        _graphicsDevice = graphicsDevice ??
            throw new ArgumentNullException(nameof(graphicsDevice));
        if (graphicsDevice.AutomaticSubmissionReclamationGate is not null)
        {
            throw new InvalidOperationException(
                "An automatic submission-reclamation gate is already installed.");
        }

        graphicsDevice.AutomaticSubmissionReclamationGate = this;
    }

    public void Open()
    {
        VkGraphicsDevice graphicsDevice = _graphicsDevice;
        if (graphicsDevice is null)
            return;

        Volatile.Write(ref _isOpen, 1);
        if (ReferenceEquals(
            graphicsDevice.AutomaticSubmissionReclamationGate,
            this))
        {
            graphicsDevice.AutomaticSubmissionReclamationGate = null;
        }

        _graphicsDevice = null;
    }

    public void Dispose() => Open();
}

internal readonly struct VulkanSubmissionWraparoundObservation
{
    public bool WaitWasObserved { get; }
    public bool BeginCompletedWhilePaused { get; }
    public int TrackedSubmissionCountWhilePaused { get; }
    public uint RecordingSubmissionSlot { get; }

    public VulkanSubmissionWraparoundObservation(
        bool waitWasObserved,
        bool beginCompletedWhilePaused,
        int trackedSubmissionCountWhilePaused,
        uint recordingSubmissionSlot)
    {
        WaitWasObserved = waitWasObserved;
        BeginCompletedWhilePaused = beginCompletedWhilePaused;
        TrackedSubmissionCountWhilePaused = trackedSubmissionCountWhilePaused;
        RecordingSubmissionSlot = recordingSubmissionSlot;
    }
}

internal static class VulkanSubmissionWraparoundProbe
{
    public static VulkanSubmissionWraparoundObservation BeginAfterCapacityReached(
        VkGraphicsDevice graphicsDevice,
        CommandList commandList,
        VulkanAutomaticSubmissionReclamationGate reclamationGate)
    {
        using var observer = new VulkanBlockingSubmissionFenceWaitObserver();
        graphicsDevice.SubmissionFenceWaitObserver = observer;

        Task<uint> beginTask = Task.Run(() =>
        {
            commandList.Begin();
            return commandList.RecordingSubmissionSlot;
        });
        bool waitWasObserved = false;
        bool beginCompletedWhilePaused = false;
        int trackedSubmissionCountWhilePaused = -1;
        uint recordingSubmissionSlot;
        try
        {
            waitWasObserved = observer.WaitUntilEntered(TimeSpan.FromSeconds(10));
            beginCompletedWhilePaused = beginTask.IsCompleted;
            trackedSubmissionCountWhilePaused = graphicsDevice
                .CaptureSubmissionResourcePoolSnapshot()
                .TrackedSubmissionCount;
        }
        finally
        {
            reclamationGate.Open();
            observer.Release();
            try
            {
                recordingSubmissionSlot = beginTask.GetAwaiter().GetResult();
            }
            finally
            {
                graphicsDevice.SubmissionFenceWaitObserver = null;
            }
        }

        return new VulkanSubmissionWraparoundObservation(
            waitWasObserved,
            beginCompletedWhilePaused,
            trackedSubmissionCountWhilePaused,
            recordingSubmissionSlot);
    }
}

internal sealed class VulkanBlockingSubmissionFenceWaitObserver
    : VkGraphicsDevice.ISubmissionFenceWaitObserver, IDisposable
{
    private readonly ManualResetEventSlim _entered = new ManualResetEventSlim(false);
    private readonly ManualResetEventSlim _release = new ManualResetEventSlim(false);

    public void BeforeWait()
    {
        _entered.Set();
        _release.Wait();
    }

    public bool WaitUntilEntered(TimeSpan timeout)
        => _entered.Wait(timeout);

    public void Release()
        => _release.Set();

    public void Dispose()
    {
        _release.Set();
        _entered.Dispose();
        _release.Dispose();
    }
}
#endif
